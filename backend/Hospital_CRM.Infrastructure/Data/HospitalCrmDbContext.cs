using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;

namespace Hospital_CRM.Infrastructure.Data;

public class HospitalCrmDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;

    public HospitalCrmDbContext(
        DbContextOptions<HospitalCrmDbContext> options,
        ITenantContext? tenantContext = null) : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<StaffInvite> StaffInvites => Set<StaffInvite>();
    public DbSet<TenantProvisioningReceipt> TenantProvisioningReceipts => Set<TenantProvisioningReceipt>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<AuthAuditLog> AuthAuditLogs => Set<AuthAuditLog>();
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<ClinicHours> ClinicHours => Set<ClinicHours>();
    public DbSet<ClinicHoliday> ClinicHolidays => Set<ClinicHoliday>();
    public DbSet<ClinicSpecialHour> ClinicSpecialHours => Set<ClinicSpecialHour>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<DoctorDailyQueueCounter> DoctorDailyQueueCounters => Set<DoctorDailyQueueCounter>();
    public DbSet<PatientConsent> PatientConsents => Set<PatientConsent>();
    public DbSet<PatientAuditLog> PatientAuditLogs => Set<PatientAuditLog>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentHistory> AppointmentHistories => Set<AppointmentHistory>();
    public DbSet<Consultation> Consultations => Set<Consultation>();
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<PrescriptionItem> PrescriptionItems => Set<PrescriptionItem>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLineItem> InvoiceLineItems => Set<InvoiceLineItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<NotificationIntent> NotificationIntents => Set<NotificationIntent>();
    public DbSet<PrecheckSubmission> PrecheckSubmissions => Set<PrecheckSubmission>();
    public DbSet<PriorityLog> PriorityLogs => Set<PriorityLog>();
    public DbSet<ConsultTemplate> ConsultTemplates => Set<ConsultTemplate>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<LabOrder> LabOrders => Set<LabOrder>();
    public DbSet<LabResult> LabResults => Set<LabResult>();
    public DbSet<LedgerExpense> LedgerExpenses => Set<LedgerExpense>();
    public DbSet<ImpersonationLog> ImpersonationLogs => Set<ImpersonationLog>();
    public DbSet<ImpersonationPhiAccessLog> ImpersonationPhiAccessLogs => Set<ImpersonationPhiAccessLog>();
    public DbSet<TenantFeatureFlag> TenantFeatureFlags => Set<TenantFeatureFlag>();
    public DbSet<TenantIntegration> TenantIntegrations => Set<TenantIntegration>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<PaymentBridgeDevice> PaymentBridgeDevices => Set<PaymentBridgeDevice>();
    public DbSet<NurseProfile> NurseProfiles => Set<NurseProfile>();
    public DbSet<NurseAvailability> NurseAvailabilities => Set<NurseAvailability>();
    public DbSet<NurseShift> NurseShifts => Set<NurseShift>();
    public DbSet<NurseShiftApplication> NurseShiftApplications => Set<NurseShiftApplication>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<PatientNurseAssignment> PatientNurseAssignments => Set<PatientNurseAssignment>();
    public DbSet<ShiftHandover> ShiftHandovers => Set<ShiftHandover>();
    public DbSet<NurseAuditLog> NurseAuditLogs => Set<NurseAuditLog>();
    public DbSet<Drug> Drugs => Set<Drug>();
    public DbSet<DrugBatch> DrugBatches => Set<DrugBatch>();
    public DbSet<DispenseRecord> DispenseRecords => Set<DispenseRecord>();
    public DbSet<DispenseItem> DispenseItems => Set<DispenseItem>();
    public DbSet<ControlledSubstanceRegister> ControlledSubstanceRegisters => Set<ControlledSubstanceRegister>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<PatientVital> PatientVitals => Set<PatientVital>();
    public DbSet<ShiftTemplate> ShiftTemplates => Set<ShiftTemplate>();
    public DbSet<StaffRoster> StaffRosters => Set<StaffRoster>();
    public DbSet<ShiftChangeRequest> ShiftChangeRequests => Set<ShiftChangeRequest>();
    public DbSet<AuthorityProxy> AuthorityProxies => Set<AuthorityProxy>();
    public DbSet<StaffTeam> StaffTeams => Set<StaffTeam>();
    public DbSet<StaffTeamMember> StaffTeamMembers => Set<StaffTeamMember>();
    public DbSet<StaffAnnouncement> StaffAnnouncements => Set<StaffAnnouncement>();
    public DbSet<DrugStockMovement> DrugStockMovements => Set<DrugStockMovement>();
    public DbSet<MedicineSearchMissLog> MedicineSearchMissLogs => Set<MedicineSearchMissLog>();
    public DbSet<PrescriptionPrintLog> PrescriptionPrintLogs => Set<PrescriptionPrintLog>();
    public DbSet<TenantTaxProfile> TenantTaxProfiles => Set<TenantTaxProfile>();
    public DbSet<PayeeAccount> PayeeAccounts => Set<PayeeAccount>();
    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();
    public DbSet<FinanceLedgerEntry> FinanceLedgerEntries => Set<FinanceLedgerEntry>();
    public DbSet<InvoicePrintLog> InvoicePrintLogs => Set<InvoicePrintLog>();
    public DbSet<PatientDeposit> PatientDeposits => Set<PatientDeposit>();
    public DbSet<PatientDepositAllocation> PatientDepositAllocations => Set<PatientDepositAllocation>();


    public Guid CurrentTenantId => _tenantContext?.CurrentTenantId ?? Guid.Empty;
    public bool BypassTenantFilter => _tenantContext?.IsPlatformAdmin == true;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();
        ApplyConfigurations(modelBuilder);
        modelBuilder.ApplyConfiguration(new NotificationIntentConfiguration());
        ApplyTenantFilters(modelBuilder);
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType) &&
                entityType.BaseType == null &&
                !entityType.IsOwned())
            {
                var method = typeof(HospitalCrmDbContext)
                    .GetMethod(nameof(ConfigureTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)?
                    .MakeGenericMethod(entityType.ClrType);

                method?.Invoke(this, [modelBuilder]);
            }
        }
    }

    private void ConfigureTenantFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantScoped
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e =>
            BypassTenantFilter || CurrentTenantId != Guid.Empty && e.TenantId == CurrentTenantId);
    }

    private static void ApplyConfigurations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.PasswordHash).HasMaxLength(255);
            e.Property(x => x.TenantId).IsRequired(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.Clinic).WithMany(c => c.Users).HasForeignKey(x => x.ClinicId);
            e.HasOne(x => x.NurseProfile).WithOne(n => n.User).HasForeignKey<NurseProfile>(x => x.UserId);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.TokenHash }).IsUnique();
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => x.LastUsedAt);
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).HasMaxLength(128);
            e.HasOne(x => x.User).WithMany(u => u.RefreshTokens).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<AuthAuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EventType, x.CreatedAt });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.EventType).HasMaxLength(50);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<TenantMembership>(e =>
        {
            e.HasKey(x => x.Id);
            // A doctor may hold several branches of one tenant; a null ClinicId means tenant-wide.
            e.HasIndex(x => new { x.UserId, x.TenantId, x.ClinicId }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Status).HasMaxLength(50);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Clinic).WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PasswordResetToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(512);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.PasswordResetTokens).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<TenantProvisioningReceipt>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.HasIndex(x => new { x.ActorId, x.IdempotencyKey }).IsUnique();
            // A cancelled invite or removed tenant must not erase the replay boundary.
        });

        modelBuilder.Entity<StaffInvite>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.Email, x.AcceptedAt });
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.TokenHash).HasMaxLength(512);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.HasOne(x => x.Clinic).WithMany(c => c.StaffInvites).HasForeignKey(x => x.ClinicId);
            e.HasOne(x => x.NurseProfile).WithOne().HasForeignKey<StaffInvite>(x => x.NurseProfileId);
        });

        modelBuilder.Entity<NurseProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.WardId });
            e.Property(x => x.LicenseNumber).HasMaxLength(50);
            e.Property(x => x.LicenseAuthority).HasMaxLength(100);
            e.Property(x => x.NursingQualification).HasMaxLength(255);
            e.Property(x => x.Institution).HasMaxLength(255);
            e.Property(x => x.Specialization).HasMaxLength(100);
            e.Property(x => x.Skills).HasMaxLength(2000);
            e.Property(x => x.Languages).HasMaxLength(500);
            e.Property(x => x.EmergencyContactName).HasMaxLength(255);
            e.Property(x => x.EmergencyContactPhone).HasMaxLength(50);
            e.Property(x => x.EmergencyContactRelation).HasMaxLength(50);
            e.Property(x => x.DocumentsJson).HasMaxLength(5000);
            e.Property(x => x.ShiftPreferencesJson).HasMaxLength(2000);
            e.Property(x => x.WorkRestrictionsJson).HasMaxLength(2000);
            e.Property(x => x.DepartmentId).IsRequired(false);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.User).WithOne(u => u.NurseProfile).HasForeignKey<NurseProfile>(x => x.UserId);
            e.HasOne(x => x.Department).WithMany(d => d.Nurses).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Department>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.Code }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Description).HasMaxLength(255);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.Clinic).WithMany(c => c.Departments).HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NurseAvailability>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.NurseProfileId, x.StartDate, x.EndDate });
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.NurseProfile).WithMany(n => n.Availabilities).HasForeignKey(x => x.NurseProfileId);
        });

        modelBuilder.Entity<NurseShift>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ClinicId, x.Date });
            e.HasIndex(x => new { x.TenantId, x.NurseProfileId, x.Date, x.ShiftType })
                .IsUnique()
                .HasFilter("\"Status\" = 1 AND \"NurseProfileId\" IS NOT NULL"); // one assigned slot per nurse/date/type
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Nurse).WithMany().HasForeignKey(x => x.NurseProfileId);
        });

        modelBuilder.Entity<NurseShiftApplication>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ShiftId, x.NurseProfileId }).IsUnique();
            e.HasIndex(x => new { x.NurseProfileId, x.Status });
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.AppliedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Shift).WithMany(s => s.Applications).HasForeignKey(x => x.ShiftId);
            e.HasOne(x => x.Nurse).WithMany().HasForeignKey(x => x.NurseProfileId);
        });

        modelBuilder.Entity<LeaveRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.NurseProfileId, x.StartDate, x.EndDate });
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.ReviewNote).HasMaxLength(1000);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Nurse).WithMany().HasForeignKey(x => x.NurseProfileId);
        });

        modelBuilder.Entity<PatientNurseAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.NurseProfileId, x.Status });
            e.HasIndex(x => new { x.PatientId, x.Status });
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.AssignedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Nurse).WithMany().HasForeignKey(x => x.NurseProfileId);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId);
        });

        modelBuilder.Entity<ShiftHandover>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.CreatedAt });
            e.Property(x => x.Note).HasMaxLength(4000);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne<NurseProfile>().WithMany().HasForeignKey(x => x.AuthorNurseProfileId);
            e.HasOne<NurseProfile>().WithMany().HasForeignKey(x => x.ToNurseProfileId);
        });

        modelBuilder.Entity<NurseAuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.NurseProfileId);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(50);
            e.Property(x => x.OldValue).HasMaxLength(4000);
            e.Property(x => x.NewValue).HasMaxLength(4000);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.ChangedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.ActivatedModules).HasMaxLength(1000).HasDefaultValue("core");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
        });

        modelBuilder.Entity<Clinic>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasOne(x => x.Tenant).WithOne(t => t.Clinic).HasForeignKey<Clinic>(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.OrganizationType).HasMaxLength(100);
            e.Property(x => x.LegalName).HasMaxLength(255);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.Website).HasMaxLength(255);
            e.Property(x => x.Timezone).HasMaxLength(50).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(10).IsRequired();
            e.Property(x => x.DateFormat).HasMaxLength(20).IsRequired();
            e.Property(x => x.TimeFormat).HasMaxLength(20).IsRequired();
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.LogoUrl).HasMaxLength(500);
            e.Property(x => x.DarkLogoUrl).HasMaxLength(500);
            e.Property(x => x.LightLogoUrl).HasMaxLength(500);
            e.Property(x => x.FaviconUrl).HasMaxLength(500);
            e.Property(x => x.PrimaryColor).HasMaxLength(20);
            e.Property(x => x.SecondaryColor).HasMaxLength(20);
            e.Property(x => x.AccentColor).HasMaxLength(20);
            e.Property(x => x.InvoicePrefix).HasMaxLength(20);
            e.Property(x => x.TokenFormat).HasMaxLength(20);
            e.Property(x => x.TokenResetFrequency).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ClinicHours>(e =>
        {
            e.HasKey(x => x.Id);
            // Allow multiple shifts per day (e.g., 09:00–13:00 and 14:00–18:00)
            e.HasIndex(x => new { x.ClinicId, x.DayOfWeek, x.ShiftIndex }).IsUnique();
            e.Property(x => x.OpenTime).HasMaxLength(10);
            e.Property(x => x.CloseTime).HasMaxLength(10);
            e.HasOne(x => x.Clinic).WithMany(c => c.WorkingHours).HasForeignKey(x => x.ClinicId);
        });

        modelBuilder.Entity<ClinicHoliday>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.StartDate).HasMaxLength(255);
            e.Property(x => x.EndDate).HasMaxLength(255);
            e.Property(x => x.InternalNote).HasMaxLength(500);
            e.HasOne(x => x.Clinic).WithMany(c => c.Holidays).HasForeignKey(x => x.ClinicId);
        });

        modelBuilder.Entity<ClinicSpecialHour>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ClinicId, x.Date }).IsUnique();
            e.Property(x => x.Date).HasMaxLength(255);
            e.Property(x => x.OpenTime).HasMaxLength(10);
            e.Property(x => x.CloseTime).HasMaxLength(10);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne(x => x.Clinic).WithMany(c => c.SpecialHours).HasForeignKey(x => x.ClinicId);
        });

        modelBuilder.Entity<Patient>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Phone); // Non-unique: Indian family members often share one phone number
            e.HasIndex(x => new { x.TenantId, x.Phone }); // tenant-scoped lookup used by registration
            e.HasIndex(x => new { x.TenantId, x.Uhid }).IsUnique().HasFilter("\"Uhid\" IS NOT NULL");
            e.HasIndex(x => x.Name); // trigram GIN index added via migration separately
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Uhid).HasMaxLength(50);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.CreatedByUser).WithMany(u => u.CreatedPatients).HasForeignKey(x => x.CreatedBy);
        });

        modelBuilder.Entity<DoctorDailyQueueCounter>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DoctorId, x.Date }).IsUnique();
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Doctor).WithMany().HasForeignKey(x => x.DoctorId);
        });

        modelBuilder.Entity<PatientConsent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.Purpose).HasMaxLength(255);
            e.Property(x => x.NoticeVersion).HasMaxLength(50).HasDefaultValue("v1.0");
            e.Property(x => x.Language).HasMaxLength(10).HasDefaultValue("en");
            e.Property(x => x.CaptureMethod).HasMaxLength(50).HasDefaultValue("receptionist_assisted");
            e.Property(x => x.CapturedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Patient).WithMany(p => p.Consents).HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.CapturedByUser).WithMany(u => u.CapturedConsents).HasForeignKey(x => x.CapturedBy);
        });

        modelBuilder.Entity<PatientAuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.ChangedAt });
            e.Property(x => x.FieldName).HasMaxLength(100);
            e.Property(x => x.OldValue).HasMaxLength(255);
            e.Property(x => x.NewValue).HasMaxLength(255);
            e.Property(x => x.ChangedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.ChangedByUser).WithMany(u => u.PatientAuditLogs).HasForeignKey(x => x.ChangedBy);
        });

        modelBuilder.Entity<Appointment>(e =>
        {
            e.HasKey(x => x.Id);
            // Strict double-booking prevention per doctor, date, slot across active bookings
            e.HasIndex(x => new { x.TenantId, x.DoctorId, x.Date, x.TimeSlot }).IsUnique().HasFilter("\"Status\" <> 6");
            // Idempotent queue token per doctor, date
            e.HasIndex(x => new { x.TenantId, x.DoctorId, x.Date, x.QueueToken }).IsUnique().HasFilter("\"QueueToken\" IS NOT NULL");
            // Public tracker lookup by token hash / random token
            e.HasIndex(x => x.QueueTrackingToken).IsUnique().HasFilter("\"QueueTrackingToken\" IS NOT NULL");
            e.HasIndex(x => new { x.PatientId, x.Date });
            e.HasIndex(x => new { x.TenantId, x.DoctorId, x.Date, x.Status }); // doctor day-view by status
            e.HasIndex(x => new { x.ClinicId, x.Date }); // clinic day list (reception/nurse/admin dashboards)
            e.Property(x => x.TimeSlot).HasMaxLength(10);
            e.Property(x => x.QueueTrackingToken).HasMaxLength(128);
            e.Property(x => x.TriageReasonCode).HasMaxLength(50);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.Patient).WithMany(p => p.Appointments).HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.Doctor).WithMany(u => u.AppointmentsAsDoctor).HasForeignKey(x => x.DoctorId);
            e.HasOne(x => x.Clinic).WithMany().HasForeignKey(x => x.ClinicId);
        });

        modelBuilder.Entity<AppointmentHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AppointmentId);
            e.Property(x => x.PreviousTimeSlot).HasMaxLength(10);
            e.Property(x => x.ChangedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Appointment).WithMany(a => a.History).HasForeignKey(x => x.AppointmentId);
        });

        modelBuilder.Entity<Consultation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AppointmentId);
            // One row per version: the last line of defence against a concurrent double-finalize.
            e.HasIndex(x => new { x.AppointmentId, x.Version }).IsUnique();
            e.HasIndex(x => new { x.DoctorId, x.CreatedAt });
            e.HasIndex(x => x.Status);
            // Patient history path is Consultation -> Appointment.PatientId; Consultation carries no PatientId.
            e.HasIndex(x => new { x.TenantId, x.AppointmentId, x.CreatedAt });
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Appointment).WithMany(a => a.Consultations).HasForeignKey(x => x.AppointmentId);
            e.HasOne(x => x.Doctor).WithMany(u => u.Consultations).HasForeignKey(x => x.DoctorId);
            e.HasOne(x => x.PreviousVersion).WithMany().HasForeignKey(x => x.PreviousVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Prescription>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.ConsultationId);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Consultation).WithMany(c => c.Prescriptions).HasForeignKey(x => x.ConsultationId);
        });

        modelBuilder.Entity<PrescriptionItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PrescriptionId);
            e.HasIndex(x => x.DrugId);
            e.Property(x => x.MedicineText).HasMaxLength(500);
            e.Property(x => x.DosageText).HasMaxLength(255);
            e.Property(x => x.FrequencyText).HasMaxLength(255);
            e.Property(x => x.DurationText).HasMaxLength(255);
            e.HasOne(x => x.Prescription).WithMany(p => p.Items).HasForeignKey(x => x.PrescriptionId);
            e.HasOne(x => x.Drug).WithMany().HasForeignKey(x => x.DrugId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt }); // newest-first invoice list, paged
            e.HasIndex(x => x.AppointmentId);
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.FinancialYear, x.Series, x.InvoiceNumber })
                .IsUnique()
                .HasFilter("\"InvoiceNumber\" > 0");
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.Appointment).WithMany().HasForeignKey(x => x.AppointmentId);
            e.Property(x => x.CheckoutRequestHash).HasMaxLength(64);
        });

        modelBuilder.Entity<InvoiceLineItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InvoiceId);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.Property(x => x.GstRate).HasColumnType("decimal(5,2)");
            e.Property(x => x.HsnCode).HasMaxLength(20);
            e.HasOne(x => x.Invoice).WithMany(i => i.LineItems).HasForeignKey(x => x.InvoiceId);
            e.HasOne(x => x.DrugBatch).WithMany().HasForeignKey(x => x.DrugBatchId);
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InvoiceId);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.PaidAt }); // ledger date-range scans key off PaidAt, not CreatedAt
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.Method, x.RazorpayPaymentId }).IsUnique()
                .HasFilter("\"RazorpayPaymentId\" IS NOT NULL AND \"Amount\" > 0 AND \"Method\" = 5");
            e.Property(x => x.RazorpayPaymentId).HasMaxLength(255);
            e.Property(x => x.RazorpayOrderId).HasMaxLength(255);
            e.Property(x => x.Utr).HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Invoice).WithMany(inv => inv.Payments).HasForeignKey(x => x.InvoiceId);
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.RefundSourcePaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.RefundCreditNoteId);
            e.Property(x => x.RefundNonConsiderationReason).HasMaxLength(500);
            e.HasOne(x => x.RefundCreditNote).WithMany().HasForeignKey(x => x.RefundCreditNoteId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Payments_RefundResolution", "NOT (\"RefundCreditNoteId\" IS NOT NULL AND \"RefundNonConsiderationReason\" IS NOT NULL)"));
        });

        modelBuilder.Entity<TenantIntegration>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Provider }).IsUnique();
            e.HasIndex(x => x.WebhookPathToken).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.MerchantId).HasMaxLength(255);
            e.Property(x => x.SecretProtected).HasMaxLength(2000);
            e.Property(x => x.WebhookPathToken).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<PaymentBridgeDevice>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.RevokedAt });
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.SecretProtected).HasMaxLength(2000);
            e.Property(x => x.PairingCodeHash).HasMaxLength(64);
        });

        modelBuilder.Entity<ProcessedWebhookEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Provider, x.EventId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.EventId).HasMaxLength(255).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Utr).HasMaxLength(64);
            e.Property(x => x.Reference).HasMaxLength(255);
        });

        modelBuilder.Entity<NotificationLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AppointmentId, x.Status });
            e.Property(x => x.Template).HasMaxLength(100);
            e.Property(x => x.FailedReason).HasMaxLength(500);
            e.HasOne(x => x.Appointment).WithMany().HasForeignKey(x => x.AppointmentId);
        });

        modelBuilder.Entity<PrecheckSubmission>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.AppointmentId);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.ChiefComplaint).HasMaxLength(2000);
            e.Property(x => x.SymptomDuration).HasMaxLength(500);
            e.Property(x => x.Medications).HasMaxLength(2000);
            e.Property(x => x.Allergies).HasMaxLength(1000);
            e.HasOne(x => x.Appointment).WithOne(a => a.PrecheckSubmission).HasForeignKey<PrecheckSubmission>(x => x.AppointmentId);
        });

        modelBuilder.Entity<PriorityLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AppointmentId);
            e.HasOne(x => x.Appointment).WithMany(a => a.PriorityLogs).HasForeignKey(x => x.AppointmentId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.ChangedBy);
            e.Property(x => x.ChangedTo).HasMaxLength(20).IsRequired();
        });

        modelBuilder.Entity<ConsultTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Specialty, x.DoctorId });
            e.Property(x => x.Specialty).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.StructureJson).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<MessageTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Content).HasMaxLength(2000).IsRequired();
        });

        modelBuilder.Entity<NotificationRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Active });
            e.Property(x => x.TimingConfigJson).HasColumnType("jsonb").IsRequired();
            e.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId);
        });

        modelBuilder.Entity<NotificationLog>(e =>
        {
            e.Property(x => x.RuleId).IsRequired(false);
        });

        modelBuilder.Entity<InventoryItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Active });
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(30).IsRequired();
        });

        modelBuilder.Entity<StockMovement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ItemId, x.RecordedAt });
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne(x => x.Item).WithMany(i => i.Movements).HasForeignKey(x => x.ItemId);
            e.HasOne(x => x.Recorder).WithMany().HasForeignKey(x => x.RecordedBy);
        });

        modelBuilder.Entity<LabOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            e.HasIndex(x => x.ConsultationId);
            e.Property(x => x.TestName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.Consultation).WithMany().HasForeignKey(x => x.ConsultationId);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.Doctor).WithMany().HasForeignKey(x => x.DoctorId);
        });

        modelBuilder.Entity<LabResult>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.TenantId).HasDefaultValue(Guid.Empty);
            e.HasIndex(x => new { x.LabOrderId, x.Version });
            e.Property(x => x.ResultText).HasMaxLength(5000);
            e.Property(x => x.FileUrl).HasMaxLength(500);
            e.HasOne(x => x.LabOrder).WithMany(o => o.Results).HasForeignKey(x => x.LabOrderId);
            e.HasOne(x => x.PreviousVersion).WithMany().HasForeignKey(x => x.PreviousVersionId);
            e.HasOne(x => x.EnteredByUser).WithMany().HasForeignKey(x => x.EnteredBy);
        });

        modelBuilder.Entity<LedgerExpense>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ExpenseDate });
            e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CategoryOther).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Recorder).WithMany().HasForeignKey(x => x.RecordedBy);
        });

        modelBuilder.Entity<ImpersonationLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.StartedAt });
            e.Property(x => x.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<ImpersonationPhiAccessLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ImpersonationId);
            e.Property(x => x.Path).HasMaxLength(512).IsRequired();
            e.Property(x => x.Method).HasMaxLength(10).IsRequired();
        });

        modelBuilder.Entity<TenantFeatureFlag>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.FlagName }).IsUnique();
            e.Property(x => x.FlagName).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Drug>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Name });
            e.HasIndex(x => x.GenericName);
            e.HasIndex(x => x.ScheduleClass);
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.GenericName).HasMaxLength(255).IsRequired();
            e.Property(x => x.TherapeuticCategory).HasMaxLength(100);
            e.Property(x => x.DosageForm).HasMaxLength(50);
            e.Property(x => x.Strength).HasMaxLength(50);
            e.Property(x => x.HsnCode).HasMaxLength(20);
            e.Property(x => x.GstRate).HasColumnType("decimal(5,2)");
            e.Property(x => x.DpcoCeilingPrice).HasColumnType("decimal(18,2)");
            e.Property(x => x.IndicativeMrp).HasColumnType("decimal(18,2)");
            e.Property(x => x.StandardPackSize).HasMaxLength(50);
            e.Property(x => x.CommonBrands).HasMaxLength(500);
        });

        modelBuilder.Entity<DrugBatch>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DrugId, x.ExpiryDate });
            e.HasIndex(x => new { x.DrugId, x.ExpiryDate, x.QuantityRemaining }); // FEFO dispense lookup
            e.HasIndex(x => x.BatchNumber);
            e.Property(x => x.BatchNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.Mrp).HasColumnType("decimal(18,2)");
            e.Property(x => x.PurchaseRate).HasColumnType("decimal(18,2)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()").IsConcurrencyToken();
            e.HasOne(x => x.Drug).WithMany(d => d.Batches).HasForeignKey(x => x.DrugId);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
        });

        modelBuilder.Entity<DispenseRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DispensedAt });
            e.HasIndex(x => x.PrescriptionId);
            e.HasIndex(x => new { x.TenantId, x.InvoiceId }).IsUnique().HasFilter("\"InvoiceId\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.Property(x => x.WalkInCustomerName).HasMaxLength(255);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.HasOne(x => x.Prescription).WithMany().HasForeignKey(x => x.PrescriptionId);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.Dispenser).WithMany().HasForeignKey(x => x.DispensedBy);
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DispenseItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DispenseRecordId);
            e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
            e.HasOne(x => x.DispenseRecord).WithMany(r => r.Items).HasForeignKey(x => x.DispenseRecordId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PrescriptionItem).WithMany().HasForeignKey(x => x.PrescriptionItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DrugBatch).WithMany().HasForeignKey(x => x.DrugBatchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ControlledSubstanceRegister>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ScheduleClass, x.DispensedAt });
            e.Property(x => x.DrugName).HasMaxLength(255).IsRequired();
            e.Property(x => x.BatchNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.PatientName).HasMaxLength(255).IsRequired();
            e.Property(x => x.PatientAddress).HasMaxLength(500);
            e.Property(x => x.PrescriberName).HasMaxLength(255).IsRequired();
            e.Property(x => x.PrescriberRegNo).HasMaxLength(100).IsRequired();
            e.Property(x => x.DispenserName).HasMaxLength(255);
            e.HasOne(x => x.Drug).WithMany().HasForeignKey(x => x.DrugId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DispenseRecord).WithMany().HasForeignKey(x => x.DispenseRecordId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Supplier>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Name });
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.Gstin).HasMaxLength(50);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.Address).HasMaxLength(500);
        });

        modelBuilder.Entity<PurchaseOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.OrderNumber).HasMaxLength(50).IsRequired();
            e.Property(x => x.DistributorName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ItemsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
            e.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatedBy);
        });

        modelBuilder.Entity<WishlistItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.Text).HasMaxLength(1000).IsRequired();
            e.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatedBy);
        });

        modelBuilder.Entity<PatientVital>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.AppointmentId });
            e.HasIndex(x => x.PatientId);
            e.HasOne(x => x.Appointment).WithMany().HasForeignKey(x => x.AppointmentId);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.RecordedByUser).WithMany().HasForeignKey(x => x.RecordedByUserId);
        });

        modelBuilder.Entity<ShiftTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.IsActive });
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.StartTime).HasMaxLength(10).IsRequired();
            e.Property(x => x.EndTime).HasMaxLength(10).IsRequired();
        });

        modelBuilder.Entity<StaffRoster>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.StartDate, x.EndDate });
            e.Property(x => x.Title).HasMaxLength(150).IsRequired();
            e.HasMany(x => x.Shifts).WithOne(s => s.Roster).HasForeignKey(s => s.RosterId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ShiftChangeRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.Status });
            e.HasIndex(x => x.RequesterNurseProfileId);
            e.HasIndex(x => x.TargetNurseProfileId);
            e.HasOne(x => x.RequesterNurse).WithMany().HasForeignKey(x => x.RequesterNurseProfileId);
            e.HasOne(x => x.RequesterShift).WithMany().HasForeignKey(x => x.RequesterShiftId);
            e.HasOne(x => x.TargetNurse).WithMany().HasForeignKey(x => x.TargetNurseProfileId);
            e.HasOne(x => x.TargetShift).WithMany().HasForeignKey(x => x.TargetShiftId);
        });

        modelBuilder.Entity<AuthorityProxy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.ValidUntil, x.IsRevoked });
            e.HasOne(x => x.DelegatorUser).WithMany().HasForeignKey(x => x.DelegatorUserId);
            e.HasOne(x => x.DesigneeUser).WithMany().HasForeignKey(x => x.DesigneeUserId);
        });

        modelBuilder.Entity<StaffTeam>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.Name });
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasMany(x => x.Members).WithOne(m => m.Team).HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Announcements).WithOne(a => a.Team).HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<StaffTeamMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<StaffAnnouncement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.CreatedAt });
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Content).HasMaxLength(4000).IsRequired();
            e.HasOne(x => x.AuthorUser).WithMany().HasForeignKey(x => x.AuthorUserId);
        });

        modelBuilder.Entity<DrugBatch>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_DrugBatch_QuantityRemaining_NonNegative", "\"QuantityRemaining\" >= 0"));
        });

        modelBuilder.Entity<DrugStockMovement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.DrugBatchId });
            e.HasIndex(x => new { x.TenantId, x.RecordedAt });
            e.HasOne(x => x.Drug).WithMany().HasForeignKey(x => x.DrugId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DrugBatch).WithMany().HasForeignKey(x => x.DrugBatchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Recorder).WithMany().HasForeignKey(x => x.RecordedBy).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MedicineSearchMissLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.SearchedAt });
            e.Property(x => x.Query).HasMaxLength(255).IsRequired();
            e.HasOne(x => x.SearchedByUser).WithMany().HasForeignKey(x => x.SearchedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PrescriptionPrintLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PrescriptionId });
            e.HasOne(x => x.Consultation).WithMany().HasForeignKey(x => x.ConsultationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Prescription).WithMany().HasForeignKey(x => x.PrescriptionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PrintedByUser).WithMany().HasForeignKey(x => x.PrintedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantTaxProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId });
        });

        modelBuilder.Entity<PayeeAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.ClinicId, x.PriorityOrder });
        });

        modelBuilder.Entity<CreditNote>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CreditNoteNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.FinancialYear, x.Series, x.SequenceNumber })
                .IsUnique()
                .HasFilter("\"SequenceNumber\" > 0");
            e.HasOne(x => x.Invoice).WithMany(i => i.CreditNotes).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinanceLedgerEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.ReferenceType, x.ReferenceId });
        });

        modelBuilder.Entity<InvoicePrintLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.InvoiceId });
            e.HasOne(x => x.Invoice).WithMany(i => i.PrintLogs).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PrintedByUser).WithMany().HasForeignKey(x => x.PrintedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PatientDeposit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.Property(x => x.UpdatedAt).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.ReceiptNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.FinancialYear, x.SequenceNumber })
                .IsUnique()
                .HasFilter("\"SequenceNumber\" > 0");
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.Status });
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Clinic).WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PatientDepositAllocation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PatientDepositId });
            e.HasIndex(x => new { x.TenantId, x.InvoiceId });
            e.HasOne(x => x.PatientDeposit).WithMany(d => d.Allocations).HasForeignKey(x => x.PatientDepositId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });
    }


    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceTenantScopedInserts();
        PreventAppendOnlyModifications();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        EnforceTenantScopedInserts();
        PreventAppendOnlyModifications();
        return base.SaveChanges();
    }

    private void EnforceTenantScopedInserts()
    {
        var tenantId = _tenantContext?.CurrentTenantId;
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty) return;

        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId == Guid.Empty)
                {
                    entry.Entity.TenantId = tenantId.Value;
                }
            }
        }
    }

    private void PreventAppendOnlyModifications()
    {
        var forbiddenEntries = ChangeTracker.Entries()
            .Where(e => (e.State == EntityState.Modified || e.State == EntityState.Deleted) &&
                        (e.Entity is PatientAuditLog ||
                         e.Entity is NurseAuditLog ||
                         e.Entity is AppointmentHistory ||
                         e.Entity is NotificationLog ||
                         e.Entity is PriorityLog ||
                         (e.Entity is ImpersonationLog && !IsPermittedImpersonationLogEnd(e)) ||
                         e.Entity is ControlledSubstanceRegister ||
                         e.Entity is DrugStockMovement ||
                         e.Entity is StockMovement ||
                         e.Entity is PrescriptionPrintLog ||
                         e.Entity is MedicineSearchMissLog ||
                         e.Entity is FinanceLedgerEntry ||
                         (e.Entity is CreditNote && !IsPermittedCreditNoteDecision(e)) ||
                         e.Entity is PatientDepositAllocation ||
                         e.Entity is InvoicePrintLog))
            .ToList();

        if (forbiddenEntries.Count != 0)
        {
            var entityNames = string.Join(", ", forbiddenEntries.Select(e => e.Entity.GetType().Name));
            throw new InvalidOperationException($"Audit records are append-only. Modification and deletion of [{entityNames}] are prohibited per FR-08.");
        }
    }

    private static bool IsPermittedImpersonationLogEnd(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return false;
        var endedAtProp = entry.Property(nameof(ImpersonationLog.EndedAt));
        var originalEndedAt = endedAtProp.OriginalValue;
        var currentEndedAt = endedAtProp.CurrentValue;

        // Permitted only when EndedAt transitions from null to a non-null timestamp,
        // and no other property was modified on the ImpersonationLog entry.
        var isEnding = originalEndedAt == null && currentEndedAt != null;
        var onlyEndedAtModified = entry.Properties.All(p => p.Metadata.Name == nameof(ImpersonationLog.EndedAt) || !p.IsModified);

        return isEnding && onlyEndedAtModified;
    }

    /// <summary>
    /// A pending credit note may be moved to Approved or Rejected exactly once by the second approver.
    /// Nothing else on an issued credit note is mutable (FR-08/FR-14 spirit: correct, never overwrite).
    /// </summary>
    private static bool IsPermittedCreditNoteDecision(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return false;

        var originalStatus = (CreditNoteStatus)entry.Property(nameof(CreditNote.Status)).OriginalValue!;
        var currentStatus = (CreditNoteStatus)entry.Property(nameof(CreditNote.Status)).CurrentValue!;
        var isDecision = originalStatus == CreditNoteStatus.PendingApproval
                         && currentStatus is CreditNoteStatus.Approved or CreditNoteStatus.Rejected;

        var allowed = new[]
        {
            nameof(CreditNote.Status),
            nameof(CreditNote.SecondApprovedByUserId),
            nameof(CreditNote.IssuedAt)
        };
        var onlyDecisionFieldsModified = entry.Properties.All(p => allowed.Contains(p.Metadata.Name) || !p.IsModified);

        return isDecision && onlyDecisionFieldsModified;
    }
}
