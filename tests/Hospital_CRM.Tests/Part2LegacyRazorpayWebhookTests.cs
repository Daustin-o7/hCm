using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part2LegacyRazorpayWebhookTests(Part2TenantBoundaryPostgresFixture fixture)
    : IClassFixture<Part2TenantBoundaryPostgresFixture>
{
    private const string Secret = "synthetic-legacy-secret";

    [Theory]
    [InlineData("missing-signature", 400, "missing_webhook_signature")]
    [InlineData("invalid-signature", 400, "invalid_webhook_signature")]
    [InlineData("missing-context", 503, "tenant_context_unavailable")]
    [InlineData("missing-order", 503, "payment_order_not_found")]
    [InlineData("oversized-order", 400, "invalid_payment_reference")]
    public async Task Legacy_RejectsBeforeAnyDatabaseAccess(string scenario, int status, string error)
    {
        var context = new TenantContext();
        var original = Guid.NewGuid();
        using var scope = context.PushTenant(original);
        // No provider: any attempt to route, start a transaction or query would fail this test.
        await using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>().Options, context);
        var controller = Controller(db, scenario == "missing-context" ? null : context);
        var order = scenario == "missing-order" ? null : scenario == "oversized-order" ? new string('x', 256) : "order_test";
        SetBody(controller, Body(order, "pay_test"));
        if (scenario == "missing-signature") controller.Request.Headers.Remove("X-Razorpay-Signature");
        if (scenario == "invalid-signature") controller.Request.Headers["X-Razorpay-Signature"] = "not-a-signature";

        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.RazorpayWebhook(default));
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, Json(result.Value).GetProperty("error").GetString());
        Assert.Equal(original, context.CurrentTenantId);
    }

    [Part2TenantBoundaryPostgresTheory]
    [InlineData("unknown", 503)]
    [InlineData("injection", 503)]
    [InlineData("ambiguous-tenant", 503)]
    [InlineData("ambiguous-same-tenant", 503)]
    [InlineData("wrong-method", 503)]
    [InlineData("wrong-amount", 400)]
    [InlineData("wrong-payment-id", 400)]
    [InlineData("foreign-invoice", 400)]
    public async Task Legacy_RequiresExactUnambiguousOrderAndScopedPayableInvoice(string scenario, int status)
    {
        var (invoice, payment) = await SeedAsync();
        await using (var seed = fixture.OwnerDb())
        {
            if (scenario.StartsWith("ambiguous", StringComparison.Ordinal))
                seed.Payments.Add(new Payment { Id = Guid.NewGuid(), InvoiceId = invoice.Id,
                    TenantId = scenario == "ambiguous-tenant" ? fixture.TenantB : fixture.TenantA,
                    RazorpayOrderId = payment.RazorpayOrderId, Amount = 40, Method = PaymentMethod.Razorpay });
            if (scenario is "wrong-method" or "wrong-payment-id" or "foreign-invoice")
            {
                seed.Attach(payment);
                if (scenario == "wrong-method") payment.Method = PaymentMethod.Cash;
                if (scenario == "wrong-payment-id") payment.RazorpayPaymentId = "pay_other";
                if (scenario == "foreign-invoice")
                {
                    var other = Bill(fixture.TenantB, invoice.InvoiceNumber, 100);
                    seed.Invoices.Add(other);
                    payment.InvoiceId = other.Id;
                }
            }
            await seed.SaveChangesAsync();
        }
        var context = new TenantContext();
        using var original = context.PushTenant(fixture.TenantB);
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync();
        var controller = Controller(db, context);
        var order = scenario == "unknown" ? payment.RazorpayOrderId + "x"
            : scenario == "injection" ? "' OR true --" : payment.RazorpayOrderId;
        SetBody(controller, Body(order, "pay_" + Guid.NewGuid().ToString("N"), scenario == "wrong-amount" ? 4100 : 4000));

        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(await controller.RazorpayWebhook(default)).StatusCode);
        await AssertScopeAsync(db, context);
        await using var verify = fixture.OwnerDb();
        Assert.Equal(0, (await verify.Invoices.SingleAsync(i => i.Id == invoice.Id)).PaidAmount);
        Assert.Equal(PaymentStatus.Pending, (await verify.Payments.SingleAsync(p => p.Id == payment.Id)).Status);
        Assert.Empty(await verify.FinanceLedgerEntries.Where(e => e.ReferenceId == payment.Id || e.ReferenceId == payment.InvoiceId).ToListAsync());
        Assert.Empty(await verify.ProcessedWebhookEvents.Where(e => e.PaymentId == payment.Id).ToListAsync());
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Legacy_TenantDedupeCannotBeSuppressedByOtherTenant_AndRestoresMaintenance()
    {
        var (invoice, payment) = await SeedAsync();
        var eventId = "pay_" + Guid.NewGuid().ToString("N");
        await using (var seed = fixture.OwnerDb())
        {
            seed.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { Id = Guid.NewGuid(), TenantId = fixture.TenantB,
                Provider = "razorpay", EventId = eventId, Status = ProcessedWebhookEvent.Processed });
            await seed.SaveChangesAsync();
        }
        var context = new TenantContext();
        using var original = context.PushMaintenance();
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync();
        var controller = Controller(db, context);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SetBody(controller, Body(payment.RazorpayOrderId, eventId));
            var result = Assert.IsType<OkObjectResult>(await controller.RazorpayWebhook(default));
            Assert.Equal(attempt == 0 ? "ok" : "duplicate", Json(result.Value).GetProperty("status").GetString());
            Assert.Null(context.CurrentTenantId);
            Assert.True(context.IsPlatformAdmin);
        }
        await using var verify = fixture.OwnerDb();
        Assert.Equal(40, (await verify.Invoices.SingleAsync(i => i.Id == invoice.Id)).PaidAmount);
        var events = await verify.ProcessedWebhookEvents.Where(e => e.EventId == eventId).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.TenantId == fixture.TenantA && e.PaymentId == payment.Id);
        Assert.Contains(events, e => e.TenantId == fixture.TenantB && e.PaymentId == null);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Legacy_DatabaseExceptionAfterRouting_RestoresScopeAndLeavesMoneyUntouched()
    {
        var (invoice, payment) = await SeedAsync();
        var context = new TenantContext();
        using var original = context.PushTenant(fixture.TenantB);
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync();
        var controller = Controller(db, context);
        SetBody(controller, Body(payment.RazorpayOrderId, "pay_" + Guid.NewGuid().ToString("N")));
        await using var owner = fixture.OwnerDb();
        // The definer can still resolve the order; the subsequent normal payment read must be denied.
        await owner.Database.ExecuteSqlRawAsync("REVOKE SELECT ON public.\"Payments\" FROM app_user");
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => controller.RazorpayWebhook(default));
            Assert.Equal("42501", error.SqlState);
            await AssertScopeAsync(db, context);
            Assert.Equal(0, (await owner.Invoices.SingleAsync(i => i.Id == invoice.Id)).PaidAmount);
            Assert.Equal(PaymentStatus.Pending, (await owner.Payments.SingleAsync(p => p.Id == payment.Id)).Status);
        }
        finally
        {
            await owner.Database.ExecuteSqlRawAsync("GRANT SELECT ON public.\"Payments\" TO app_user");
        }
    }

    private async Task AssertScopeAsync(HospitalCrmDbContext db, TenantContext context)
    {
        Assert.Equal(fixture.TenantB, context.CurrentTenantId);
        Assert.False(context.IsPlatformAdmin);
        Assert.Equal(fixture.TenantB.ToString(), await db.Database.SqlQueryRaw<string>(
            "SELECT current_setting('app.current_tenant_id') AS \"Value\"").SingleAsync());
        Assert.Equal("false", await db.Database.SqlQueryRaw<string>(
            "SELECT current_setting('app.is_platform_admin') AS \"Value\"").SingleAsync());
    }

    private async Task<(Invoice Invoice, Payment Payment)> SeedAsync()
    {
        await using var seed = fixture.OwnerDb();
        var invoice = Bill(fixture.TenantA, 101 + await seed.Invoices.CountAsync(), 100);
        var payment = new Payment { Id = Guid.NewGuid(), TenantId = fixture.TenantA, InvoiceId = invoice.Id,
            Method = PaymentMethod.Razorpay, Amount = 40, RazorpayOrderId = "order_" + Guid.NewGuid().ToString("N"),
            Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Pending };
        seed.AddRange(invoice, payment);
        await seed.SaveChangesAsync();
        return (invoice, payment);
    }

    private static InvoicesController Controller(HospitalCrmDbContext db, ITenantContext? context) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Razorpay:WebhookSecret"] = Secret }).Build(), null!, context)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

    private static string Body(string? order, string payment, int amount = 4000) => JsonSerializer.Serialize(new
    {
        @event = "payment.captured",
        payload = new { payment = new { entity = new { id = payment, order_id = order, status = "captured", currency = "INR", amount } } }
    });

    private static void SetBody(InvoicesController controller, string body)
    {
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.Request.Headers["X-Razorpay-Signature"] = PaymentWebhookCodec.SignHmac(Secret, body);
    }
}
