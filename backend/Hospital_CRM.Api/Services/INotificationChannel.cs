using Hospital_CRM.Domain.Enums;

namespace Hospital_CRM.Api.Services;

public enum NotificationAcceptance { Accepted, Rejected, Retryable, Unknown }

public sealed record NotificationChannelResult(NotificationAcceptance Acceptance, string? ProviderMessageId = null, string? FailureCode = null);

public sealed record NotificationChannelMessage(Guid TenantId, NotificationChannel Channel, string Phone,
    string TemplateName, IReadOnlyList<string> Parameters);

public interface INotificationChannel
{
    Task<NotificationChannelResult> SendAsync(NotificationChannelMessage message, CancellationToken ct);
}
