namespace Hospital_CRM.Domain.Entities;

/// <summary>
/// B2: one row per accepted PSP webhook. Doubles as the dedupe key
/// (unique tenant + provider + event id) and as the "needs review" queue when no open
/// invoice could be matched — a payment is never silently dropped.
/// </summary>
public class ProcessedWebhookEvent : ITenantScoped
{
    public const string Processed = "processed";
    public const string Unmatched = "unmatched";
    public const string Duplicate = "duplicate";

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string Status { get; set; } = Processed;

    public decimal? Amount { get; set; }
    public string? Utr { get; set; }
    public string? Reference { get; set; }
    public Guid? PaymentId { get; set; }

    /// <summary>Raw payload, kept so an operator can review an unmatched payment.</summary>
    public string? RawPayload { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
