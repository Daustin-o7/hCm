using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part1IdempotencyMiddlewareTests
{
    [Theory]
    [InlineData("/api/v1/pharmacy/checkout")]
    [InlineData("/api/v1/pharmacy/dispense")]
    [InlineData("/API/V1/PHARMACY/CHECKOUT/")]
    [InlineData("/api/v1/pharmacy/dispense/")]
    [InlineData("/api/v1/patients")]
    [InlineData("/API/V1/PATIENTS/")]
    [InlineData("/api/v1/platform-admin/tenants")]
    [InlineData("/API/V1/PLATFORM-ADMIN/TENANTS/")]
    [InlineData("/api/v1/invoices/11111111-1111-1111-1111-111111111111/payment")]
    [InlineData("/API/V1/INVOICES/11111111-1111-1111-1111-111111111111/PAYMENT/")]
    [InlineData("/api/v1/invoices/11111111-1111-1111-1111-111111111111/payments/report")]
    [InlineData("/API/V1/INVOICES/11111111-1111-1111-1111-111111111111/PAYMENTS/REPORT/")]
    [InlineData("/api/v1/invoices/11111111-1111-1111-1111-111111111111/payments/confirm")]
    [InlineData("/API/V1/INVOICES/11111111-1111-1111-1111-111111111111/PAYMENTS/CONFIRM/")]
    [InlineData("/api/v1/invoices/11111111111111111111111111111111/payment")]
    public async Task DurableRoutes_AlwaysReachPayloadValidation(string path)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        var middleware = new IdempotencyMiddleware(context =>
        {
            context.Response.StatusCode = ++calls == 1 ? 201 : 409;
            return Task.CompletedTask;
        }, cache, NullLogger<IdempotencyMiddleware>.Instance);
        var first = Context(path);
        var changedPayload = Context(path);

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(changedPayload);

        Assert.Equal(2, calls);
        Assert.Equal(409, changedPayload.Response.StatusCode);
        Assert.False(changedPayload.Response.Headers.ContainsKey("X-Idempotency-Replay"));
    }

    [Theory]
    [InlineData("/api/v1/pharmacy/checkout-extra")]
    [InlineData("/api/v1/patients-extra")]
    [InlineData("/api/v1/patients/check-duplicate")]
    [InlineData("/api/v1/platform-admin/tenants-extra")]
    [InlineData("/api/v1/invoices/11111111-1111-1111-1111-111111111111/payments/confirm-extra")]
    public async Task DurableBypass_DoesNotMatchAnUnrelatedRoute(string path)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        var middleware = new IdempotencyMiddleware(context =>
        {
            calls++;
            context.Response.StatusCode = 201;
            return Task.CompletedTask;
        }, cache, NullLogger<IdempotencyMiddleware>.Instance);
        await middleware.InvokeAsync(Context(path));
        var retry = Context(path);
        await middleware.InvokeAsync(retry);

        Assert.Equal(1, calls);
        Assert.Equal("true", retry.Response.Headers["X-Idempotency-Replay"]);
    }

    [Theory]
    [InlineData("Idempotency-Key", false)]
    [InlineData("X-Idempotency-Key", false)]
    [InlineData("Idempotency-Key", true)]
    [InlineData("X-Idempotency-Key", true)]
    public async Task PatientReplay_RechecksChangedPayloadAndReturnsWithdrawal(string header, bool withdraw)
    {
        await using var db = Part1SaleReadinessTests.MemoryDb();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new PatientsController(db, new FakePatientSearchService());
        Part1SaleReadinessTests.Caller(controller, Guid.NewGuid(), Guid.NewGuid(), "Receptionist");
        var user = controller.User;
        var calls = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            calls++;
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            var request = await JsonSerializer.DeserializeAsync<PatientRegisterRequest>(context.Request.Body);
            var result = Assert.IsAssignableFrom<ObjectResult>(await controller.Register(request!, CancellationToken.None));
            context.Response.StatusCode = result.StatusCode ?? 200;
            await context.Response.WriteAsync(JsonSerializer.Serialize(result.Value));
        }, cache, NullLogger<IdempotencyMiddleware>.Instance);
        var original = new PatientRegisterRequest("Synthetic patient", "9876543210", null, 30, "Female", null,
            new(true, "care_delivery"), $"IDEMP-PAT-{Guid.NewGuid()}");
        DefaultHttpContext Request(PatientRegisterRequest payload)
        {
            var context = Context("/api/v1/patients");
            context.User = user;
            context.Request.Headers.Clear();
            context.Request.Headers[header] = original.IdempotencyKey;
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
            return context;
        }
        var first = Request(original);
        await middleware.InvokeAsync(first);
        Assert.Equal(201, first.Response.StatusCode);
        var consent = await db.PatientConsents.SingleAsync();
        if (withdraw)
            Assert.IsType<OkObjectResult>(await controller.WithdrawConsent(consent.PatientId, consent.Id, CancellationToken.None));
        db.ChangeTracker.Clear();

        var retry = Request(withdraw ? original : original with { Name = "Changed payload" });
        await middleware.InvokeAsync(retry);
        Assert.Equal(2, calls);
        Assert.False(retry.Response.Headers.ContainsKey("X-Idempotency-Replay"));
        retry.Response.Body.Position = 0;
        var body = (await JsonDocument.ParseAsync(retry.Response.Body)).RootElement;
        if (withdraw)
        {
            Assert.Equal(200, retry.Response.StatusCode);
            Assert.True(body.GetProperty("idempotentReplay").GetBoolean());
            Assert.Equal(consent.WithdrawnAt, Assert.Single(body.GetProperty("consents").EnumerateArray())
                .GetProperty("withdrawnAt").GetDateTimeOffset());
        }
        else
        {
            Assert.Equal(409, retry.Response.StatusCode);
            Assert.Equal("idempotency_key_conflict", body.GetProperty("error").GetString());
        }
        Assert.Single(db.Patients);
        Assert.Single(db.PatientConsents);
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Headers["Idempotency-Key"] = "same-key";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
