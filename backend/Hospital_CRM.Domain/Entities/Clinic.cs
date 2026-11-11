using System.ComponentModel.DataAnnotations;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Enums;

namespace Hospital_CRM.Domain.Entities;

public class Clinic : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? OrganizationType { get; set; }

    [MaxLength(255)]
    public string? LegalName { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(255)]
    public string? Website { get; set; }

    [MaxLength(50)]
    public string Timezone { get; set; } = AccountingTimezones.Kolkata;

    [MaxLength(10)]
    public string Currency { get; set; } = "INR";

    [MaxLength(20)]
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    [MaxLength(20)]
    public string TimeFormat { get; set; } = "HH:mm";

    [MaxLength(10)]
    public string Language { get; set; } = "en";

    // Branding
    [MaxLength(500)]
    public string? LogoUrl { get; set; }

    [MaxLength(500)]
    public string? DarkLogoUrl { get; set; }

    [MaxLength(500)]
    public string? LightLogoUrl { get; set; }

    [MaxLength(500)]
    public string? FaviconUrl { get; set; }

    [MaxLength(20)]
    public string PrimaryColor { get; set; } = "#0d9488";

    [MaxLength(20)]
    public string SecondaryColor { get; set; } = "#1e40af";

    [MaxLength(20)]
    public string AccentColor { get; set; } = "#f59e0b";

    // Appointment settings
    public int DefaultAppointmentDurationMinutes { get; set; } = 30;
    public int BufferMinutes { get; set; } = 0;
    public int MinAdvanceBookingHours { get; set; } = 0;
    public int MaxAdvanceBookingDays { get; set; } = 90;
    public bool SameDayBookingAllowed { get; set; } = true;
    public bool WalkInsAllowed { get; set; } = true;
    public bool OverbookingAllowed { get; set; } = false;
    public int CancellationWindowHours { get; set; } = 24;
    public bool ReschedulingAllowed { get; set; } = true;
    public bool NoShowHandlingEnabled { get; set; } = true;

    // Queue settings
    public bool QueueEnabled { get; set; } = true;
    [MaxLength(20)]
    public string TokenFormat { get; set; } = "D{0:000}";
    public int TokenStartNumber { get; set; } = 1;
    [MaxLength(20)]
    public string TokenResetFrequency { get; set; } = "daily";

    // Billing settings
    [MaxLength(20)]
    public string InvoicePrefix { get; set; } = "INV";
    public decimal DefaultConsultationFee { get; set; } = 0m;
    public decimal DefaultGstRate { get; set; } = 18.0m;
    public bool DefaultInvoiceStatus { get; set; } = false;

    // Section 4: Pharmacy Retail Licensing (Form 20 & 21) & Tax
    [MaxLength(100)]
    public string? PharmacyLicenceNumber20 { get; set; }
    [MaxLength(100)]
    public string? PharmacyLicenceNumber21 { get; set; }
    [MaxLength(50)]
    public string? PharmacyGstNumber { get; set; }

    // MOD-14: Platform-admin-controlled subscription fields
    public SubscriptionTier SubscriptionTier { get; set; } = SubscriptionTier.Trial;
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Active;
    public string ActivatedModules { get; set; } = "core";  // comma-separated: core,lab,inventory,finance,wishlist,emergency,precheck,notifications
    public DateTimeOffset? SubscriptionEndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public virtual ICollection<ClinicHours> WorkingHours { get; set; } = [];
    public virtual ICollection<ClinicHoliday> Holidays { get; set; } = [];
    public virtual ICollection<ClinicSpecialHour> SpecialHours { get; set; } = [];
    public virtual ICollection<StaffInvite> StaffInvites { get; set; } = [];
    public virtual ICollection<User> Users { get; set; } = [];
    public virtual ICollection<Department> Departments { get; set; } = [];
    public virtual Tenant? Tenant { get; set; }
}