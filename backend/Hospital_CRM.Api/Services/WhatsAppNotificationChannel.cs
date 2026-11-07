using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public sealed class WhatsAppNotificationChannel(HospitalCrmDbContext db, ITenantContext tenant,
    ISecretProtector secrets, HttpClient http, IConfiguration config) : INotificationChannel
{
    public async Task<NotificationChannelResult> SendAsync(NotificationChannelMessage message, CancellationToken ct)
    {
        if (tenant.CurrentTenantId != message.TenantId || message.TenantId == Guid.Empty)
            throw new InvalidOperationException("notification_tenant_required");
        if (message.Channel != NotificationChannel.WhatsApp)
            return new(NotificationAcceptance.Rejected, FailureCode: "channel_not_configured");
        var integration = await db.TenantIntegrations.AsNoTracking().SingleOrDefaultAsync(i =>
            i.TenantId == message.TenantId && i.Provider == "whatsapp" && i.Enabled, ct);
        var version = config["WhatsApp:GraphApiVersion"];
        if (integration == null || string.IsNullOrEmpty(integration.MerchantId) ||
            string.IsNullOrEmpty(version) || !Regex.IsMatch(version, @"^v[0-9]{1,2}\.[0-9]{1,2}$") ||
            !Regex.IsMatch(integration.MerchantId, "^[0-9]{1,40}$"))
            return new(NotificationAcceptance.Rejected, FailureCode: "channel_not_configured");
        var token = secrets.Unprotect(integration.SecretProtected);
        if (string.IsNullOrEmpty(token)) return new(NotificationAcceptance.Rejected, FailureCode: "channel_secret_unavailable");
        if (!Regex.IsMatch(message.TemplateName, "^[a-z0-9_]{1,100}$") ||
            !Regex.IsMatch(message.Phone, @"^\+?[1-9][0-9]{7,14}$"))
            return new(NotificationAcceptance.Rejected, FailureCode: "channel_payload_invalid");
        // Registration stores Indian mobile numbers without their country code.
        var phone = Regex.IsMatch(message.Phone, "^[6-9][0-9]{9}$") ? "91" + message.Phone : message.Phone.TrimStart('+');

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{version}/{integration.MerchantId}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            messaging_product = "whatsapp",
            to = phone,
            type = "template",
            template = new
            {
                name = message.TemplateName,
                language = new { code = config["WhatsApp:LanguageCode"] ?? "en" },
                components = message.Parameters.Count == 0 ? Array.Empty<object>() :
                    [new { type = "body", parameters = message.Parameters.Select(p => new { type = "text", text = p }).ToArray() }]
            }
        });
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(NotificationAcceptance.Retryable, FailureCode: "provider_rate_limited");
            if (!response.IsSuccessStatusCode)
                return new((int)response.StatusCode >= 500 ? NotificationAcceptance.Unknown : NotificationAcceptance.Rejected,
                    FailureCode: (int)response.StatusCode >= 500 ? "provider_acceptance_unknown" : "provider_rejected");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0 && messages[0].ValueKind == JsonValueKind.Object && messages[0].TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()) && id.GetString()!.Length <= 512)
                return new(NotificationAcceptance.Accepted, id.GetString());
            return new(NotificationAcceptance.Unknown, FailureCode: "provider_acceptance_unknown");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // A lost response is not proof of rejection; retry needs provider reconciliation.
            return new(NotificationAcceptance.Unknown, FailureCode: "provider_acceptance_unknown");
        }
    }
}
