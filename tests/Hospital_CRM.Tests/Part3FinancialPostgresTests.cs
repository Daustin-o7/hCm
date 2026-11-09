using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Infrastructure.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Hospital_CRM.Tests;

public sealed class Part3FinancialPostgresFactAttribute : FactAttribute
{
    public Part3FinancialPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PART3_FINANCIAL_TEST_OWNER")))
            Skip = "Requires an isolated synthetic Part 3 cluster on 127.0.0.1:55441.";
    }
}

public class Part3FinancialPostgresTests(Part3FinancialPostgresFixture fixture, ITestOutputHelper output)
    : IClassFixture<Part3FinancialPostgresFixture>
{
    [Part3FinancialPostgresFact]
    public async Task RuntimeRole_EnforcesRlsAndAppendOnlyEvenWithRawSql()
    {
        var a = await fixture.SeedAsync();
        var b = await fixture.SeedAsync();
        await using var db = fixture.AppDb(a);
        output.WriteLine($"Database: {db.Database.GetDbConnection().Database}; EnsureCreated + synthetic RLS/grants; not migration proof.");
        output.WriteLine(await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").SingleAsync());
        Assert.Equal("app_user", await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        Assert.Equal(a.Invoice, (await db.Invoices.IgnoreQueryFilters().SingleAsync()).Id);
        Assert.Equal(0, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.\"Invoices\" SET \"PaidAmount\" = 0 WHERE \"Id\" = {b.Invoice}"));
        await using var unscoped = fixture.AppDb();
        Assert.Empty(await unscoped.Payments.IgnoreQueryFilters().ToListAsync());
        foreach (var sql in new[] {
            "UPDATE public.\"InvoicePrintLogs\" SET \"CopyNumber\" = 0",
            "DELETE FROM public.\"InvoicePrintLogs\"",
            "UPDATE public.\"FinanceLedgerEntries\" SET \"DebitAmount\" = 0",
            "DELETE FROM public.\"FinanceLedgerEntries\"" })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
    }

    [Part3FinancialPostgresFact]
    public async Task Pdf_GeneratesBeforeLogging_AndEncodingFailurePreservesOriginal()
    {
        var s = await fixture.SeedAsync();
        await using var db = fixture.AppDb(s);
        var controller = Controller(db, s);
        var called = false;
        var failure = new PdfProbe(() =>
        {
            called = true;
            using var observer = fixture.AppDb(s);
            Assert.Empty(observer.InvoicePrintLogs.ToList());
            Assert.Equal(0, observer.Invoices.Single().PrintCount);
            throw new EncoderFallbackException("Synthetic unsupported glyph");
        });
        Assert.IsType<UnprocessableEntityObjectResult>(await controller.GetInvoicePdf(s.Invoice, failure, default));
        Assert.True(called);
        Assert.IsType<FileContentResult>(await controller.GetInvoicePdf(s.Invoice, new PdfService(), default));
        await using var saved = fixture.AppDb(s);
        Assert.Equal("ORIGINAL", (await saved.InvoicePrintLogs.SingleAsync()).Watermark);
        Assert.Equal(1, (await saved.Invoices.SingleAsync()).PrintCount);
    }

    [Part3FinancialPostgresFact]
    public async Task Pdf_ConcurrentInvoiceChangeRollsBackAppendOnlyLog()
    {
        var s = await fixture.SeedAsync();
        var writes = new PrintInsertProbe();
        await using var db = fixture.AppDb(s, interceptor: writes);
        var pdf = new PdfProbe(() =>
        {
            using var competitor = fixture.AppDb(s);
            var invoice = competitor.Invoices.Single();
            invoice.UpdatedAt = invoice.UpdatedAt.AddMinutes(1);
            competitor.SaveChanges();
        });
        Assert.IsType<ConflictObjectResult>(await Controller(db, s).GetInvoicePdf(s.Invoice, pdf, default));
        Assert.True(writes.ObservedInsert, "The append-only INSERT must execute before the conflicting invoice update rolls it back.");
        await using var saved = fixture.AppDb(s);
        Assert.Empty(await saved.InvoicePrintLogs.ToListAsync());
        Assert.Equal(0, (await saved.Invoices.SingleAsync()).PrintCount);
        Assert.IsType<FileContentResult>(await Controller(saved, s).GetInvoicePdf(s.Invoice, new PdfService(), default));
        Assert.Equal(1, (await saved.InvoicePrintLogs.SingleAsync()).CopyNumber);
    }

    [Part3FinancialPostgresFact]
    public async Task ConcurrentPdfPrints_OnlyOneOriginal_ThenJsonUsesNextCopy()
    {
        var s = await fixture.SeedAsync();
        using var barrier = new Barrier(2);
        async Task<IActionResult> Print()
        {
            await using var db = fixture.AppDb(s);
            return await Controller(db, s).GetInvoicePdf(s.Invoice, new PdfProbe(() =>
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(20)))), default);
        }
        var results = await Task.WhenAll(Task.Run(Print), Task.Run(Print)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results.OfType<FileContentResult>());
        Assert.Single(results.OfType<ConflictObjectResult>());
        await using var saved = fixture.AppDb(s);
        Assert.Equal(1, (await saved.InvoicePrintLogs.SingleAsync()).CopyNumber);
        Assert.IsType<OkObjectResult>(await Controller(saved, s).PrintInvoice(s.Invoice, default));
        Assert.Equal(new[] { 1, 2 }, await saved.InvoicePrintLogs.OrderBy(l => l.CopyNumber).Select(l => l.CopyNumber).ToArrayAsync());
    }

    [Part3FinancialPostgresFact]
    public async Task ProviderReceiptUniqueIndex_RejectsConcurrentDuplicatePositiveRazorpayOnly()
    {
        var a = await fixture.SeedAsync();
        var b = await fixture.SeedAsync();
        var receipt = "pay_" + Guid.NewGuid().ToString("N");
        var gate = new SaveBarrier();
        async Task<Exception?> Insert()
        {
            await using var db = fixture.AppDb(a, interceptor: gate);
            db.Payments.Add(Receipt(a, receipt));
            return await Record.ExceptionAsync(() => db.SaveChangesAsync());
        }
        var results = await Task.WhenAll(Insert(), Insert()).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, e => e == null);
        var failed = Assert.IsType<DbUpdateException>(Assert.Single(results, e => e != null));
        var pg = Assert.IsType<PostgresException>(failed.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("IX_Payments_TenantId_Method_RazorpayPaymentId", pg.ConstraintName);
        await using var other = fixture.AppDb(b);
        other.Payments.Add(Receipt(b, receipt));
        await other.SaveChangesAsync();
        await using var saved = fixture.AppDb(a);
        var negative = Receipt(a, receipt); negative.Amount = -1;
        var zero = Receipt(a, receipt); zero.Amount = 0;
        var cash = Receipt(a, receipt); cash.Method = PaymentMethod.Cash;
        saved.Payments.AddRange(negative, zero, cash, Receipt(a, null), Receipt(a, null));
        await saved.SaveChangesAsync();
        Assert.Equal(1, await saved.Payments.CountAsync(p => p.RazorpayPaymentId == receipt && p.Amount > 0 && p.Method == PaymentMethod.Razorpay));
    }

    [Part3FinancialPostgresFact]
    public async Task IdempotencyKeys_AreUniqueWithinTenantAcrossPatientInvoicePaymentAndDeposit()
    {
        var a = await fixture.SeedAsync();
        var b = await fixture.SeedAsync();
        foreach (var entity in new[] { "Patient", "Invoice", "Payment", "Deposit" })
        {
            var key = "synthetic-" + Guid.NewGuid().ToString("N");
            foreach (var scope in new[] { a, b })
            {
                await using var db = fixture.AppDb(scope);
                db.Add(Keyed(entity, scope, key));
                await db.SaveChangesAsync();
            }
            await using var duplicate = fixture.AppDb(a);
            duplicate.Add(Keyed(entity, a, key));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            var pg = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
            Assert.EndsWith("TenantId_IdempotencyKey", pg.ConstraintName);
        }
    }

    [Part3FinancialPostgresFact]
    public async Task ConcurrentFinalization_ConflictRollsBackLedgerAndDoesNotConsumeNumber()
    {
        var s = await fixture.SeedAsync(draft: true);
        Guid second;
        await using (var db = fixture.AppDb(s))
        {
            var invoice = new Invoice { TenantId = s.Tenant, Status = InvoiceStatus.Draft, Subtotal = 100, Total = 100, BalanceDue = 100,
                LineItems = [new InvoiceLineItem { Description = "Synthetic service", Quantity = 1, UnitPrice = 100, Amount = 100 }] };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            second = invoice.Id;
        }
        var gate = new SaveBarrier();
        async Task<IActionResult> Finalize(Guid invoice)
        {
            await using var db = fixture.AppDb(s, interceptor: gate);
            return await Controller(db, s).FinalizeInvoice(invoice, default);
        }
        var results = await Task.WhenAll(Finalize(s.Invoice), Finalize(second)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results.OfType<OkObjectResult>());
        Assert.Single(results.OfType<ConflictObjectResult>());
        await using var saved = fixture.AppDb(s);
        Assert.Equal(new[] { 0, 1 }, await saved.Invoices.OrderBy(i => i.InvoiceNumber).Select(i => i.InvoiceNumber).ToArrayAsync());
        Assert.Equal(2, await saved.FinanceLedgerEntries.CountAsync());
        var pending = await saved.Invoices.SingleAsync(i => i.Status == InvoiceStatus.Draft);
        Assert.IsType<OkObjectResult>(await Controller(saved, s).FinalizeInvoice(pending.Id, default));
        Assert.Equal(new[] { 1, 2 }, await saved.Invoices.OrderBy(i => i.InvoiceNumber).Select(i => i.InvoiceNumber).ToArrayAsync());
        Assert.Equal(4, await saved.FinanceLedgerEntries.CountAsync());
    }

    [Part3FinancialPostgresFact]
    public async Task ConcurrentCollections_CannotOvercollect_AndSavedReceiptReplays()
    {
        var s = await fixture.SeedAsync(paid: false);
        var gate = new SaveBarrier();
        var requests = new[] { new CollectPaymentRequest("Cash", 70, "IDEMP-PAY-cash-a"), new CollectPaymentRequest("Cash", 70, "IDEMP-PAY-cash-b") };
        async Task<IActionResult> Collect(CollectPaymentRequest request)
        {
            await using var db = fixture.AppDb(s, interceptor: gate);
            return await Controller(db, s).CollectPayment(s.Invoice, request, default);
        }
        var results = await Task.WhenAll(requests.Select(Collect)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results.OfType<OkObjectResult>());
        Assert.Single(results.OfType<ConflictObjectResult>());
        await using var saved = fixture.AppDb(s);
        var receipt = await saved.Payments.SingleAsync();
        var request = requests.Single(r => r.IdempotencyKey == receipt.IdempotencyKey);
        var replay = Json(await Controller(saved, s).CollectPayment(s.Invoice, request, default));
        Assert.Equal(receipt.Id, replay.GetProperty("paymentId").GetGuid());
        Assert.True(replay.GetProperty("idempotentReplay").GetBoolean());
        Assert.IsType<ConflictObjectResult>(await Controller(saved, s).CollectPayment(s.Invoice, request with { Amount = 60 }, default));
        Assert.Equal(70, (await saved.Invoices.SingleAsync()).PaidAmount);
        Assert.Equal(30, (await saved.Invoices.SingleAsync()).BalanceDue);
        Assert.Equal(2, await saved.FinanceLedgerEntries.CountAsync());
    }

    [Part3FinancialPostgresFact]
    public async Task PharmacyReturn_ApprovalAndIntentCommittedBeforeProvider_PendingCannotRestoreStock()
    {
        var s = await fixture.SeedAsync(pharmacy: true, threshold: 10);
        var handler = new ProviderProbe(async sent =>
        {
            await AssertReservedAsync(s, approved: true);
            return ProviderResponse(s, sent.Method == "POST" ? "pending" : "processed");
        });
        var request = Return(s);
        Guid refundId;
        await using (var db = fixture.AppDb(s))
        {
            var result = Json(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
            Assert.Equal("pending_approval", result.GetProperty("status").GetString());
            refundId = result.GetProperty("refundId").GetGuid();
            Assert.Empty(handler.Requests);
            Assert.IsType<BadRequestObjectResult>(await Controller(db, s, handler).ApproveRefund(s.Invoice, refundId, null, default));
        }
        await using (var db = fixture.AppDb(s, approver: true))
        {
            var result = Json(await Controller(db, s, handler, approver: true).ApproveRefund(s.Invoice, refundId, new("Synthetic distinct approval"), default));
            Assert.Equal("processing", result.GetProperty("status").GetString());
            Assert.Equal("rfnd_synthetic", result.GetProperty("refundReference").GetString());
        }
        await AssertReservedAsync(s, approved: true);
        await using (var db = fixture.AppDb(s))
        {
            var result = Json(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
            Assert.Equal(refundId, result.GetProperty("refundId").GetGuid());
            Assert.Equal("refunded", result.GetProperty("status").GetString());
        }
        Assert.Equal(new[] { "POST", "GET" }, handler.Requests.Select(r => r.Method));
        Assert.Equal("/v1/refunds/rfnd_synthetic", handler.Requests[1].Path);
        await AssertReturnedAsync(s, request, handler);
    }

    [Part3FinancialPostgresFact]
    public async Task PharmacyReturn_FailedProviderEvidenceReplaysWithoutMoneyStockOrAnotherSend()
    {
        var s = await fixture.SeedAsync(pharmacy: true);
        var handler = new ProviderProbe(async _ =>
        {
            await AssertReservedAsync(s);
            return ProviderResponse(s, "failed");
        });
        var request = Return(s);
        Guid refundId;
        await using (var db = fixture.AppDb(s))
        {
            var first = Assert.IsType<ObjectResult>(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
            Assert.Equal(502, first.StatusCode);
            refundId = Json(first).GetProperty("refundId").GetGuid();
        }
        await using var saved = fixture.AppDb(s);
        var replay = Assert.IsType<ObjectResult>(await Pharmacy(saved, s, handler).ProcessReturn(s.Invoice, request, default));
        Assert.Equal(502, replay.StatusCode);
        Assert.Equal(refundId, Json(replay).GetProperty("refundId").GetGuid());
        Assert.Single(handler.Requests);
        var refund = await saved.Payments.SingleAsync(p => p.Amount < 0);
        Assert.Equal(PaymentStatus.Failed, refund.Status);
        Assert.Equal("rfnd_synthetic", refund.RefundReference);
        await AssertUnchangedAsync(saved, s);
    }

    [Part3FinancialPostgresFact]
    public async Task LostProviderResponse_RetryUsesIdenticalSavedIntentKeyAndBody()
    {
        var s = await fixture.SeedAsync(pharmacy: true);
        var attempts = 0;
        var handler = new ProviderProbe(async _ =>
        {
            await AssertReservedAsync(s);
            if (++attempts == 1) throw new HttpRequestException("Synthetic lost response");
            return ProviderResponse(s, "processed");
        });
        var request = Return(s);
        Guid refundId;
        await using (var db = fixture.AppDb(s))
        {
            var first = Json(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
            Assert.Equal("processing", first.GetProperty("status").GetString());
            refundId = first.GetProperty("refundId").GetGuid();
        }
        await using (var db = fixture.AppDb(s))
        {
            var second = Json(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
            Assert.Equal(refundId, second.GetProperty("refundId").GetGuid());
            Assert.Equal("refunded", second.GetProperty("status").GetString());
        }
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        await AssertReturnedAsync(s, request, handler);
    }

    [Part3FinancialPostgresFact]
    public async Task RefundFailureAfterSqlWrites_RollsBackMoneyStockRegisterAndLedger_ThenSameIntentRetries()
    {
        var s = await fixture.SeedAsync(pharmacy: true);
        var failure = new FailAfterRefundWrites();
        var handler = new ProviderProbe(_ => Task.FromResult(ProviderResponse(s, "processed")));
        var request = Return(s);
        await using (var db = fixture.AppDb(s, interceptor: failure))
            Assert.IsType<ConflictObjectResult>(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
        Assert.True(failure.ObservedWrittenRows);
        await AssertReservedAsync(s);
        await using (var db = fixture.AppDb(s))
            Assert.Equal("refunded", Json(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default)).GetProperty("status").GetString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        await AssertReturnedAsync(s, request, handler);
    }

    [Part3FinancialPostgresFact]
    public async Task Refund_UnrelatedDatabasePermissionFailurePropagatesAndRollsBack()
    {
        var s = await fixture.SeedAsync(pharmacy: true);
        var failure = new FailAfterRefundWrites(permissionFailure: true);
        var handler = new ProviderProbe(_ => Task.FromResult(ProviderResponse(s, "processed")));
        await using (var db = fixture.AppDb(s, interceptor: failure))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Pharmacy(db, s, handler).ProcessReturn(s.Invoice, Return(s), default));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.True(failure.ObservedWrittenRows);
        Assert.Single(handler.Requests);
        await AssertReservedAsync(s);
    }

    private async Task AssertReservedAsync(Part3FinancialScenario s, bool approved = false)
    {
        await using var observer = fixture.AppDb(s);
        var refund = await observer.Payments.SingleAsync(p => p.Amount < 0);
        Assert.Equal(PaymentStatus.Pending, refund.Status);
        Assert.Equal(PaymentVerificationStatus.Confirmed, refund.VerificationStatus);
        Assert.Equal(s.Source, refund.RefundSourcePaymentId);
        Assert.Equal(s.User, refund.ConfirmedByUserId);
        if (approved) Assert.Equal(s.Approver, refund.SecondApprovedByUserId);
        Assert.Equal(-100, refund.Amount);
        Assert.Equal("return-" + s.Invoice, refund.IdempotencyKey);
        await AssertUnchangedAsync(observer, s);
    }

    private static async Task AssertUnchangedAsync(HospitalCrmDbContext db, Part3FinancialScenario s)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == s.Invoice);
        Assert.Equal(100, invoice.PaidAmount);
        Assert.Equal(0, invoice.BalanceDue);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(8, (await db.DrugBatches.AsNoTracking().SingleAsync()).QuantityRemaining);
        Assert.Empty(await db.FinanceLedgerEntries.ToListAsync());
        Assert.Empty(await db.DrugStockMovements.ToListAsync());
        Assert.Equal(2, (await db.ControlledSubstanceRegisters.SingleAsync()).Quantity);
    }

    private async Task AssertReturnedAsync(Part3FinancialScenario s, ReturnRequest request, ProviderProbe handler)
    {
        await using var db = fixture.AppDb(s);
        var sends = handler.Requests.Count;
        var before = await db.Payments.AsNoTracking().SingleAsync(p => p.Amount < 0);
        var replay = Json(await Pharmacy(db, s, handler).ProcessReturn(s.Invoice, request, default));
        Assert.Equal(before.Id, replay.GetProperty("refundId").GetGuid());
        Assert.Equal(before.RefundReference, replay.GetProperty("refundReference").GetString());
        Assert.Equal(sends, handler.Requests.Count);
        Assert.Equal(PaymentStatus.Refunded, before.Status);
        Assert.Equal(2, await db.Payments.CountAsync());
        Assert.Equal(0, (await db.Invoices.SingleAsync()).PaidAmount);
        Assert.Equal(10, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
        Assert.Equal(2, (await db.DrugStockMovements.SingleAsync()).Quantity);
        Assert.Equal(new[] { -2, 2 }, await db.ControlledSubstanceRegisters.OrderBy(r => r.Quantity).Select(r => r.Quantity).ToArrayAsync());
        var ledger = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Equal(2, ledger.Count);
        Assert.Equal(100, ledger.Sum(e => e.DebitAmount));
        Assert.Equal(100, ledger.Sum(e => e.CreditAmount));
    }

    private static Payment Receipt(Part3FinancialScenario s, string? reference) => new()
    {
        TenantId = s.Tenant, InvoiceId = s.Invoice, Amount = 1, Method = PaymentMethod.Razorpay,
        RazorpayPaymentId = reference, Status = PaymentStatus.Completed, VerificationStatus = PaymentVerificationStatus.Confirmed
    };

    private static object Keyed(string entity, Part3FinancialScenario s, string key) => entity switch
    {
        "Patient" => new Patient { Id = Guid.NewGuid(), TenantId = s.Tenant, CreatedBy = s.User, Name = "Synthetic", Phone = "9000000000", IdempotencyKey = key },
        "Invoice" => new Invoice { TenantId = s.Tenant, IdempotencyKey = key },
        "Payment" => new Payment { TenantId = s.Tenant, InvoiceId = s.Invoice, Amount = 1, Method = PaymentMethod.Cash, IdempotencyKey = key },
        "Deposit" => new PatientDeposit { TenantId = s.Tenant, PatientId = s.Patient, CollectedByUserId = s.User, ReceiptNumber = Guid.NewGuid().ToString("N"), IdempotencyKey = key },
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };

    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value);
    private static ReturnRequest Return(Part3FinancialScenario s) => new("Synthetic return", null, "return-" + s.Invoice, SourcePaymentId: s.Source, RefundNonConsiderationReason: "Customer goodwill adjustment");
    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Razorpay:KeyId"] = "synthetic-key", ["Razorpay:KeySecret"] = "synthetic-secret" }).Build();

    private static InvoicesController Controller(HospitalCrmDbContext db, Part3FinancialScenario s, ProviderProbe? handler = null, bool approver = false) =>
        new(db, Config, new RazorpayOrderClient(new HttpClient(handler ?? new ProviderProbe(_ => throw new InvalidOperationException("Unexpected provider send"))), Config))
        { ControllerContext = new() { HttpContext = Part3FinancialPostgresFixture.Http(s, approver) } };

    private static PharmacyPosController Pharmacy(HospitalCrmDbContext db, Part3FinancialScenario s, ProviderProbe handler) =>
        new(db, NullLogger<PharmacyPosController>.Instance, new TimezoneService(), Config, new RazorpayOrderClient(new HttpClient(handler), Config))
        { ControllerContext = new() { HttpContext = Part3FinancialPostgresFixture.Http(s) } };

    private static HttpResponseMessage ProviderResponse(Part3FinancialScenario s, string status) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { id = "rfnd_synthetic", payment_id = "pay_" + s.Source.ToString("N"), currency = "INR", amount = 10000, status }), Encoding.UTF8, "application/json")
    };

    private sealed record Sent(string Method, string Path, string? Key, string Body);
    private sealed class ProviderProbe(Func<Sent, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sent = new Sent(request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Refund-Idempotency", out var keys) ? keys.Single() : null,
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            Requests.Add(sent);
            return await respond(sent);
        }
    }

    private sealed class PdfProbe(Action beforeGeneration) : IPdfService
    {
        public byte[] GenerateInvoicePdf(Invoice invoice, Clinic? clinic, bool isDuplicate, string? supplierGstin = null)
        {
            beforeGeneration();
            return new PdfService().GenerateInvoicePdf(invoice, clinic, isDuplicate, supplierGstin);
        }
        public byte[] GeneratePrescriptionPdf(Prescription prescription, Consultation consultation, Clinic? clinic, bool isDuplicate) => throw new NotSupportedException();
        public byte[] GenerateCreditNotePdf(CreditNote creditNote, Invoice invoice, Clinic? clinic, string? supplierGstin = null) => throw new NotSupportedException();
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return result;
        }
    }

    private sealed class PrintInsertProbe : DbCommandInterceptor
    {
        public bool ObservedInsert { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            // The first result has arrived: this INSERT ran, even if a later statement in the batch conflicts.
            if (command.CommandText.StartsWith("INSERT INTO \"InvoicePrintLogs\"", StringComparison.Ordinal))
                ObservedInsert = true;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailAfterRefundWrites(bool permissionFailure = false) : SaveChangesInterceptor
    {
        public bool ObservedWrittenRows { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            var db = (HospitalCrmDbContext)eventData.Context!;
            if (!db.ChangeTracker.Entries<Payment>().Any(e => e.Entity.Amount < 0 && e.Entity.Status == PaymentStatus.Refunded)) return result;
            Assert.NotNull(db.Database.CurrentTransaction);
            // Read actual SQL writes in the still-open transaction before injecting failure.
            Assert.Equal(2, await db.FinanceLedgerEntries.AsNoTracking().CountAsync(ct));
            Assert.Equal(10, (await db.DrugBatches.AsNoTracking().SingleAsync(ct)).QuantityRemaining);
            Assert.Equal(2, await db.ControlledSubstanceRegisters.AsNoTracking().CountAsync(ct));
            ObservedWrittenRows = true;
            if (permissionFailure)
                await db.Database.ExecuteSqlRawAsync("DELETE FROM public.\"InvoicePrintLogs\"", ct);
            throw new DbUpdateConcurrencyException("Synthetic failure after refund SQL writes and before commit");
        }
    }
}

public sealed record Part3FinancialScenario(Guid Tenant, Guid Clinic, Guid User, Guid Approver, Guid Patient, Guid Invoice, Guid Source);

public sealed class Part3FinancialPostgresFixture : IAsyncLifetime
{
    private string _owner = "";
    private string _app = "";

    public async Task InitializeAsync()
    {
        var input = Environment.GetEnvironmentVariable("PART3_FINANCIAL_TEST_OWNER");
        if (string.IsNullOrWhiteSpace(input)) return;
        var builder = new NpgsqlConnectionStringBuilder(input);
        if (builder.Host != "127.0.0.1" || builder.Port != 55441 || builder.Username != "part3_owner" ||
            builder.Database?.StartsWith("part3_financial_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Only part3_owner on a dedicated 127.0.0.1:55441 part3_financial_* synthetic database is allowed.");
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Username = "postgres", Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SHOW data_directory", connection);
            var directory = Path.GetFullPath((string)(await command.ExecuteScalarAsync())!);
            var approved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode") + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(approved + "part3-financial-pg-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PostgreSQL data_directory must be a Part 3 synthetic cluster under approved OpenCode Temp.");
        }
        builder.Database = "part3_financial_" + Guid.NewGuid().ToString("N");
        builder.Pooling = false;
        _owner = builder.ConnectionString;
        builder.Username = "app_user";
        builder.Password = null;
        _app = builder.ConnectionString;
        await using var db = OwnerDb();
        Assert.True(await db.Database.EnsureCreatedAsync(), "Must create a fresh database, never overwrite existing data.");
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        // EnsureCreated verifies current model constraints; synthetic policies exercise real RLS, not migration deployment.
        await db.Database.ExecuteSqlRawAsync("""
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO app_user;
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_user;
            REVOKE UPDATE, DELETE ON public."InvoicePrintLogs", public."FinanceLedgerEntries",
                public."DrugStockMovements", public."ControlledSubstanceRegisters" FROM app_user;
            DO $policies$
            DECLARE t record;
            BEGIN
                FOR t IN SELECT table_name FROM information_schema.columns
                    WHERE table_schema = 'public' AND column_name = 'TenantId'
                LOOP
                    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t.table_name);
                    EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t.table_name);
                    EXECUTE format('CREATE POLICY tenant_isolation_policy ON public.%I
                        USING ("TenantId" = NULLIF(current_setting(''app.current_tenant_id'', true), '''')::uuid
                            OR current_setting(''app.is_platform_admin'', true) = ''true'')
                        WITH CHECK ("TenantId" = NULLIF(current_setting(''app.current_tenant_id'', true), '''')::uuid
                            OR current_setting(''app.is_platform_admin'', true) = ''true'')', t.table_name);
                END LOOP;
            END $policies$;
            """);
    }

    private HospitalCrmDbContext OwnerDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_owner)
        .AddInterceptors(new TenantConnectionInterceptor(TestMaintenanceTenantContext.Instance)).Options, TestMaintenanceTenantContext.Instance);

    public HospitalCrmDbContext AppDb(Part3FinancialScenario? s = null, bool approver = false, IInterceptor? interceptor = null)
    {
        var context = s == null ? new TenantContext() : new TenantContext(new RequestAccessor { HttpContext = Http(s, approver) });
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_app)
            .AddInterceptors(new TenantConnectionInterceptor(context));
        if (interceptor != null) options.AddInterceptors(interceptor);
        return new HospitalCrmDbContext(options.Options, context);
    }

    public static DefaultHttpContext Http(Part3FinancialScenario s, bool approver = false) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", (approver ? s.Approver : s.User).ToString()), new Claim("tenant_id", s.Tenant.ToString()),
            new Claim("clinicId", s.Clinic.ToString()), new Claim(ClaimTypes.Role, "ClinicAdmin")], "SyntheticTest"))
    };

    public async Task<Part3FinancialScenario> SeedAsync(bool paid = true, bool draft = false, bool pharmacy = false, decimal threshold = 1000)
    {
        var s = new Part3FinancialScenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = OwnerDb();
        db.AddRange(new Tenant { Id = s.Tenant },
            new Clinic { Id = s.Clinic, TenantId = s.Tenant, Name = "Synthetic clinic", Address = "Test", Phone = "9000000000" },
            new User { Id = s.User, TenantId = s.Tenant, ClinicId = s.Clinic, Role = UserRole.ClinicAdmin, Name = "Synthetic cashier", Email = s.User + "@synthetic.invalid", PasswordHash = "unused" },
            new User { Id = s.Approver, TenantId = s.Tenant, ClinicId = s.Clinic, Role = UserRole.ClinicAdmin, Name = "Synthetic approver", Email = s.Approver + "@synthetic.invalid", PasswordHash = "unused" },
            new TenantMembership { TenantId = s.Tenant, ClinicId = s.Clinic, UserId = s.User, Role = UserRole.ClinicAdmin, Status = "active" },
            new TenantMembership { TenantId = s.Tenant, ClinicId = s.Clinic, UserId = s.Approver, Role = UserRole.ClinicAdmin, Status = "active" },
            new Patient { Id = s.Patient, TenantId = s.Tenant, CreatedBy = s.User, Name = "Synthetic patient", Phone = "9000000000" },
            new TenantTaxProfile { TenantId = s.Tenant, ThresholdRefundApproval = threshold });
        var invoice = new Invoice { Id = s.Invoice, TenantId = s.Tenant, PatientId = s.Patient, InvoiceNumber = draft ? 0 : 1,
            InvoiceType = pharmacy ? InvoiceType.Pharmacy : InvoiceType.Consultation,
            Status = draft ? InvoiceStatus.Draft : paid ? InvoiceStatus.Paid : InvoiceStatus.Finalized,
            Subtotal = 100, Total = 100, PaidAmount = paid && !draft ? 100 : 0, BalanceDue = paid && !draft ? 0 : 100,
            LineItems = [new InvoiceLineItem { Description = "Synthetic service", Quantity = pharmacy ? 2 : 1, UnitPrice = pharmacy ? 50 : 100, Amount = 100 }] };
        db.Invoices.Add(invoice);
        if (paid && !draft)
            invoice.Payments.Add(new Payment { Id = s.Source, TenantId = s.Tenant, InvoiceId = s.Invoice, Amount = 100,
                Method = PaymentMethod.Razorpay, Status = PaymentStatus.Completed, VerificationStatus = PaymentVerificationStatus.Confirmed,
                RazorpayPaymentId = "pay_" + s.Source.ToString("N") });
        if (pharmacy)
        {
            var drug = new Drug { TenantId = s.Tenant, Name = "Synthetic drug" };
            var batch = new DrugBatch { TenantId = s.Tenant, Drug = drug, BatchNumber = "SYN-1", QuantityReceived = 10, QuantityRemaining = 8,
                ExpiryDate = new DateOnly(2035, 1, 1), Mrp = 50 };
            db.DrugBatches.Add(batch);
            invoice.LineItems.Single().DrugBatch = batch;
            db.ControlledSubstanceRegisters.Add(new ControlledSubstanceRegister { TenantId = s.Tenant, Invoice = invoice, Drug = drug,
                Quantity = 2, DrugName = "Synthetic drug", BatchNumber = "SYN-1", DispensedBy = s.User });
        }
        await db.SaveChangesAsync();
        return s;
    }

    public Task DisposeAsync() => Task.CompletedTask; // Preserve synthetic evidence; the runner stops its owned cluster.

    // Independent test requests coexist on one async flow; the production accessor uses one ambient request.
    private sealed class RequestAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
