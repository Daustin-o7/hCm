using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RoutingMigration = Hospital_CRM.Infrastructure.Migrations.PublicTokenRouting;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part2TenantBoundaryIntegrationTests(Part2TenantBoundaryPostgresFixture fixture)
    : IClassFixture<Part2TenantBoundaryPostgresFixture>
{
    [Part2TenantBoundaryPostgresFact]
    public async Task Migration_ReplacesAnonymousPolicy_AndRestrictsDefinerPrivileges()
    {
        var (_, submission) = await fixture.SeedPrecheckAsync();
        await using var db = fixture.AppDb(new TenantContext());
        Assert.Empty(await db.PrecheckSubmissions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync("UPDATE public.\"PrecheckSubmissions\" SET \"ChiefComplaint\" = 'anonymous', \"SubmittedAt\" = now()"));
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT count(*) = 2 AND bool_and(p.prosecdef AND r.rolname = 'public_token_router'
                AND p.proconfig = ARRAY['search_path=pg_catalog']
                AND NOT r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls
                AND NOT EXISTS (SELECT FROM aclexplode(p.proacl) acl WHERE acl.grantee = 0)
                AND has_function_privilege('app_user', p.oid, 'EXECUTE')) AS "Value"
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_roles r ON r.oid = p.proowner
            WHERE n.nspname = 'public' AND p.proname IN ('resolve_public_token', 'resolve_razorpay_order')
            """).SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("""
            SELECT has_table_privilege('public_token_router', 'public."PrecheckSubmissions"', 'SELECT')
                OR has_column_privilege('public_token_router', 'public."PrecheckSubmissions"', 'ChiefComplaint', 'SELECT')
                OR has_column_privilege('public_token_router', 'public."Payments"', 'Amount', 'SELECT')
                OR has_table_privilege('public_token_router', 'public."Patients"', 'SELECT')
                OR pg_has_role('app_user', 'public_token_router', 'MEMBER') AS "Value"
            """).SingleAsync());
        await using var owner = fixture.OwnerDb();
        Assert.Null((await owner.PrecheckSubmissions.SingleAsync(s => s.Id == submission.Id)).SubmittedAt);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Migration_DownRestoresPriorPolicy_AndUpReappliesWithinTransaction()
    {
        await fixture.SeedPrecheckAsync();
        await using var owner = fixture.OwnerDb();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        foreach (var operation in new RoutingMigration().DownOperations.Cast<SqlOperation>())
            await owner.Database.ExecuteSqlRawAsync(operation.Sql);
        Assert.True(await owner.Database.SqlQueryRaw<bool>("""
            SELECT to_regprocedure('public.resolve_public_token(text,text)') IS NULL
                AND to_regprocedure('public.resolve_razorpay_order(text)') IS NULL
                AND NOT has_column_privilege('public_token_router', 'public."Payments"', 'RazorpayOrderId', 'SELECT') AS "Value"
            """).SingleAsync());
        await owner.Database.ExecuteSqlRawAsync("SET LOCAL ROLE app_user");
        Assert.True(await owner.PrecheckSubmissions.IgnoreQueryFilters().AnyAsync());
        await owner.Database.ExecuteSqlRawAsync("RESET ROLE");
        foreach (var operation in new RoutingMigration().UpOperations.Cast<SqlOperation>())
            await owner.Database.ExecuteSqlRawAsync(operation.Sql);
        await owner.Database.ExecuteSqlRawAsync("SET LOCAL ROLE app_user");
        Assert.Empty(await owner.PrecheckSubmissions.IgnoreQueryFilters().ToListAsync());
        await transaction.RollbackAsync();
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task RazorpayOrderRouting_ExactUnambiguousBindingOnly_WithoutReadingPayments()
    {
        var order = "order_" + Guid.NewGuid().ToString("N");
        var invoice = Bill(fixture.TenantA, 51, 100);
        var payment = new Payment { Id = Guid.NewGuid(), TenantId = fixture.TenantA, InvoiceId = invoice.Id,
            RazorpayOrderId = order, Amount = 100, Method = PaymentMethod.Razorpay, Status = PaymentStatus.Pending };
        await using var owner = fixture.OwnerDb();
        owner.AddRange(invoice, payment);
        await owner.SaveChangesAsync();
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        async Task<PublicTokenBinding?> Route(string? key) => await db.Database.SqlQuery<PublicTokenBinding>(
            $"SELECT \"TenantId\", \"ResourceId\" FROM public.resolve_razorpay_order({key})").SingleOrDefaultAsync();
        Assert.Equal(new PublicTokenBinding(fixture.TenantA, payment.Id), await Route(order));
        Assert.Null(await Route(order + "x"));
        Assert.Null(await Route("' OR true --"));
        Assert.Null(await Route(null));
        Assert.Null(await Route(""));
        Assert.Empty(await db.Payments.IgnoreQueryFilters().ToListAsync());
        Assert.Null(context.CurrentTenantId);
        Assert.False(context.IsPlatformAdmin);
        var foreignInvoice = Bill(fixture.TenantB, 51, 100);
        owner.AddRange(foreignInvoice, new Payment { Id = Guid.NewGuid(), TenantId = fixture.TenantB,
            InvoiceId = foreignInvoice.Id, RazorpayOrderId = order, Amount = 100, Method = PaymentMethod.Razorpay });
        await owner.SaveChangesAsync();
        Assert.Null(await Route(order));
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task BusinessWebhook_SignedTenantOverridesAmbientTenant_AndRestoresScope()
    {
        const string secret = "synthetic-business-secret";
        var path = Guid.NewGuid().ToString("N");
        var invoice = Bill(fixture.TenantA, 61, 100);
        var foreignInvoice = Bill(fixture.TenantB, 61, 100);
        await using (var owner = fixture.OwnerDb())
        {
            owner.AddRange(invoice, foreignInvoice, new TenantIntegration { Id = Guid.NewGuid(), TenantId = fixture.TenantA,
                Provider = "paytm", Enabled = true, WebhookPathToken = path, SecretProtected = new TestSecretProtector().Protect(secret) });
            await owner.SaveChangesAsync();
        }
        const string body = """{"txnId":"boundary-event","amount":40,"status":"TXN_SUCCESS","reference":"INV-2026-27-000061","bankTxnId":"boundary-UTR"}""";
        var context = new TenantContext();
        using var originalScope = context.PushTenant(fixture.TenantB);
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync();
        var controller = new PaymentIntegrationsController(db, new TestSecretProtector(), new ConfigurationBuilder().Build(),
            NullLogger<PaymentIntegrationsController>.Instance, context)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        SetBody(controller, body, "wrong-secret", "X-Paytm-Signature");
        Assert.IsType<BadRequestObjectResult>(await controller.Receive("paytm", path, default));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SetBody(controller, body, secret, "X-Paytm-Signature");
            Assert.Equal(attempt == 0 ? "ok" : "duplicate", Json(Assert.IsType<OkObjectResult>(
                await controller.Receive("paytm", path, default)).Value).GetProperty("status").GetString());
            Assert.Equal(fixture.TenantB, context.CurrentTenantId);
            var visible = await db.Invoices.IgnoreQueryFilters().ToListAsync();
            Assert.Contains(visible, i => i.Id == foreignInvoice.Id);
            Assert.All(visible, i => Assert.Equal(fixture.TenantB, i.TenantId));
        }
        await using var verify = fixture.OwnerDb();
        Assert.Equal(40, (await verify.Invoices.SingleAsync(i => i.Id == invoice.Id)).PaidAmount);
        Assert.Equal(0, (await verify.Invoices.SingleAsync(i => i.Id == foreignInvoice.Id)).PaidAmount);
        var payment = Assert.Single(await verify.Payments.Where(p => p.InvoiceId == invoice.Id).ToListAsync());
        var entries = await verify.FinanceLedgerEntries.Where(e => e.TenantId == fixture.TenantA
            && e.EntryType == FinanceEntryType.PaymentConfirmed
            && (e.ReferenceId == payment.Id || e.ReferenceId == invoice.Id)).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ReferenceId == payment.Id && e.AccountCategory == "Bank" && e.DebitAmount == 40);
        Assert.Contains(entries, e => e.ReferenceId == invoice.Id && e.AccountCategory == "AccountsReceivable" && e.CreditAmount == 40);
        Assert.Equal(entries.Sum(e => e.DebitAmount), entries.Sum(e => e.CreditAmount));
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task SubscriptionWebhook_SignedTenantScopesMirrorAndDedupe_AndRestoresScope()
    {
        const string secret = "synthetic-subscription-secret";
        await using (var seed = fixture.OwnerDb())
        {
            await seed.Clinics.ExecuteUpdateAsync(s => s.SetProperty(c => c.SubscriptionStatus, SubscriptionStatus.Trial));
            await seed.Tenants.ExecuteUpdateAsync(s => s.SetProperty(t => t.SubscriptionStatus, SubscriptionStatus.Trial));
        }
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "subscription.activated",
            payload = new { subscription = new { entity = new
                { id = "sub_boundary", current_end = 2100000000, notes = new { tenant_id = fixture.TenantA } } } }
        });
        var context = new TenantContext();
        using var originalScope = context.PushTenant(fixture.TenantB);
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SubscriptionBilling:WebhookSecrets:razorpay"] = secret }).Build();
        var controller = new SubscriptionWebhooksController(db, config, NullLogger<SubscriptionWebhooksController>.Instance, context)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        SetBody(controller, body, "wrong-secret");
        Assert.IsType<BadRequestObjectResult>(await controller.Receive("razorpay", default));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SetBody(controller, body, secret);
            Assert.Equal(attempt == 0 ? "ok" : "duplicate", Json(Assert.IsType<OkObjectResult>(
                await controller.Receive("razorpay", default)).Value).GetProperty("status").GetString());
            Assert.Equal(fixture.TenantB, context.CurrentTenantId);
            Assert.Equal(fixture.ClinicB, (await db.Clinics.IgnoreQueryFilters().SingleAsync()).Id);
        }
        await using var owner = fixture.OwnerDb();
        Assert.Equal(SubscriptionStatus.Active, (await owner.Clinics.SingleAsync(c => c.Id == fixture.ClinicA)).SubscriptionStatus);
        Assert.NotEqual(SubscriptionStatus.Active, (await owner.Clinics.SingleAsync(c => c.Id == fixture.ClinicB)).SubscriptionStatus);
        Assert.Single(await owner.ProcessedWebhookEvents.Where(e => e.Provider == "razorpay:subscription").ToListAsync());
        SetBody(controller, body.Replace(fixture.TenantA.ToString(), Guid.Empty.ToString()), secret);
        Assert.IsType<AcceptedResult>(await controller.Receive("razorpay", default));
        Assert.Equal(fixture.TenantB, context.CurrentTenantId);
    }

    private static void SetBody(ControllerBase controller, string body, string secret, string header = "X-Razorpay-Signature")
    {
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.Request.Headers[header] = PaymentWebhookCodec.SignHmac(secret, body);
    }
}
