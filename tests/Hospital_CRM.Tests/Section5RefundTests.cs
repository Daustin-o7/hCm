using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section5RefundTests
{
    private static HospitalCrmDbContext CreateInMemoryDb(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static InvoicesController CreateController(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role = "ClinicAdmin")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new InvoicesController(db, config, new RazorpayOrderClient(new HttpClient(), config));

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

    private static async Task<(Invoice invoice, User admin1, User admin2)> SeedPaidInvoiceAsync(HospitalCrmDbContext db, Guid tenantId, decimal paidAmount = 5000m)
    {
        var admin1 = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = "admin1@clinic.in",
            PasswordHash = "hash",
            Name = "Admin One",
            Role = UserRole.ClinicAdmin
        };

        var admin2 = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = "admin2@clinic.in",
            PasswordHash = "hash",
            Name = "Admin Two",
            Role = UserRole.ClinicAdmin
        };

        db.Users.AddRange(admin1, admin2);

        var taxProfile = new TenantTaxProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ThresholdRefundApproval = 1000m
        };
        db.TenantTaxProfiles.Add(taxProfile);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FinancialYear = "2026-27",
            Series = "INV",
            InvoiceNumber = 1,
            Status = InvoiceStatus.Paid,
            Subtotal = paidAmount,
            Total = paidAmount,
            PaidAmount = paidAmount,
            BalanceDue = 0m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Amount = paidAmount,
            Method = PaymentMethod.UPI,
            Status = PaymentStatus.Completed,
            VerificationStatus = PaymentVerificationStatus.Confirmed,
            ConfirmedByUserId = admin1.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        invoice.Payments.Add(payment);
        db.Invoices.Add(invoice);

        await db.SaveChangesAsync();
        return (invoice, admin1, admin2);
    }

    [Fact]
    public async Task ProcessRefund_UnderThreshold_DirectlyProcessesAndReversesLedger()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, _) = await SeedPaidInvoiceAsync(db, tenantId, 2000m);

        var controller = CreateController(db, tenantId, admin1.Id);

        // Refund ₹500 (threshold is ₹1,000)
        var req = new CreateRefundRequest(
            Amount: 500m,
            Method: PaymentMethod.UPI,
            RefundReference: "UPI-REF-001234",
            Reason: "Partial cancellation of lab test", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "Cancellation waiver"
        );

        var res = await controller.ProcessRefund(invoice.Id, req, CancellationToken.None);
        var obj = Assert.IsType<ObjectResult>(res);
        Assert.Equal(201, obj.StatusCode);

        // Verify invoice balance updated
        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(1500m, updatedInvoice.PaidAmount);
        Assert.Equal(500m, updatedInvoice.BalanceDue);
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice.Status);

        // Verify refund payment record created with negative amount
        var refundPayment = await db.Payments.FirstOrDefaultAsync(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.Refunded);
        Assert.NotNull(refundPayment);
        Assert.Equal(-500m, refundPayment.Amount);
        Assert.Equal(PaymentVerificationStatus.Confirmed, refundPayment.VerificationStatus);
        Assert.Equal("UPI-REF-001234", refundPayment.RefundReference);

        // Verify append-only ledger reversal entries posted
        var ledgerEntries = await db.FinanceLedgerEntries.Where(l => l.EntryType == FinanceEntryType.RefundProcessed).ToListAsync();
        Assert.Equal(2, ledgerEntries.Count);
        Assert.Contains(ledgerEntries, e => e.AccountCategory == "Bank" && e.CreditAmount == 500m);
        Assert.Contains(ledgerEntries, e => e.AccountCategory == "AccountsReceivable" && e.DebitAmount == 500m);
    }

    [Fact]
    public async Task ProcessRefund_ExceedingThreshold_RequiresDualApproval_ReturnsPending202()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, _) = await SeedPaidInvoiceAsync(db, tenantId, 5000m);

        var controller = CreateController(db, tenantId, admin1.Id);

        // Refund ₹2,500 (exceeds ₹1,000 threshold) without second approver
        var req = new CreateRefundRequest(
            Amount: 2500m,
            Method: PaymentMethod.NetBanking,
            RefundReference: "NEFT-REF-998877",
            Reason: "Surgery cancellation fee refund", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "Procedure cancellation waiver"
        );

        var res = await controller.ProcessRefund(invoice.Id, req, CancellationToken.None);
        var accepted = Assert.IsType<ObjectResult>(res);
        Assert.Equal(202, accepted.StatusCode);

        // Verify invoice balance not altered yet
        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(5000m, updatedInvoice.PaidAmount);
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice.Status);

        // Verify pending payment created
        var pendingPayment = await db.Payments.FirstOrDefaultAsync(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.Pending);
        Assert.NotNull(pendingPayment);
        Assert.Equal(-2500m, pendingPayment.Amount);
        Assert.Equal(PaymentVerificationStatus.Pending, pendingPayment.VerificationStatus);

        // Ledger entries must not be posted while pending
        var ledgerEntries = await db.FinanceLedgerEntries.Where(l => l.EntryType == FinanceEntryType.RefundProcessed).ToListAsync();
        Assert.Empty(ledgerEntries);
    }

    [Fact]
    public void CreateRefundRequest_CannotNameASecondApprover()
    {
        // The approver is the authenticated caller of /approve; letting the requester name one in the body
        // collapses dual control into a single request.
        Assert.Null(typeof(CreateRefundRequest).GetProperty("SecondApprovedByUserId"));
    }

    [Fact]
    public async Task ProcessRefund_AboveThreshold_AlwaysPending_EvenWhenBodyNamesASecondApprover()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, admin2) = await SeedPaidInvoiceAsync(db, tenantId, 3000m);

        var controller = CreateController(db, tenantId, admin1.Id);

        // The caller names a colleague in the body; the approver is whoever later calls /approve, never the body.
        var req = new CreateRefundRequest(
            Amount: 2000m,
            Method: PaymentMethod.Cash,
            RefundReference: "CASH-REF-01",
            Reason: "Refund attempt", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "Goodwill refund waiver"
        );

        var res = await controller.ProcessRefund(invoice.Id, req, CancellationToken.None);
        var accepted = Assert.IsType<ObjectResult>(res);
        Assert.Equal(202, accepted.StatusCode);

        var pending = await db.Payments.FirstAsync(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.Pending);
        Assert.Null(pending.SecondApprovedByUserId);
        Assert.Equal(admin1.Id, pending.ConfirmedByUserId);
        Assert.NotEqual(admin2.Id, pending.SecondApprovedByUserId);

        // Only the distinct authenticated approver completes it.
        var approverController = CreateController(db, tenantId, admin2.Id);
        var approveRes = await approverController.ApproveRefund(
            invoice.Id, pending.Id, new ApproveRefundRequest("Verified"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(approveRes);
        Assert.Equal(admin2.Id, pending.SecondApprovedByUserId);
    }

    [Fact]
    public async Task ApproveRefund_SecondApprover_SuccessfullyCompletesAndReversesLedger()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, admin2) = await SeedPaidInvoiceAsync(db, tenantId, 4000m);

        // Initiator (admin1) creates pending refund for ₹2000
        var controller1 = CreateController(db, tenantId, admin1.Id);
        var req = new CreateRefundRequest(
            Amount: 2000m,
            Method: PaymentMethod.UPI,
            RefundReference: "UPI-DUAL-REF-1",
            Reason: "Procedure cancellation", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "Procedure cancellation waiver"
        );
        var res1 = await controller1.ProcessRefund(invoice.Id, req, CancellationToken.None);
        var accepted = Assert.IsType<ObjectResult>(res1);
        Assert.Equal(202, accepted.StatusCode);

        var pendingPayment = await db.Payments.FirstAsync(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.Pending);

        // 1. Initiator cannot approve own refund
        var selfApproveRes = await controller1.ApproveRefund(invoice.Id, pendingPayment.Id, null, CancellationToken.None);
        var badSelf = Assert.IsType<BadRequestObjectResult>(selfApproveRes);
        Assert.Contains("dual_approval_self_approval_blocked", badSelf.Value?.ToString() ?? "");

        // 2. Distinct second approver (admin2) approves
        var controller2 = CreateController(db, tenantId, admin2.Id);
        var approveRes = await controller2.ApproveRefund(invoice.Id, pendingPayment.Id, new ApproveRefundRequest("Approved after doctor verification"), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(approveRes);

        // Verify status and dual approver recorded
        var finalPayment = await db.Payments.FindAsync(pendingPayment.Id);
        Assert.NotNull(finalPayment);
        Assert.Equal(PaymentStatus.Refunded, finalPayment.Status);
        Assert.Equal(PaymentVerificationStatus.Confirmed, finalPayment.VerificationStatus);
        Assert.Equal(admin2.Id, finalPayment.SecondApprovedByUserId);

        // Verify invoice balance adjusted
        var updatedInvoice = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(2000m, updatedInvoice.PaidAmount);
        Assert.Equal(2000m, updatedInvoice.BalanceDue);
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice.Status);

        // Verify ledger entries posted
        var ledgerEntries = await db.FinanceLedgerEntries.Where(l => l.EntryType == FinanceEntryType.RefundProcessed).ToListAsync();
        Assert.Equal(2, ledgerEntries.Count);
    }

    [Fact]
    public async Task ProcessRefund_CountsPendingRefundsAgainstTheRefundableBalance()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, _) = await SeedPaidInvoiceAsync(db, tenantId, 3000m);
        var controller = CreateController(db, tenantId, admin1.Id);

        var first = await controller.ProcessRefund(invoice.Id, new CreateRefundRequest(
            Amount: 2000m, Method: PaymentMethod.UPI, RefundReference: "REF-A", Reason: "First request", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "First request non-consideration"), CancellationToken.None);
        Assert.Equal(202, Assert.IsType<ObjectResult>(first).StatusCode);

        // Only ₹1,000 is still uncommitted, so a second ₹2,000 request must not be accepted as pending too.
        var second = await controller.ProcessRefund(invoice.Id, new CreateRefundRequest(
            Amount: 2000m, Method: PaymentMethod.UPI, RefundReference: "REF-B", Reason: "Second request", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "Second request non-consideration"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(second);
        Assert.Contains("refund_exceeds_paid_balance", bad.Value?.ToString() ?? "");
        Assert.Single(db.Payments.Where(p => p.InvoiceId == invoice.Id && p.Status == PaymentStatus.Pending));
    }

    [Fact]
    public async Task ApproveRefund_RechecksRefundableBalanceAndCannotOverdrawTheInvoice()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, admin2) = await SeedPaidInvoiceAsync(db, tenantId, 3000m);

        // Two requests slipped past creation (e.g. raised concurrently); approval must still refuse the overdraw.
        var first = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.UPI,
            Amount = -2000m,
            Status = PaymentStatus.Pending,
            VerificationStatus = PaymentVerificationStatus.Pending,
            ConfirmedByUserId = admin1.Id,
            RefundReference = "REF-1",
            RefundReason = "Pending one",
            RefundNonConsiderationReason = "Goodwill adjustment for pending one",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var second = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.UPI,
            Amount = -2000m,
            Status = PaymentStatus.Pending,
            VerificationStatus = PaymentVerificationStatus.Pending,
            ConfirmedByUserId = admin1.Id,
            RefundReference = "REF-2",
            RefundReason = "Pending two",
            RefundNonConsiderationReason = "Goodwill adjustment for pending two",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Payments.AddRange(first, second);
        await db.SaveChangesAsync();

        var approver = CreateController(db, tenantId, admin2.Id);
        Assert.IsType<OkObjectResult>(await approver.ApproveRefund(invoice.Id, first.Id, null, CancellationToken.None));

        var secondRes = await approver.ApproveRefund(invoice.Id, second.Id, null, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(secondRes);
        Assert.Contains("refund_exceeds_paid_balance", bad.Value?.ToString() ?? "");

        var reloaded = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(1000m, reloaded.PaidAmount);
        Assert.True(reloaded.BalanceDue >= 0m);
    }

    [Fact]
    public async Task ProcessRefund_ExceedingPaidAmount_Blocked()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var (invoice, admin1, _) = await SeedPaidInvoiceAsync(db, tenantId, 1000m);

        var controller = CreateController(db, tenantId, admin1.Id);

        // Attempting to refund ₹1,500 when only ₹1,000 was paid
        var req = new CreateRefundRequest(
            Amount: 1500m,
            Method: PaymentMethod.Cash,
            RefundReference: "CASH-REF-02",
            Reason: "Excessive refund", IdempotencyKey: $"IDEMP-REF-{Guid.NewGuid()}", RefundNonConsiderationReason: "Excessive refund waiver"
        );

        var res = await controller.ProcessRefund(invoice.Id, req, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("refund_exceeds_paid_balance", bad.Value?.ToString() ?? "");
    }
}
