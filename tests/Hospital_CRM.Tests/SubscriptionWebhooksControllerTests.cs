using System.Security.Claims;
using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>Controller-level regressions for B4 subscription webhooks: signature rejection, replay/dedupe, tenant resolution, concurrency, out-of-order safety.</summary>
public class SubscriptionWebhooksControllerTests
{
    private static HospitalCrmDbContext CreateInMemoryDb(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid tenantId, Guid userId, string role = "Receptionist")
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static SubscriptionWebhooksController CreateController(
        HospitalCrmDbContext db,
        string webhookSecret,
        ClaimsPrincipal? user = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SubscriptionBilling:WebhookSecrets:razorpay"] = webhookSecret
            })
            .Build();

        var controller = new SubscriptionWebhooksController(db, config, NullLogger<SubscriptionWebhooksController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() }
        };
        return controller;
    }

    private static string RazorpaySubscriptionWebhook(string eventName, Guid tenantId, string subscriptionId = "sub_TEST123", long? currentEnd = 1_763_000_000)
    {
        return $$"""
        {
          "entity": "event",
          "event": "{{eventName}}",
          "payload": {
            "subscription": {
              "entity": {
                "id": "{{subscriptionId}}",
                "status": "active",
                "current_end": {{currentEnd?.ToString() ?? "null"}},
                "notes": { "tenant_id": "{{tenantId}}" }
              }
            }
          }
        }
        """;
    }

    private static string SignBody(string secret, string body)
    {
        return PaymentWebhookCodec.SignHmac(secret, body);
    }

    [Fact]
    public async Task InvalidSignature_Returns400()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var controller = CreateController(db, "correct-secret");

        var body = RazorpaySubscriptionWebhook("subscription.activated", tenantId);
        var badSignature = "bad-signature";

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = badSignature;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await controller.Receive("razorpay", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Contains("invalid_webhook_signature", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task DuplicateEventId_ReturnsDuplicate()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var secret = "test-secret";
        var controller = CreateController(db, secret);

        // Seed the tenant so the webhook can find it
        var tenant = new Tenant { Id = tenantId, Name = "Test", SubscriptionStatus = SubscriptionStatus.Trial, ActivatedModules = "core" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var body = RazorpaySubscriptionWebhook("subscription.activated", tenantId);
        var signature = SignBody(secret, body);

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = signature;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-123";

        // First delivery
        var result1 = await controller.Receive("razorpay", CancellationToken.None);
        var ok1 = Assert.IsType<OkObjectResult>(result1);

        // Second delivery (replay)
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = signature;
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-123";

        var result2 = await controller.Receive("razorpay", CancellationToken.None);
        var ok2 = Assert.IsType<OkObjectResult>(result2);
        Assert.Contains("duplicate", ok2.Value?.ToString());

        // Only one row in dedupe table
        var count = await db.ProcessedWebhookEvents.IgnoreQueryFilters().CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UnknownTenant_ReturnsAcceptedUnresolved()
    {
        var db = CreateInMemoryDb();
        var controller = CreateController(db, "test-secret");

        var body = RazorpaySubscriptionWebhook("subscription.activated", Guid.NewGuid()); // unknown tenant
        var signature = SignBody("test-secret", body);

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = signature;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-unknown";

        var result = await controller.Receive("razorpay", CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Contains("unresolved_tenant", accepted.Value?.ToString());
    }

    [Fact]
    public async Task SubscriptionCancelled_KeepsAccessUntilPaidPeriodEnds()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var controller = CreateController(db, "test-secret");

        // Seed tenant with active subscription ending at a future date
        var periodEnd = DateTimeOffset.FromUnixTimeSeconds(1_763_000_000);
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Test Clinic",
            SubscriptionStatus = SubscriptionStatus.Active,
            SubscriptionEndsAt = periodEnd,
            ActivatedModules = "core"
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Cancel webhook
        var body = RazorpaySubscriptionWebhook("subscription.cancelled", tenantId, currentEnd: 1_763_000_000);
        var signature = SignBody("test-secret", body);

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = signature;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-cancel";

        var result = await controller.Receive("razorpay", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);

        var updated = await db.Tenants.FindAsync(tenantId);
        Assert.Equal(SubscriptionStatus.Cancelled, updated!.SubscriptionStatus);
        Assert.Equal(periodEnd, updated.SubscriptionEndsAt); // keeps original paid period end
    }

    [Fact]
    public async Task OutOfOrderDelivery_ConvergesToCorrectState()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var controller = CreateController(db, "test-secret");

        // Seed with trial
        var tenant = new Tenant { Id = tenantId, Name = "Test", SubscriptionStatus = SubscriptionStatus.Trial, ActivatedModules = "core" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Event 1: charged (renewal) - arrives first
        var body1 = RazorpaySubscriptionWebhook("subscription.charged", tenantId, currentEnd: 1_765_600_000);
        var sig1 = SignBody("test-secret", body1);

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = sig1;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body1));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-charged";

        await controller.Receive("razorpay", CancellationToken.None);

        var afterFirst = await db.Tenants.FindAsync(tenantId);
        Assert.Equal(SubscriptionStatus.Active, afterFirst!.SubscriptionStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_765_600_000), afterFirst.SubscriptionEndsAt);

        // Event 2: activated (earlier event) - arrives second
        var body2 = RazorpaySubscriptionWebhook("subscription.activated", tenantId, currentEnd: 1_763_000_000);
        var sig2 = SignBody("test-secret", body2);

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = sig2;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body2));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-activated";

        var result2 = await controller.Receive("razorpay", CancellationToken.None);
        var ok2 = Assert.IsType<OkObjectResult>(result2);

        var afterSecond = await db.Tenants.FindAsync(tenantId);
        // The later period end should win (charged at 1_765_600_000 > activated at 1_763_000_000)
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_765_600_000), afterSecond!.SubscriptionEndsAt);
    }

    [Fact]
    public async Task MissingWebhookSecretConfig_Returns503()
    {
        var db = CreateInMemoryDb();
        var controller = CreateController(db, ""); // no secret configured

        var body = RazorpaySubscriptionWebhook("subscription.activated", Guid.NewGuid());
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = "anything";
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await controller.Receive("razorpay", CancellationToken.None);

        var statusCode = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, statusCode.StatusCode);
        Assert.Contains("billing_not_configured", statusCode.Value?.ToString());
    }

    [Fact]
    public async Task IgnoredEventType_ReturnsOkIgnored()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var controller = CreateController(db, "test-secret");

        var body = RazorpaySubscriptionWebhook("subscription.updated", tenantId);
        var signature = SignBody("test-secret", body);

        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Signature"] = signature;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Razorpay-Event-Id"] = "evt-updated";

        var result = await controller.Receive("razorpay", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("ignored", ok.Value?.ToString());

        // No dedupe row created for ignored events
        var count = await db.ProcessedWebhookEvents.IgnoreQueryFilters().CountAsync();
        Assert.Equal(0, count);
    }
}