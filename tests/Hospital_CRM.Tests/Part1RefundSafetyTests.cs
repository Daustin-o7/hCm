using System.Net;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1RefundSafetyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(1000000.01)]
    public async Task Refund_InvalidMoneyCannotCreateAnIntent(decimal amount)
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        Assert.IsType<BadRequestObjectResult>(await Controller(db, tenant, user).ProcessRefund(invoice.Id,
            new(amount, PaymentMethod.Cash, "VOUCHER-1", "Synthetic cancellation", Key()), Ct));
        Assert.Single(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Refund_RequiresStableKeyAndRejectsConflictingReplays()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);
        var request = new CreateRefundRequest(40m, PaymentMethod.Cash, "VOUCHER-1", "Synthetic cancellation");
        Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        request = request with { IdempotencyKey = Key() };
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct)).StatusCode);
        var replay = Assert.IsType<OkObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal("refunded", Json(replay).GetProperty("status").GetString());
        foreach (var changed in new[] { request with { Amount = 50m }, request with { RefundReference = "OTHER" },
                     request with { Reason = "Different reason" }, request with { Method = PaymentMethod.UPI } })
            Assert.IsType<ConflictObjectResult>(await controller.ProcessRefund(invoice.Id, changed, Ct));
        Assert.Equal(60m, invoice.PaidAmount);
        Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Refund_IncomingCollectionKeyCannotBeReusedAsARefund()
    {
        await using var db = MemoryDb();
        var (invoice, payment, tenant, user) = await Paid(db);
        payment.IdempotencyKey = Key();
        await db.SaveChangesAsync(Ct);
        Assert.IsType<ConflictObjectResult>(await Controller(db, tenant, user).ProcessRefund(invoice.Id,
            new(50m, PaymentMethod.Cash, "VOUCHER-1", "Synthetic cancellation", payment.IdempotencyKey), Ct));
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Single(db.Payments);
    }

    [Fact]
    public async Task ProviderRefund_UsesRealResponseAndPostsOnlyOnce()
    {
        await using var db = MemoryDb();
        var (invoice, payment, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var handler = new ProviderHandler(_ => Response("processed"));
        var controller = Controller(db, tenant, user, handler);
        var request = ProviderRequest();
        var result = Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal(201, result.StatusCode);
        Assert.Equal("rfnd_real123", Json(result).GetProperty("refundReference").GetString());
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/v1/payments/pay_captured123/refund", sent.Path);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(5000, body.GetProperty("amount").GetInt32());
        Assert.Matches("^[A-Za-z0-9_-]{10,}$", sent.Key!);
        Assert.Equal(32, body.GetProperty("receipt").GetString()!.Length);
        var refund = await db.Payments.SingleAsync(p => p.Amount < 0);
        Assert.Equal(payment.RazorpayPaymentId, refund.RazorpayPaymentId);
        Assert.Equal("rfnd_real123", refund.RefundReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(50m, invoice.PaidAmount);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
        Assert.IsType<OkObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Single(handler.Requests);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task ProviderRefund_LostResponseRetriesIdenticalDurableOperation()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var attempts = 0;
        var handler = new ProviderHandler(_ => ++attempts == 1
            ? throw new HttpRequestException("Synthetic response lost after provider acceptance") : Response("processed"));
        var controller = Controller(db, tenant, user, handler);
        var request = ProviderRequest();
        var ambiguous = Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal(202, ambiguous.StatusCode);
        Assert.Equal("processing", Json(ambiguous).GetProperty("status").GetString());
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(PaymentStatus.Pending, (await db.Payments.SingleAsync(p => p.Amount < 0)).Status);
        Assert.IsType<OkObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Equal(50m, invoice.PaidAmount);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task ProviderRefund_PendingIsRetrievedBeforeCompletion()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var handler = new ProviderHandler(request => Response(request.Method == "POST" ? "pending" : "processed"));
        var controller = Controller(db, tenant, user, handler);
        var request = ProviderRequest();
        var pending = Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal(202, pending.StatusCode);
        Assert.Equal("processing", Json(pending).GetProperty("status").GetString());
        Assert.Equal("rfnd_real123", Json(pending).GetProperty("refundReference").GetString());
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.IsType<OkObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Equal("/v1/refunds/rfnd_real123", handler.Requests[1].Path);
        Assert.Equal(50m, invoice.PaidAmount);
    }

    [Theory]
    [InlineData("failed", "rfnd_real123", "pay_captured123", "INR", 5000)]
    [InlineData("processed", "", "pay_captured123", "INR", 5000)]
    [InlineData("processed", "rfnd_real123", "pay_other", "INR", 5000)]
    [InlineData("processed", "rfnd_real123", "pay_captured123", "USD", 5000)]
    [InlineData("processed", "rfnd_real123", "pay_captured123", "INR", 4900)]
    [InlineData("unknown", "rfnd_real123", "pay_captured123", "INR", 5000)]
    public async Task ProviderRefund_UnverifiedOrFailedResponseCannotPostMoney(string status, string id, string paymentId, string currency, int amount)
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var handler = new ProviderHandler(_ => Response(status, id, paymentId, currency, amount));
        var result = Assert.IsType<ObjectResult>(await Controller(db, tenant, user, handler).ProcessRefund(invoice.Id, ProviderRequest(), Ct));
        Assert.NotEqual("refunded", Json(result).GetProperty("status").GetString());
        Assert.NotEqual(201, result.StatusCode);
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.DoesNotContain(db.Payments, p => p.Amount < 0 && p.Status == PaymentStatus.Refunded);
    }

    [Fact]
    public async Task ProviderRefund_MissingConfigurationCannotCreateACompletedPayout()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var result = Assert.IsType<ObjectResult>(await Controller(db, tenant, user).ProcessRefund(invoice.Id, ProviderRequest(), Ct));
        Assert.Equal(503, result.StatusCode);
        Assert.Single(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(100m, invoice.PaidAmount);
    }

    [Fact]
    public async Task ProviderRefund_DualApprovalDoesNotCallProviderUntilDistinctApproval()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        db.TenantTaxProfiles.Add(new TenantTaxProfile { TenantId = tenant, ThresholdRefundApproval = 10m });
        await db.SaveChangesAsync(Ct);
        var handler = new ProviderHandler(_ => Response("pending"));
        var controller = Controller(db, tenant, user, handler);
        var pending = Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, ProviderRequest(), Ct));
        Assert.Equal(202, pending.StatusCode);
        Assert.Equal("pending_approval", Json(pending).GetProperty("status").GetString());
        Assert.Empty(handler.Requests);
        var refund = await db.Payments.SingleAsync(p => p.Amount < 0);
        Assert.IsType<BadRequestObjectResult>(await controller.ApproveRefund(invoice.Id, refund.Id, null, Ct));
        Assert.Empty(handler.Requests);
        var approver = Controller(db, tenant, Guid.NewGuid(), handler);
        var approved = Assert.IsType<ObjectResult>(await approver.ApproveRefund(invoice.Id, refund.Id, new("Verified"), Ct));
        Assert.Equal(202, approved.StatusCode);
        Assert.Equal("processing", Json(approved).GetProperty("status").GetString());
        Assert.Single(handler.Requests);
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task InvoiceRead_ReturnsAuthoritativeSourcesLimitsAndRefundHistory()
    {
        await using var db = MemoryDb();
        var (invoice, collected, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        db.Payments.AddRange(
            new Payment { TenantId = tenant, InvoiceId = invoice.Id, Amount = 500m, Method = PaymentMethod.UPI,
                Status = PaymentStatus.Completed, VerificationStatus = PaymentVerificationStatus.Reported },
            new Payment { TenantId = tenant, InvoiceId = invoice.Id, Amount = 200m, Method = PaymentMethod.AdvanceDeposit,
                Status = PaymentStatus.Completed, VerificationStatus = PaymentVerificationStatus.Confirmed });
        db.TenantTaxProfiles.Add(new TenantTaxProfile { TenantId = tenant, ThresholdRefundApproval = 10m });
        await db.SaveChangesAsync(Ct);
        var controller = Controller(db, tenant, user, new ProviderHandler(_ => Response("pending")));
        var request = ProviderRequest() with { SourcePaymentId = collected.Id };
        Assert.Equal(202, Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct)).StatusCode);
        var refund = await db.Payments.SingleAsync(p => p.Amount < 0);
        var detail = Json(Assert.IsType<OkObjectResult>(await controller.GetInvoice(invoice.Id, Ct)));
        Assert.Equal(50m, detail.GetProperty("refundableAmount").GetDecimal());
        var receipts = detail.GetProperty("payments").EnumerateArray().ToList();
        var source = receipts.Single(p => p.GetProperty("id").GetGuid() == collected.Id);
        Assert.Equal(50m, source.GetProperty("refundableAmount").GetDecimal());
        Assert.Equal("pay_captured123", source.GetProperty("razorpayPaymentId").GetString());
        Assert.All(receipts.Where(p => p.GetProperty("amount").GetDecimal() > 100m),
            p => Assert.Equal(0m, p.GetProperty("refundableAmount").GetDecimal()));
        var history = receipts.Single(p => p.GetProperty("id").GetGuid() == refund.Id);
        Assert.Equal("pending_approval", history.GetProperty("refundStatus").GetString());
        Assert.Equal(request.IdempotencyKey, history.GetProperty("idempotencyKey").GetString());
        Assert.Equal(request.Reason, history.GetProperty("refundReason").GetString());
        Assert.Equal(collected.Id, history.GetProperty("refundSourcePaymentId").GetGuid());
        Assert.Equal(user, history.GetProperty("confirmedByUserId").GetGuid());
        Assert.False(history.GetProperty("isPharmacyReturn").GetBoolean());
    }

    [Fact]
    public async Task ProviderRefund_KnownFailureReplayCannotStartAnotherPayout()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var handler = new ProviderHandler(_ => Response("failed"));
        var controller = Controller(db, tenant, user, handler);
        var request = ProviderRequest();
        var first = Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var replay = Assert.IsType<ObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal(502, first.StatusCode);
        Assert.Equal(502, replay.StatusCode);
        Assert.Equal(Json(first).GetProperty("refundId").GetGuid(), Json(replay).GetProperty("refundId").GetGuid());
        Assert.Single(handler.Requests);
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
        var detail = Json(Assert.IsType<OkObjectResult>(await controller.GetInvoice(invoice.Id, Ct)));
        Assert.Equal(100m, detail.GetProperty("refundableAmount").GetDecimal());
    }

    [Fact]
    public async Task ProcessingRefund_ConfigurationFailureRetainsSavedIdentityAndBalance()
    {
        await using var db = MemoryDb();
        var (invoice, source, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        var request = ProviderRequest();
        var handler = new ProviderHandler(_ => Response("pending"));
        await Controller(db, tenant, user, handler).ProcessRefund(invoice.Id, request, Ct);
        var refund = await db.Payments.AsNoTracking().SingleAsync(p => p.Amount < 0);

        var result = Assert.IsType<ObjectResult>(await Controller(db, tenant, user).ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("razorpay_not_configured", Json(result).GetProperty("error").GetString());
        AssertSavedPending(Json(result), refund, invoice, source.Id);
        Assert.Single(handler.Requests);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task ApprovalFailureRetainsTheReservedIntentAndNeverSendsPayment()
    {
        await using var db = MemoryDb();
        var (invoice, source, tenant, user) = await Paid(db, PaymentMethod.Razorpay);
        db.TenantTaxProfiles.Add(new TenantTaxProfile { TenantId = tenant, ThresholdRefundApproval = 10m });
        await db.SaveChangesAsync(Ct);
        var handler = new ProviderHandler(_ => Response("processed"));
        var controller = Controller(db, tenant, user, handler);
        await controller.ProcessRefund(invoice.Id, ProviderRequest(), Ct);
        var refund = await db.Payments.AsNoTracking().SingleAsync(p => p.Amount < 0);
        var result = Assert.IsType<BadRequestObjectResult>(await controller.ApproveRefund(invoice.Id, refund.Id, null, Ct));
        Assert.Equal("dual_approval_self_approval_blocked", Json(result).GetProperty("error").GetString());
        AssertSavedPending(Json(result), refund, invoice, source.Id, "pending_approval");
        Assert.Empty(handler.Requests);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task ProcessedProviderReturnWithChangedStockReportsOnlyPersistedEvidence()
    {
        await using var db = MemoryDb();
        var (invoice, source, tenant, user) = await Paid(db, PaymentMethod.Razorpay, 50m);
        invoice.InvoiceType = InvoiceType.Pharmacy;
        var batch = new DrugBatch { TenantId = tenant, QuantityReceived = 10, QuantityRemaining = 9, BatchNumber = "SYNTHETIC" };
        db.DrugBatches.Add(batch);
        db.InvoiceLineItems.Add(new InvoiceLineItem { Invoice = invoice, DrugBatchId = batch.Id, Description = "Synthetic medicine", Quantity = 1, Amount = 50m, UnitPrice = 50m });
        await db.SaveChangesAsync(Ct);
        var handler = new ProviderHandler(_ =>
        {
            // A concurrent stock reconciliation consumes the allowance before local refund finalization.
            batch.QuantityRemaining = 10;
            db.SaveChanges();
            return Response("processed");
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Razorpay:KeyId"] = "synthetic-key", ["Razorpay:KeySecret"] = "synthetic-secret" }).Build();
        var controller = new PharmacyPosController(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<PharmacyPosController>.Instance,
            new TimezoneService(), config, new RazorpayOrderClient(new HttpClient(handler), config));
        Caller(controller, user, tenant, "ClinicAdmin");
        var result = Assert.IsType<BadRequestObjectResult>(await controller
            .ProcessReturn(invoice.Id, new("Synthetic return", null, IdempotencyKey: Key(), SourcePaymentId: source.Id), Ct));
        var refund = await db.Payments.AsNoTracking().SingleAsync(p => p.Amount < 0);
        Assert.Equal("return_stock_reconciliation_required", Json(result).GetProperty("error").GetString());
        AssertSavedPending(Json(result), refund, invoice, source.Id);
        Assert.Null(Json(result).GetProperty("refundReference").GetString());
        Assert.True(Json(result).GetProperty("isPharmacyReturn").GetBoolean());
        Assert.Equal(50m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Empty(db.DrugStockMovements);
    }

    private static void AssertSavedPending(JsonElement data, Payment refund, Invoice invoice, Guid source, string status = "processing")
    {
        Assert.Equal(refund.Id, data.GetProperty("refundId").GetGuid());
        Assert.Equal(invoice.Id, data.GetProperty("invoiceId").GetGuid());
        Assert.Equal(refund.IdempotencyKey, data.GetProperty("idempotencyKey").GetString());
        Assert.Equal(source, data.GetProperty("refundSourcePaymentId").GetGuid());
        Assert.Equal(-refund.Amount, data.GetProperty("amount").GetDecimal());
        Assert.Equal(invoice.PaidAmount, data.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(invoice.BalanceDue, data.GetProperty("balanceDue").GetDecimal());
        Assert.Equal(status, data.GetProperty("status").GetString());
        Assert.Equal("pending", data.GetProperty("verificationStatus").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("refundedAt").ValueKind);
        Assert.False(data.GetProperty("stockRestored").GetBoolean());
    }

    private static string Key() => $"IDEMP-REF-{Guid.NewGuid()}";
    private static CreateRefundRequest ProviderRequest() => new(50m, PaymentMethod.Razorpay, null!, "Synthetic cancellation", Key());
    private static JsonElement Json(ObjectResult result) => JsonSerializer.SerializeToElement(result.Value);

    internal static async Task<(Invoice Invoice, Payment Payment, Guid Tenant, Guid User)> Paid(HospitalCrmDbContext db,
        PaymentMethod method = PaymentMethod.Cash, decimal total = 100m)
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var invoice = Bill(tenant, 1, total);
        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAmount = total;
        invoice.BalanceDue = 0;
        var payment = new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Amount = total,
            Method = method, Status = PaymentStatus.Completed, VerificationStatus = PaymentVerificationStatus.Confirmed,
            RazorpayPaymentId = method == PaymentMethod.Razorpay ? "pay_captured123" : null };
        invoice.Payments.Add(payment);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(Ct);
        return (invoice, payment, tenant, user);
    }

    internal static InvoicesController Controller(HospitalCrmDbContext db, Guid tenant, Guid user, ProviderHandler? handler = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(handler == null ? [] : new Dictionary<string, string?>
            { ["Razorpay:KeyId"] = "synthetic-key", ["Razorpay:KeySecret"] = "synthetic-secret" }).Build();
        var client = new RazorpayOrderClient(handler == null ? new HttpClient() : new HttpClient(handler), config);
        var controller = new InvoicesController(db, config, client);
        Caller(controller, user, tenant, "ClinicAdmin");
        return controller;
    }

    internal static HttpResponseMessage Response(string status, string id = "rfnd_real123", string paymentId = "pay_captured123",
        string currency = "INR", int amount = 5000) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { id, payment_id = paymentId, currency, amount, status }), Encoding.UTF8, "application/json") };

    internal record SentRequest(string Method, string Path, string? Key, string Body);
    internal sealed class ProviderHandler(Func<SentRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new SentRequest(request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Refund-Idempotency", out var keys) ? keys.Single() : null,
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(sent);
            return respond(sent);
        }
    }
}
