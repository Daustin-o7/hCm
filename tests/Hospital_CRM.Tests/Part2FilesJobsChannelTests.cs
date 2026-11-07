using System.Net;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Hospital_CRM.Tests.Part2FilesJobsNotificationTests;

namespace Hospital_CRM.Tests;

public sealed class Part2FilesJobsChannelTests
{
    [Theory]
    [InlineData(200, "{\"messages\":[{\"id\":\"wamid.synthetic\"}]}", NotificationAcceptance.Accepted)]
    [InlineData(200, "{}", NotificationAcceptance.Unknown)]
    [InlineData(200, "[]", NotificationAcceptance.Unknown)]
    [InlineData(200, "{\"messages\":[null]}", NotificationAcceptance.Unknown)]
    [InlineData(200, "not-json", NotificationAcceptance.Unknown)]
    [InlineData(400, "{\"error\":\"private provider payload\"}", NotificationAcceptance.Rejected)]
    [InlineData(429, "{}", NotificationAcceptance.Retryable)]
    [InlineData(500, "{}", NotificationAcceptance.Unknown)]
    public async Task Provider_result_is_classified_without_fabricating_delivery(int status, string body, NotificationAcceptance expected)
    {
        await using var fixture = new Fixture();
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        await AddIntegration(fixture, fixture.TenantId, "111111", "tenant-A-token");
        var transport = new ControlledTransport { Status = status, Body = body };
        using var http = new HttpClient(transport);
        var service = new WhatsAppNotificationChannel(fixture.Db, fixture.Context, new TestSecrets(), http, Config());
        var result = await service.SendAsync(Message(fixture.TenantId), default);
        Assert.Equal(expected, result.Acceptance);
        Assert.Equal(expected == NotificationAcceptance.Accepted ? "wamid.synthetic" : null, result.ProviderMessageId);
        Assert.Equal("Bearer tenant-A-token", transport.Authorization);
        Assert.EndsWith("/111111/messages", transport.Uri!.AbsolutePath);
        Assert.Equal(1, transport.Calls);
        Assert.DoesNotContain("private provider payload", result.FailureCode ?? "");
    }

    [Fact]
    public async Task Secrets_are_selected_by_explicit_tenant_without_global_fallback()
    {
        await using var fixture = new Fixture();
        var second = Guid.NewGuid();
        using (fixture.Context.PushTenant(fixture.TenantId)) await AddIntegration(fixture, fixture.TenantId, "111111", "tenant-A-token");
        using (fixture.Context.PushTenant(second)) await AddIntegration(fixture, second, "222222", "tenant-B-token");
        var transport = new ControlledTransport();
        using var http = new HttpClient(transport);
        var service = new WhatsAppNotificationChannel(fixture.Db, fixture.Context, new TestSecrets(), http, Config());
        using (fixture.Context.PushTenant(fixture.TenantId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(Message(second), default));
            Assert.Equal(0, transport.Calls);
            await service.SendAsync(Message(fixture.TenantId), default);
            Assert.Equal("Bearer tenant-A-token", transport.Authorization);
        }
        using (fixture.Context.PushTenant(second))
        {
            await service.SendAsync(Message(second), default);
            Assert.Equal("Bearer tenant-B-token", transport.Authorization);
            Assert.EndsWith("/222222/messages", transport.Uri!.AbsolutePath);
        }
    }

    [Fact]
    public async Task Missing_configuration_and_timeout_are_not_sent()
    {
        await using var fixture = new Fixture();
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        var transport = new ControlledTransport { ThrowTimeout = true };
        using var http = new HttpClient(transport);
        var service = new WhatsAppNotificationChannel(fixture.Db, fixture.Context, new TestSecrets(), http, Config());
        var missing = await service.SendAsync(Message(fixture.TenantId), default);
        Assert.Equal(NotificationAcceptance.Rejected, missing.Acceptance);
        Assert.Equal("channel_not_configured", missing.FailureCode);
        Assert.Equal(0, transport.Calls);
        await AddIntegration(fixture, fixture.TenantId, "111111", "tenant-A-token");
        var timeout = await service.SendAsync(Message(fixture.TenantId), default);
        Assert.Equal(NotificationAcceptance.Unknown, timeout.Acceptance);
        Assert.Null(timeout.ProviderMessageId);
    }

    [Theory]
    [InlineData("9876543210")]
    [InlineData("+919876543210")]
    [InlineData("919876543210")]
    public async Task Registered_Indian_mobile_and_international_forms_address_the_same_recipient(string phone)
    {
        await using var fixture = new Fixture();
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        await AddIntegration(fixture, fixture.TenantId, "111111", "synthetic-token");
        var transport = new ControlledTransport();
        using var http = new HttpClient(transport);
        var service = new WhatsAppNotificationChannel(fixture.Db, fixture.Context, new TestSecrets(), http, Config());
        await service.SendAsync(Message(fixture.TenantId) with { Phone = phone }, default);
        using var payload = JsonDocument.Parse(transport.RequestBody!);
        Assert.Equal("919876543210", payload.RootElement.GetProperty("to").GetString());
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["WhatsApp:GraphApiVersion"] = "v22.0" }).Build();

    private static NotificationChannelMessage Message(Guid tenant) => new(tenant, NotificationChannel.WhatsApp, "+919876543210", "appointment_confirmation", ["synthetic"]);

    private static async Task AddIntegration(Fixture fixture, Guid tenantId, string phoneId, string token)
    {
        fixture.Db.TenantIntegrations.Add(new TenantIntegration { Id = Guid.NewGuid(), TenantId = tenantId, Provider = "whatsapp",
            MerchantId = phoneId, SecretProtected = token, Enabled = true });
        await fixture.Db.SaveChangesAsync();
    }

    private sealed class TestSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string? Unprotect(string? protectedValue) => protectedValue;
    }

    private sealed class ControlledTransport : HttpMessageHandler
    {
        public int Status { get; set; } = 200;
        public string Body { get; set; } = "{\"messages\":[{\"id\":\"wamid.synthetic\"}]}";
        public bool ThrowTimeout { get; set; }
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            if (ThrowTimeout) throw new TaskCanceledException("synthetic timeout with private payload");
            return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
