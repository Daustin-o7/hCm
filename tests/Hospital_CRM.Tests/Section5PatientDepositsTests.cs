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

public class Section5PatientDepositsTests
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

    private static PatientDepositsController CreateController(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role = "Receptionist")
    {
        var controller = new PatientDepositsController(db);

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

    private static (Patient patient, Appointment appointment, Invoice invoice) SeedPatientAndInvoice(HospitalCrmDbContext db, Guid tenantId, decimal invoiceTotal = 1500m)
    {
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Sunita Rao",
            Phone = "9876543210"
        };
        db.Patients.Add(patient);

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PatientId = patient.Id,
            DoctorId = Guid.NewGuid(),
            Status = AppointmentStatus.Completed
        };
        db.Appointments.Add(appointment);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment.Id,
            FinancialYear = "2026-27",
            Series = "INV",
            InvoiceNumber = 1,
            Status = InvoiceStatus.Finalized,
            Subtotal = invoiceTotal,
            Total = invoiceTotal,
            PaidAmount = 0m,
            BalanceDue = invoiceTotal,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Invoices.Add(invoice);

        return (patient, appointment, invoice);
    }

    [Fact]
    public async Task CollectDeposit_AssignsGaplessIndianFySequence_AndPostsDoubleEntryLedger()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        var (patient, _, _) = SeedPatientAndInvoice(db, tenantId);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        // Deposit 1: ₹5,000 via UPI
        var req1 = new CreatePatientDepositRequest(
            Amount: 5000m,
            Method: PaymentMethod.UPI,
            ReferenceNumber: "UPI-DEP-998811",
            Notes: "Advance for planned surgery",
            IdempotencyKey: $"DEP-{Guid.NewGuid():N}"
        );
        var res1 = await controller.CollectDeposit(patient.Id, req1, CancellationToken.None);
        var obj1 = Assert.IsType<ObjectResult>(res1);
        Assert.Equal(201, obj1.StatusCode);

        var doc1 = ToJsonDoc(obj1.Value);
        Assert.Equal(5000m, doc1.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(5000m, doc1.RootElement.GetProperty("balance").GetDecimal());
        Assert.Equal("DEP-2026-27-000001", doc1.RootElement.GetProperty("receiptNumber").GetString());
        Assert.Equal("active", doc1.RootElement.GetProperty("status").GetString());

        // Deposit 2: ₹3,000 via Cash (Gapless sequence 2)
        var req2 = new CreatePatientDepositRequest(
            Amount: 3000m,
            Method: PaymentMethod.Cash,
            Notes: "Second cash advance",
            IdempotencyKey: $"DEP-{Guid.NewGuid():N}"
        );
        var res2 = await controller.CollectDeposit(patient.Id, req2, CancellationToken.None);
        var obj2 = Assert.IsType<ObjectResult>(res2);
        Assert.Equal(201, obj2.StatusCode);

        var doc2 = ToJsonDoc(obj2.Value);
        Assert.Equal("DEP-2026-27-000002", doc2.RootElement.GetProperty("receiptNumber").GetString());

        // Verify ledger postings: 2 entries per deposit (Debit Bank/Cash, Credit PatientDeposits)
        var ledgerEntries = await db.FinanceLedgerEntries
            .Where(l => l.EntryType == FinanceEntryType.DepositCollected)
            .ToListAsync();
        Assert.Equal(4, ledgerEntries.Count);

        // Deposit 1 ledger check
        Assert.Contains(ledgerEntries, l => l.ReferenceNumber == "DEP-2026-27-000001" && l.AccountCategory == "Bank" && l.DebitAmount == 5000m);
        Assert.Contains(ledgerEntries, l => l.ReferenceNumber == "DEP-2026-27-000001" && l.AccountCategory == "PatientDeposits" && l.CreditAmount == 5000m);

        // Deposit 2 ledger check
        Assert.Contains(ledgerEntries, l => l.ReferenceNumber == "DEP-2026-27-000002" && l.AccountCategory == "Cash" && l.DebitAmount == 3000m);
        Assert.Contains(ledgerEntries, l => l.ReferenceNumber == "DEP-2026-27-000002" && l.AccountCategory == "PatientDeposits" && l.CreditAmount == 3000m);
    }

    [Fact]
    public async Task GetPatientDeposits_ReturnsAllDepositsAndAvailableBalance()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        var (patient, _, _) = SeedPatientAndInvoice(db, tenantId);
        db.PatientDeposits.AddRange(
            new PatientDeposit
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PatientId = patient.Id,
                ReceiptNumber = "DEP-2026-27-000001",
                Amount = 2000m,
                AllocatedAmount = 500m,
                Balance = 1500m,
                Method = PaymentMethod.UPI,
                Status = PatientDepositStatus.Active
            },
            new PatientDeposit
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PatientId = patient.Id,
                ReceiptNumber = "DEP-2026-27-000002",
                Amount = 1000m,
                AllocatedAmount = 1000m,
                Balance = 0m,
                Method = PaymentMethod.Cash,
                Status = PatientDepositStatus.FullyAllocated
            }
        );
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);
        var res = await controller.GetPatientDeposits(patient.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);

        var doc = ToJsonDoc(ok.Value);
        Assert.Equal(3000m, doc.RootElement.GetProperty("totalDeposited").GetDecimal());
        Assert.Equal(1500m, doc.RootElement.GetProperty("totalAllocated").GetDecimal());
        Assert.Equal(1500m, doc.RootElement.GetProperty("availableBalance").GetDecimal());
        Assert.Equal(2, doc.RootElement.GetProperty("deposits").GetArrayLength());
    }

    [Fact]
    public async Task ApplyAdvanceToInvoice_AllocatesFifo_UpdatesInvoiceBalance_AndPostsLedgerReversals()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        var (patient, _, invoice) = SeedPatientAndInvoice(db, tenantId, invoiceTotal: 2500m);

        // Seed Deposit 1: ₹1,000 available
        var dep1 = new PatientDeposit
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PatientId = patient.Id,
            ReceiptNumber = "DEP-2026-27-000001",
            FinancialYear = "2026-27",
            SequenceNumber = 1,
            Amount = 1000m,
            AllocatedAmount = 0m,
            Balance = 1000m,
            Method = PaymentMethod.UPI,
            Status = PatientDepositStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };

        // Seed Deposit 2: ₹2,000 available
        var dep2 = new PatientDeposit
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PatientId = patient.Id,
            ReceiptNumber = "DEP-2026-27-000002",
            FinancialYear = "2026-27",
            SequenceNumber = 2,
            Amount = 2000m,
            AllocatedAmount = 0m,
            Balance = 2000m,
            Method = PaymentMethod.Cash,
            Status = PatientDepositStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        db.PatientDeposits.AddRange(dep1, dep2);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        // Apply full invoice balance (₹2,500) from patient advance
        // FIFO must consume dep1 (₹1,000 fully) and ₹1,500 from dep2 (leaving ₹500 balance)
        var res = await controller.ApplyAdvanceToInvoice(invoice.Id, new ApplyAdvanceRequest(2500m, IdempotencyKey: $"ADV-{Guid.NewGuid():N}"), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);

        var doc = ToJsonDoc(ok.Value);
        Assert.Equal(2500m, doc.RootElement.GetProperty("allocatedAdvanceAmount").GetDecimal());
        Assert.Equal(2500m, doc.RootElement.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(0m, doc.RootElement.GetProperty("balanceDue").GetDecimal());
        Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

        // Verify deposit 1 status & balance
        var updatedDep1 = await db.PatientDeposits.FindAsync(dep1.Id);
        Assert.NotNull(updatedDep1);
        Assert.Equal(1000m, updatedDep1.AllocatedAmount);
        Assert.Equal(0m, updatedDep1.Balance);
        Assert.Equal(PatientDepositStatus.FullyAllocated, updatedDep1.Status);

        // Verify deposit 2 status & balance
        var updatedDep2 = await db.PatientDeposits.FindAsync(dep2.Id);
        Assert.NotNull(updatedDep2);
        Assert.Equal(1500m, updatedDep2.AllocatedAmount);
        Assert.Equal(500m, updatedDep2.Balance);
        Assert.Equal(PatientDepositStatus.Active, updatedDep2.Status);

        // Verify allocations records
        var allocations = await db.PatientDepositAllocations.Where(a => a.InvoiceId == invoice.Id).ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Contains(allocations, a => a.PatientDepositId == dep1.Id && a.AllocatedAmount == 1000m);
        Assert.Contains(allocations, a => a.PatientDepositId == dep2.Id && a.AllocatedAmount == 1500m);

        // Verify ledger postings: Debit PatientDeposits ₹2,500, Credit AccountsReceivable ₹2,500
        var ledgerEntries = await db.FinanceLedgerEntries
            .Where(l => l.EntryType == FinanceEntryType.DepositApplied)
            .ToListAsync();
        Assert.Equal(2, ledgerEntries.Count);
        Assert.Contains(ledgerEntries, l => l.AccountCategory == "PatientDeposits" && l.DebitAmount == 2500m);
        Assert.Contains(ledgerEntries, l => l.AccountCategory == "AccountsReceivable" && l.CreditAmount == 2500m);
    }

    [Fact]
    public async Task ApplyAdvanceToInvoice_RejectsWhenExceedingBalanceDueOrAvailableAdvance()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var receptionistId = Guid.NewGuid();

        var (patient, _, invoice) = SeedPatientAndInvoice(db, tenantId, invoiceTotal: 1000m);

        // Seed Deposit of only ₹400
        db.PatientDeposits.Add(new PatientDeposit
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PatientId = patient.Id,
            ReceiptNumber = "DEP-2026-27-000001",
            Amount = 400m,
            Balance = 400m,
            Method = PaymentMethod.Cash,
            Status = PatientDepositStatus.Active
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, receptionistId);

        // Requesting ₹800 exceeds available advance of ₹400
        var res1 = await controller.ApplyAdvanceToInvoice(invoice.Id, new ApplyAdvanceRequest(800m, IdempotencyKey: $"ADV-{Guid.NewGuid():N}"), CancellationToken.None);
        var bad1 = Assert.IsType<BadRequestObjectResult>(res1);
        Assert.Contains("amount_exceeds_available_advance", bad1.Value?.ToString() ?? "");

        // Requesting ₹1,500 exceeds invoice balance due of ₹1,000
        var res2 = await controller.ApplyAdvanceToInvoice(invoice.Id, new ApplyAdvanceRequest(1500m, IdempotencyKey: $"ADV-{Guid.NewGuid():N}"), CancellationToken.None);
        var bad2 = Assert.IsType<BadRequestObjectResult>(res2);
        Assert.Contains("amount_exceeds_balance", bad2.Value?.ToString() ?? "");
    }
}
