using Hospital_CRM.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Hospital_CRM.Domain.Entities;

public class Appointment : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid ClinicId { get; set; }
    
    public DateOnly Date { get; set; }
    public string TimeSlot { get; set; } = string.Empty; // e.g. "10:30"
    public AppointmentType Type { get; set; }
    public AppointmentStatus Status { get; set; }
    public AppointmentPriority Priority { get; set; } = AppointmentPriority.Normal;
    public int? QueueToken { get; set; }
    
    [MaxLength(128)]
    public string? QueueTrackingToken { get; set; }

    public DateTimeOffset? QueueTrackingExpiresAt { get; set; }

    public void EndQueueTracking(DateTimeOffset endedAt)
    {
        // Terminal transitions can shorten a link, never renew it (including legacy links).
        if (QueueTrackingExpiresAt > endedAt.AddHours(2))
            QueueTrackingExpiresAt = endedAt.AddHours(2);
    }

    [MaxLength(50)]
    public string? TriageReasonCode { get; set; }

    public Guid? TriageConfirmedBy { get; set; }
    public DateTimeOffset? TriageConfirmedAt { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    [ConcurrencyCheck]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    // Constraints via unique index: (doctor_id, date, time_slot) where status != cancelled
    // Navigation
    public virtual Patient Patient { get; set; } = null!;
    public virtual User Doctor { get; set; } = null!;
    public virtual Clinic Clinic { get; set; } = null!;
    public virtual ICollection<AppointmentHistory> History { get; set; } = [];
    public virtual ICollection<Consultation> Consultations { get; set; } = [];
    public virtual ICollection<PriorityLog> PriorityLogs { get; set; } = [];
    public virtual PrecheckSubmission? PrecheckSubmission { get; set; }
}
