using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1PaymentSafetyTests(Part2TenantBoundaryPostgresFixture fixture)
    : IClassFixture<Part2TenantBoundaryPostgresFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("INV-2026-27-000011", 40, 11)]
    [InlineData("Received for (INV-2026-27-000011).", 100, 11)]
    [InlineData("INV-2026-27-000099", 100, null)]
    [InlineData("INV-2026-27-000011X", 100, null)]
    [InlineData("INV-2026-27-000011 and INV-2026-27-000001", 100, null)]
    [InlineData("INV-2026-27-000011", 101, null)]
    [InlineData("INV-2026-27-000011", 0, null)]
    [InlineData("INV-2026-27-000011", -1, null)]
    [InlineData("INV-2026-27-000011", 1.001, null)]
    [InlineData(null, 100, null)]
    public async Task Matching_RequiresAnExactUnambiguousPayableReference(string? reference, double amount, int? expected)
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        db.Invoices.AddRange(Bill(tenant, 1, 100m), Bill(tenant, 11, 100m));
        await db.SaveChangesAsync(Ct);
        var matched = await Match(db, tenant, new("event", (decimal)amount, null, reference));
        Assert.Equal(expected, matched?.InvoiceNumber);
    }

    [Fact]
    public async Task ExplicitReferenceConflict_DoesNotPayOtherInvoice_OrUseOriginalTotal()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var intended = Bill(tenant, 11, 100m);
        intended.PaidAmount = 60m;
        intended.BalanceDue = 40m;
        intended.Status = InvoiceStatus.PartiallyPaid;
        intended.CreatedAt = DateTimeOffset.UtcNow.AddMonths(-2);
        db.Invoices.AddRange(intended, Bill(tenant, 1, 100m), Bill(Guid.NewGuid(), 11, 40m));
        await db.SaveChangesAsync(Ct);
        Assert.Null(await Match(db, tenant, new("overpayment", 100m, null, intended.FormatInvoiceNumber())));
        Assert.Equal(intended.Id, (await Match(db, tenant, new("remainder", 40m, null, intended.FormatInvoiceNumber())))?.Id);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, 10)]
    [InlineData(InvoiceStatus.Cancelled, 10)]
    [InlineData(InvoiceStatus.Refunded, 10)]
    [InlineData(InvoiceStatus.Paid, 10)]
    [InlineData(InvoiceStatus.Finalized, 101)]
    [InlineData(InvoiceStatus.Finalized, 0)]
    public async Task ManualMatchDenial_LeavesEventMoneyAndJournalUntouched(InvoiceStatus status, int amount)
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        invoice.Status = status;
        var evt = new ProcessedWebhookEvent { Id = Guid.NewGuid(), TenantId = tenant, Provider = "paytm", EventId = "manual", Amount = amount, Status = ProcessedWebhookEvent.Unmatched };
        db.Invoices.Add(invoice);
        db.ProcessedWebhookEvents.Add(evt);
        await db.SaveChangesAsync(Ct);
        var controller = Integrations(db);
        Caller(controller, Guid.NewGuid(), tenant, "ClinicAdmin");
        Assert.IsType<BadRequestObjectResult>(await controller.MatchUnmatched(evt.Id, new(invoice.Id), Ct));
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(ProcessedWebhookEvent.Unmatched, evt.Status);
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Fact]
    public async Task SignedPrimaryWebhook_ConfirmsPartialPaymentAndBalancesJournal_OnlyOnce()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 11, 100m);
        db.Invoices.Add(invoice);
        db.TenantIntegrations.Add(new TenantIntegration { Id = Guid.NewGuid(), TenantId = tenant, Provider = "paytm", Enabled = true, WebhookPathToken = "synthetic-token", SecretProtected = new TestSecretProtector().Protect("synthetic-secret") });
        await db.SaveChangesAsync(Ct);
        const string body = """{"txnId":"partial-event","amount":40,"status":"TXN_SUCCESS","reference":"INV-2026-27-000011","bankTxnId":"synthetic-UTR"}""";
        var controller = Integrations(db);
        Caller(controller, Guid.NewGuid(), Guid.NewGuid(), "ClinicAdmin");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            controller.Request.Headers["X-Paytm-Signature"] = PaymentWebhookCodec.SignHmac("synthetic-secret", body);
            Assert.IsType<OkObjectResult>(await controller.Receive("paytm", "synthetic-token", Ct));
        }
        var payment = await db.Payments.SingleAsync(Ct);
        Assert.Equal(tenant, payment.TenantId);
        Assert.Equal(40m, payment.Amount);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(PaymentVerificationStatus.Confirmed, payment.VerificationStatus);
        Assert.NotNull(payment.ConfirmedAt);
        Assert.Equal(40m, invoice.PaidAmount);
        Assert.Equal(60m, invoice.BalanceDue);
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
        var ledger = await db.FinanceLedgerEntries.ToListAsync(Ct);
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, e => Assert.Equal(tenant, e.TenantId));
        Assert.Equal(ledger.Sum(e => e.DebitAmount), ledger.Sum(e => e.CreditAmount));
        Assert.Single(db.ProcessedWebhookEvents);
    }

    [Fact]
    public void RazorpayCodec_RequiresCapturedInrPayment_AndPreservesRealId()
    {
        const string body = """{"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_real_test_id","order_id":"order_test_id","amount":12345,"currency":"INR","status":"captured","notes":{"receipt":"INV-2026-27-000011"}}}}}""";
        var parsed = PaymentWebhookCodec.TryParse("razorpay", body);
        Assert.NotNull(parsed);
        Assert.Equal(123.45m, parsed.Amount);
        Assert.Equal("pay_real_test_id", parsed.EventId);
        Assert.Null(PaymentWebhookCodec.TryParse("razorpay", body.Replace("captured", "authorized")));
        Assert.Null(PaymentWebhookCodec.TryParse("razorpay", body.Replace("INR", "USD")));
    }

    [Part2TenantBoundaryPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RazorpayCapture_BothRoutesReuseThePendingOrder_AndCreditOnlyOnce(bool primaryFirst)
    {
        var tenant = fixture.TenantA;
        var order = "order_" + Guid.NewGuid().ToString("N");
        var paymentId = "pay_" + Guid.NewGuid().ToString("N");
        var path = Guid.NewGuid().ToString("N");
        var invoice = Bill(tenant, primaryFirst ? 81 : 82, 100m);
        var pending = new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Method = PaymentMethod.Razorpay, Amount = 40m, RazorpayOrderId = order, Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Pending };
        await using (var seed = fixture.OwnerDb())
        {
            var integration = await seed.TenantIntegrations.SingleOrDefaultAsync(i => i.TenantId == tenant && i.Provider == "razorpay", Ct);
            if (integration is null)
                seed.TenantIntegrations.Add(new TenantIntegration { Id = Guid.NewGuid(), TenantId = tenant, Provider = "razorpay", Enabled = true, WebhookPathToken = path, SecretProtected = new TestSecretProtector().Protect("synthetic-secret") });
            else
                integration.WebhookPathToken = path;
            seed.AddRange(invoice, pending);
            await seed.SaveChangesAsync(Ct);
        }
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "payment.captured",
            payload = new { payment = new { entity = new { id = paymentId, order_id = order, status = "captured", currency = "INR", amount = 4000 } } }
        });
        var context = new TenantContext();
        using var originalScope = context.PushTenant(fixture.TenantB);
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync(Ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Razorpay:WebhookSecret"] = "synthetic-secret" }).Build();
        using var http = new HttpClient();
        var primary = new PaymentIntegrationsController(db, new TestSecretProtector(), config,
            NullLogger<PaymentIntegrationsController>.Instance, context)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var legacy = new InvoicesController(db, config, new RazorpayOrderClient(http, config), context)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var attempt = 0;
        foreach (var usePrimary in new[] { primaryFirst, !primaryFirst, primaryFirst })
        {
            var controller = usePrimary ? (ControllerBase)primary : legacy;
            controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            controller.Request.Headers["X-Razorpay-Signature"] = PaymentWebhookCodec.SignHmac("synthetic-secret", body);
            var result = Assert.IsType<OkObjectResult>(usePrimary ? await primary.Receive("razorpay", path, Ct) : await legacy.RazorpayWebhook(Ct));
            Assert.Equal(attempt++ == 0 ? "ok" : "duplicate", Json(result.Value).GetProperty("status").GetString());
            Assert.Equal(fixture.TenantB, context.CurrentTenantId);
            Assert.False(context.IsPlatformAdmin);
            Assert.Empty(await db.Payments.IgnoreQueryFilters().Where(p => p.Id == pending.Id).ToListAsync(Ct));
            db.ChangeTracker.Clear();
        }
        await using var verify = fixture.OwnerDb();
        var savedPayment = await verify.Payments.SingleAsync(p => p.InvoiceId == invoice.Id, Ct);
        Assert.Equal(pending.Id, savedPayment.Id);
        Assert.Equal(paymentId, savedPayment.RazorpayPaymentId);
        Assert.Equal(PaymentVerificationStatus.Confirmed, savedPayment.VerificationStatus);
        var savedInvoice = await verify.Invoices.SingleAsync(i => i.Id == invoice.Id, Ct);
        Assert.Equal(40m, savedInvoice.PaidAmount);
        Assert.Equal(60m, savedInvoice.BalanceDue);
        var journal = await verify.FinanceLedgerEntries.Where(e => e.ReferenceId == invoice.Id || e.ReferenceId == pending.Id).ToListAsync(Ct);
        Assert.Equal(2, journal.Count);
        Assert.All(journal, e => Assert.Equal(tenant, e.TenantId));
        Assert.Equal(40m, journal.Sum(e => e.DebitAmount));
        Assert.Equal(40m, journal.Sum(e => e.CreditAmount));
        Assert.Single(await verify.ProcessedWebhookEvents.Where(e => e.TenantId == tenant && e.Provider == "razorpay" && e.EventId == paymentId).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(5000, null)]
    [InlineData(4000, "INV-2026-27-000001")]
    public async Task RazorpayPendingOrder_AmountOrReferenceConflictStaysUnmatched(int paise, string? reference)
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var intended = Bill(tenant, 11, 100m);
        db.Invoices.AddRange(intended, Bill(tenant, 1, paise / 100m));
        db.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = intended.Id, Method = PaymentMethod.Razorpay, Amount = 40m, RazorpayOrderId = "order_test", Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Pending });
        await db.SaveChangesAsync(Ct);
        Assert.Null(await Match(db, tenant, new("pay_test", paise / 100m, null, reference, "pay_test", "order_test")));
        Assert.Equal(0m, intended.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task ManualMatch_ExplicitConflictingReferenceCannotPayAnotherBill()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        var evt = new ProcessedWebhookEvent { Id = Guid.NewGuid(), TenantId = tenant, Provider = "paytm", EventId = "conflict", Amount = 40m, Reference = "INV-2026-27-000011", Status = ProcessedWebhookEvent.Unmatched };
        db.Invoices.Add(invoice);
        db.ProcessedWebhookEvents.Add(evt);
        await db.SaveChangesAsync(Ct);
        var controller = Integrations(db);
        Caller(controller, Guid.NewGuid(), tenant, "ClinicAdmin");
        Assert.IsType<BadRequestObjectResult>(await controller.MatchUnmatched(evt.Id, new(invoice.Id), Ct));
        Assert.Equal(ProcessedWebhookEvent.Unmatched, evt.Status);
        Assert.Equal(0m, invoice.PaidAmount);
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task ManualMatch_ReplayReturnsOriginalPaymentWithoutCreditingAgain()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        var evt = new ProcessedWebhookEvent { Id = Guid.NewGuid(), TenantId = tenant, Provider = "paytm", EventId = "replay", Amount = 40m, Status = ProcessedWebhookEvent.Unmatched };
        db.Invoices.Add(invoice);
        db.ProcessedWebhookEvents.Add(evt);
        await db.SaveChangesAsync(Ct);
        var controller = Integrations(db);
        Caller(controller, Guid.NewGuid(), tenant, "ClinicAdmin");
        var first = Assert.IsType<OkObjectResult>(await controller.MatchUnmatched(evt.Id, new(invoice.Id), Ct));
        var second = Assert.IsType<OkObjectResult>(await controller.MatchUnmatched(evt.Id, new(invoice.Id), Ct));
        Assert.Equal(Json(first.Value).GetProperty("paymentId").GetGuid(), Json(second.Value).GetProperty("paymentId").GetGuid());
        Assert.True(Json(second.Value).GetProperty("alreadyMatched").GetBoolean());
        Assert.Equal(40m, invoice.PaidAmount);
        Assert.Single(db.Payments);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync(Ct));
    }

    [Fact]
    public async Task AmbiguousRazorpayOrder_CannotFallBackToExplicitInvoice()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        db.Invoices.Add(invoice);
        db.Payments.AddRange(Enumerable.Range(0, 2).Select(_ => new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Method = PaymentMethod.Razorpay, Amount = 40m, RazorpayOrderId = "order_ambiguous", Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Pending }));
        await db.SaveChangesAsync(Ct);
        Assert.Null(await Match(db, tenant, new("pay_test", 40m, null, invoice.FormatInvoiceNumber(), "pay_test", "order_ambiguous")));
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Fact]
    public async Task StaffConfirmation_UnknownPaymentIdCannotCreateReceiptOrSpikeAudit()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        db.Invoices.Add(invoice);
        db.TenantTaxProfiles.Add(new TenantTaxProfile { Id = Guid.NewGuid(), TenantId = tenant, StaffConfirmationSpikeThreshold = 1 });
        await db.SaveChangesAsync(Ct);
        var controller = new PaymentsController(db);
        Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        Assert.IsType<NotFoundObjectResult>(await controller.ConfirmPayment(invoice.Id, new("cash", 40m, PaymentId: Guid.NewGuid()), Ct));
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Empty(db.AuthAuditLogs);
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(50, false)]
    public async Task StaffConfirmation_UsesReportedAmountAndRejectsConflictingOverride(int requested, bool allowed)
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        var payment = new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Method = PaymentMethod.Cash, Amount = 40m, Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Reported };
        db.Invoices.Add(invoice);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(Ct);
        var controller = new PaymentsController(db);
        Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        var result = await controller.ConfirmPayment(invoice.Id, new("cash", requested, PaymentId: payment.Id), Ct);
        if (allowed)
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(40m, invoice.PaidAmount);
            Assert.Equal(60m, invoice.BalanceDue);
            Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync(Ct));
        }
        else
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(0m, invoice.PaidAmount);
            Assert.Empty(db.FinanceLedgerEntries);
        }
        Assert.Equal(40m, payment.Amount);
        Assert.Single(db.Payments);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.001)]
    public async Task StaffConfirmation_InvalidMoneyCannotBeRoundedOrDefaulted(double amount)
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(Ct);
        var controller = new PaymentsController(db);
        Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        Assert.IsType<BadRequestObjectResult>(await controller.ConfirmPayment(invoice.Id, new("cash", (decimal)amount), Ct));
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(0m, invoice.PaidAmount);
    }

    [Fact]
    public async Task StaffConfirmation_ReusedKeyWithDifferentAmountIsAConflict()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(Ct);
        var controller = new PaymentsController(db);
        Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        Assert.IsType<OkObjectResult>(await controller.ConfirmPayment(invoice.Id, new("cash", 40m, IdempotencyKey: "receipt-retry"), Ct));
        Assert.IsType<ConflictObjectResult>(await controller.ConfirmPayment(invoice.Id, new("cash", 50m, IdempotencyKey: "receipt-retry"), Ct));
        Assert.Equal(40m, invoice.PaidAmount);
        Assert.Single(db.Payments);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync(Ct));
    }

    [Fact]
    public async Task StaffConfirmation_CannotReplaceTheOriginalReportKey()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(Ct);
        var controller = new PaymentsController(db);
        Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        var report = new ReportPaymentRequest("upi", 40m, "UTR-original", "report-original");
        var reported = Assert.IsType<OkObjectResult>(await controller.ReportPayment(invoice.Id, report, Ct));
        var paymentId = Json(reported.Value).GetProperty("paymentId").GetGuid();
        Assert.IsType<ConflictObjectResult>(await controller.ConfirmPayment(invoice.Id,
            new("upi", PaymentId: paymentId, IdempotencyKey: "replacement-key"), Ct));
        Assert.Equal("report-original", (await db.Payments.SingleAsync(Ct)).IdempotencyKey);
        Assert.Equal(0m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);

        var confirmed = Assert.IsType<OkObjectResult>(await controller.ConfirmPayment(invoice.Id,
            new("upi", PaymentId: paymentId, IdempotencyKey: report.IdempotencyKey), Ct));
        var receipt = Json(confirmed.Value);
        Assert.Equal(40m, receipt.GetProperty("amount").GetDecimal());
        Assert.Equal("UPI", receipt.GetProperty("method").GetString());
        Assert.Equal("completed", receipt.GetProperty("paymentStatus").GetString());
        Assert.Equal("confirmed", receipt.GetProperty("verificationStatus").GetString());
        Assert.Equal(report.IdempotencyKey, receipt.GetProperty("idempotencyKey").GetString());
        var replay = Assert.IsType<OkObjectResult>(await controller.ReportPayment(invoice.Id, report, Ct));
        Assert.Equal(paymentId, Json(replay.Value).GetProperty("paymentId").GetGuid());
        Assert.Equal(40m, Json(replay.Value).GetProperty("amount").GetDecimal());
        Assert.Equal("completed", Json(replay.Value).GetProperty("paymentStatus").GetString());
        Assert.Single(db.Payments);
        Assert.Equal(40m, invoice.PaidAmount);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptKey_FromAnotherInvoiceCannotCreateASecondOperation(bool confirm)
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var first = Bill(tenant, 1, 100m);
        var second = Bill(tenant, 2, 100m);
        db.Invoices.AddRange(first, second);
        await db.SaveChangesAsync(Ct);
        var controller = new PaymentsController(db);
        Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        Assert.IsType<OkObjectResult>(await controller.ReportPayment(first.Id, new("cash", 40m, IdempotencyKey: "same-receipt-key"), Ct));
        var result = confirm
            ? await controller.ConfirmPayment(second.Id, new("cash", 40m, IdempotencyKey: "same-receipt-key"), Ct)
            : await controller.ReportPayment(second.Id, new("cash", 40m, IdempotencyKey: "same-receipt-key"), Ct);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(0m, second.PaidAmount);
    }
}
