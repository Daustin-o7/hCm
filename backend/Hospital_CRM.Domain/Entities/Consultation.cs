using System.ComponentModel.DataAnnotations;
using Hospital_CRM.Domain.Enums;

namespace Hospital_CRM.Domain.Entities;

public class Consultation : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid DoctorId { get; set; }
    
    public string? ChiefComplaint { get; set; }
    public string? Observations { get; set; }
    public string? Diagnosis { get; set; }

    public ConsultationStatus Status { get; set; } = ConsultationStatus.Draft;
    
    public int Version { get; set; } = 1;
    public Guid? PreviousVersionId { get; set; }

    // Amendment metadata
    public string? AmendmentReason { get; set; }

    // Legal & Signature Elements
    public DateTimeOffset? SignedAt { get; set; }
    public Guid? SignedByDoctorId { get; set; }
    
    [MaxLength(100)]
    public string? DoctorRegistrationNumber { get; set; }

    [MaxLength(200)]
    public string? DoctorQualifications { get; set; }

    [MaxLength(256)]
    public string? DigitalSignatureHash { get; set; }

    [MaxLength(256)]
    public string? VerificationQrCode { get; set; }

    // Core investigations list (independent of lab module)
    public string? AdvisedInvestigations { get; set; }

    // Pending selections only; finalization creates LabOrders and clears this field.
    public string[]? DraftLabTests { get; set; }

    // Diagnosis coding (ICD-10)
    [MaxLength(20)]
    public string? IcdCode { get; set; }

    [MaxLength(255)]
    public string? IcdTitle { get; set; }

    // Follow-up appointment & retention
    public DateOnly? FollowUpDate { get; set; }
    public string? FollowUpNotes { get; set; }

    // Template schema & structured notes
    public Guid? TemplateId { get; set; }
    public int? TemplateVersion { get; set; }
    public string? StructuredObservationsJson { get; set; }
    public string? OdontogramJson { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    
    // Navigation — append-only versioned (FR-14)
    public virtual Appointment Appointment { get; set; } = null!;
    public virtual User Doctor { get; set; } = null!;
    public virtual Consultation? PreviousVersion { get; set; }
    public virtual ICollection<Prescription> Prescriptions { get; set; } = [];
}
