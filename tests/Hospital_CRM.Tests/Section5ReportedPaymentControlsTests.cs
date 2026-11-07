using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section5ReportedPaymentControlsTests
{
    private static JsonDocument ToJsonDoc(object? value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static HospitalCrmDbContext CreateInMemoryDb(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static PaymentsController CreateController(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role = "Receptionist")
    {
        var controller = new PaymentsController(db);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "TestAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    private static Invoice CreateInvoice(Guid tenantId, decimal amount = 1000m)
    {
        return new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FinancialYear = "2026-27",
            Series = "INV",
            InvoiceNumber = 1,
            Status = InvoiceStatus.Finalized,
            Subtotal = amount,
            Total = amount,
            PaidAmount = 0m,
            BalanceDue = amount,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task ReportPayment_EnforcesClinicUnconfirmedCap_RejectsWhenCapExceeded()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        // Clinic tax profile allows max 3 pending unconfirmed reported payments
        var taxProfile = new TenantTaxProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaxUnconfirmedReportedPaymentsCount = 3,
            MaxUnconfirmedReportedPaymentsAmount = 10000m
        };
        db.TenantTaxProfiles.Add(taxProfile);

        // Pre-seed 3 pending reported payments
        for (int i = 0; i < 3; i++)
        {
            var inv = CreateInvoice(tenantId, 500m);
            db.Invoices.Add(inv);
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                InvoiceId = inv.Id,
                Method = PaymentMethod.UPI,
                Amount = 500m,
                Status = PaymentStatus.Pending,
                VerificationStatus = PaymentVerificationStatus.Reported,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        // Attempting to report 4th payment exceeds count cap (3)
        var newInvoice = CreateInvoice(tenantId, 500m);
        db.Invoices.Add(newInvoice);
        await db.SaveChangesAsync();

        var req = new ReportPaymentRequest("upi", 500m);
        var res = await controller.ReportPayment(newInvoice.Id, req, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("reported_payments_cap_exceeded", bad.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task ConfirmPayment_DetectsStaffConfirmationSpike_LogsAuditAndAlerts()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        // Configured spike threshold: 5 confirmations within 1 hour
        var taxProfile = new TenantTaxProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StaffConfirmationSpikeThreshold = 5
        };
        db.TenantTaxProfiles.Add(taxProfile);

        // Seed 4 prior confirmed payments in the last 15 minutes by this receptionist
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 4; i++)
        {
            var inv = CreateInvoice(tenantId, 200m);
            db.Invoices.Add(inv);
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                InvoiceId = inv.Id,
                Method = PaymentMethod.Cash,
                Amount = 200m,
                Status = PaymentStatus.Completed,
                VerificationStatus = PaymentVerificationStatus.Confirmed,
                ConfirmedByUserId = receptionistId,
                ConfirmedAt = now.AddMinutes(-10),
                CreatedAt = now.AddMinutes(-10)
            });
        }
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        // 5th confirmation: triggers anomaly spike alert
        var invoice = CreateInvoice(tenantId, 300m);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var req = new ConfirmPaymentRequest("cash", 300m);
        var res = await controller.ConfirmPayment(invoice.Id, req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(res);
        Assert.NotNull(ok.Value);
        var doc = ToJsonDoc(ok.Value);
        Assert.True(doc.RootElement.GetProperty("anomalySpikeAlert").GetBoolean());

        // Verify security audit log record was added
        var auditLog = await db.AuthAuditLogs
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.EventType == "payment_confirmation_spike_detected");
        Assert.NotNull(auditLog);
        Assert.Equal(receptionistId, auditLog.UserId);
        Assert.Contains("Staff member confirmed 5 payments", auditLog.Detail);
    }

    [Fact]
    public async Task GenerateDynamicQr_EnforcesPayeeDailyLimit_FallsBackToBackupWhenPrimaryExhausted()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        db.Clinics.Add(new Clinic { Id = clinicId, TenantId = tenantId, Name = "City Clinic" });

        // Seed Primary payee account with DailyLimit = ₹5,000
        var primaryAccount = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PriorityOrder = 1,
            Vpa = "primary@okhdfcbank",
            AccountHolderName = "City Clinic Primary",
            ClinicId = clinicId,
            DailyLimit = 5000m,
            IsActive = true
        };

        // Seed Backup payee account with DailyLimit = ₹25,000
        var backupAccount = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PriorityOrder = 2,
            Vpa = "backup@okaxisbank",
            AccountHolderName = "City Clinic Backup",
            ClinicId = clinicId,
            DailyLimit = 25000m,
            IsActive = true
        };

        db.PayeeAccounts.AddRange(primaryAccount, backupAccount);

        // Seed ₹4,500 already collected on Primary account today
        var invPast = CreateInvoice(tenantId, 4500m);
        db.Invoices.Add(invPast);
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invPast.Id,
            PayeeAccountId = primaryAccount.Id,
            Amount = 4500m,
            Method = PaymentMethod.UPI,
            Status = PaymentStatus.Completed,
            VerificationStatus = PaymentVerificationStatus.Confirmed,
            ConfirmedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        // New invoice of ₹1,000 (would bring Primary to ₹5,500 > ₹5,000 limit)
        var newInvoice = CreateInvoice(tenantId, 1000m);
        db.Invoices.Add(newInvoice);
        await db.SaveChangesAsync();

        // Calling GenerateDynamicQr without specifying backup
        var res = await controller.GenerateDynamicQr(newInvoice.Id, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);

        var qrDoc = ToJsonDoc(ok.Value);
        // Must automatically fall back to backup account
        Assert.Equal(backupAccount.Id, qrDoc.RootElement.GetProperty("account").GetProperty("id").GetGuid());
        Assert.True(qrDoc.RootElement.GetProperty("account").GetProperty("isBackup").GetBoolean());
        Assert.Equal("backup@okaxisbank", qrDoc.RootElement.GetProperty("account").GetProperty("vpa").GetString());
    }

    [Fact]
    public async Task GenerateDynamicQr_Returns503_WhenAllPayeeAccountsExceededDailyLimit()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        db.Clinics.Add(new Clinic { Id = clinicId, TenantId = tenantId, Name = "City Clinic" });

        // Seed primary with limit ₹1,000 already consumed
        var primaryAccount = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PriorityOrder = 1,
            Vpa = "primary@okhdfcbank",
            DailyLimit = 1000m,
            ClinicId = clinicId,
            IsActive = true
        };
        db.PayeeAccounts.Add(primaryAccount);

        var invPast = CreateInvoice(tenantId, 1000m);
        db.Invoices.Add(invPast);
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invPast.Id,
            PayeeAccountId = primaryAccount.Id,
            Amount = 1000m,
            Method = PaymentMethod.UPI,
            Status = PaymentStatus.Completed,
            VerificationStatus = PaymentVerificationStatus.Confirmed,
            ConfirmedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        var newInvoice = CreateInvoice(tenantId, 500m);
        db.Invoices.Add(newInvoice);
        await db.SaveChangesAsync();

        var res = await controller.GenerateDynamicQr(newInvoice.Id, null, CancellationToken.None);
        var obj = Assert.IsType<ObjectResult>(res);
        Assert.Equal(503, obj.StatusCode);
        Assert.Contains("payee_daily_limit_exceeded", obj.Value?.ToString() ?? "");
    }

    [Theory]
    [InlineData(PaymentStatus.Pending, PaymentVerificationStatus.Confirmed)]
    [InlineData(PaymentStatus.Refunded, PaymentVerificationStatus.Confirmed)]
    [InlineData(PaymentStatus.Failed, PaymentVerificationStatus.Reported)]
    public async Task ConfirmPayment_RejectsRowsThatWereNeverReported(PaymentStatus status, PaymentVerificationStatus verification)
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        var invoice = CreateInvoice(tenantId, 1000m);
        db.Invoices.Add(invoice);
        var row = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.Cash,
            Amount = 1000m,
            Status = status,
            VerificationStatus = verification,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Payments.Add(row);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);
        var req = new ConfirmPaymentRequest("cash", 1000m, PaymentId: row.Id);
        var res = await controller.ConfirmPayment(invoice.Id, req, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("payment_not_confirmable", bad.Value?.ToString() ?? "");

        var reloaded = await db.Invoices.Include(i => i.Payments).FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(0m, reloaded.PaidAmount);
        Assert.DoesNotContain(reloaded.Payments, p => p.Status == PaymentStatus.Completed);
    }
}
