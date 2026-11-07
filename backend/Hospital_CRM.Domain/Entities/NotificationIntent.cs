namespace Hospital_CRM.Domain.Entities;

// Booking outbox; delivery history remains append-only in NotificationLog.
public sealed class NotificationIntent : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AppointmentId { get; set; }
    public DateOnly AppointmentDate { get; set; }
    public string? ProtectedPrecheckLink { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? HandedOffAt { get; set; }
    public Appointment Appointment { get; set; } = null!;
}
