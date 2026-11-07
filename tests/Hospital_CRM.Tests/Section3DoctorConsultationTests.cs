using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section3DoctorConsultationTests
{
    private HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private ConsultationsController CreateController(HospitalCrmDbContext db, Guid doctorId, Guid tenantId, Guid clinicId)
    {
        var controller = new ConsultationsController(db, NullLogger<ConsultationsController>.Instance, tenantContext: new TenantContext());
        var claims = new[]
        {
            new Claim("sub", doctorId.ToString()),
            new Claim("role", "Doctor"),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("clinicId", clinicId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private (User Doctor, Patient Patient, Appointment Appointment, Tenant Tenant) SetupBasicClinic(HospitalCrmDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var patientId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Test Healthcare Clinic",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active,
            ActivatedModules = "core,pharmacy,lab",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenant);

        var clinic = new Clinic
        {
            Id = clinicId,
            TenantId = tenantId,
            Name = "Test Hospital OPD",
            Address = "123 Medical Way",
            Phone = "+919876543210",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Clinics.Add(clinic);

        var doctor = new User
        {
            Id = doctorId,
            TenantId = tenantId,
            Name = "Dr. Anita Desai",
            Email = "dr.anita@hospital.org",
            Role = UserRole.Doctor,
            ClinicId = clinicId,
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(doctor);

        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = doctorId,
            ClinicId = clinicId,
            Role = UserRole.Doctor,
            RegistrationNumber = "MCI-2015-88941",
            Qualifications = "MBBS, MD (Medicine)",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.TenantMemberships.Add(membership);

        var patient = new Patient
        {
            Id = patientId,
            TenantId = tenantId,
            Name = "Rahul Sharma",
            Phone = "+919988776655",
            Gender = Gender.Male,
            ApproxAge = 35,
            Uhid = "UHID-2026-0001",
            CreatedBy = doctorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Patients.Add(patient);

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            DoctorId = doctorId,
            PatientId = patientId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            TimeSlot = "10:30",
            Type = AppointmentType.Scheduled,
            Status = AppointmentStatus.CheckedIn,
            QueueToken = 5,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);

        db.SaveChanges();
        return (doctor, patient, appointment, tenant);
    }

    [Fact]
    public async Task ClaimPatient_TransitionsState_Atomically()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        var result = await controller.ClaimAppointment(appointment.Id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);

        var updatedAppointment = await db.Appointments.FindAsync(appointment.Id);
        Assert.NotNull(updatedAppointment);
        Assert.Equal(AppointmentStatus.InConsultation, updatedAppointment.Status);
    }

    [Fact]
    public async Task ParkAndRecallPatient_UpdatesQueueStatesCorrectly()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        // First claim
        Assert.IsType<OkObjectResult>(await controller.ClaimAppointment(appointment.Id, CancellationToken.None));

        // Park patient (e.g. sent for external test or vitals)
        var parkResult = await controller.ParkAppointment(appointment.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(parkResult);

        var parkedAppt = await db.Appointments.FindAsync(appointment.Id);
        Assert.Equal(AppointmentStatus.Parked, parkedAppt!.Status);

        // Recall patient back to InConsultation
        var recallResult = await controller.RecallAppointment(appointment.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(recallResult);

        var recalledAppt = await db.Appointments.FindAsync(appointment.Id);
        Assert.Equal(AppointmentStatus.InConsultation, recalledAppt!.Status);
    }

    [Fact]
    public async Task SaveDraft_PersistsNonBindingDraft_DoesNotCompleteAppointment()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        var draftRequest = new SaveConsultationDraftRequest(
            ChiefComplaint: "Intermittent headaches and fever",
            Observations: "BP normal, mild conjunctival congestion",
            Diagnosis: "Viral Pyrexia",
            AdvisedInvestigations: "CBC, Dengue NS1 antigen",
            IcdCode: "R50.9",
            IcdTitle: "Fever, unspecified",
            FollowUpDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
            FollowUpNotes: "Review if fever persists > 3 days",
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: null
        );

        var result = await controller.SaveDraft(appointment.Id, draftRequest, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);

        var draft = await db.Consultations.FirstOrDefaultAsync(c => c.AppointmentId == appointment.Id);
        Assert.NotNull(draft);
        Assert.Equal(ConsultationStatus.Draft, draft.Status);
        Assert.Null(draft.SignedAt);

        // Appointment remains InConsultation, NOT completed
        var appt = await db.Appointments.FindAsync(appointment.Id);
        Assert.NotEqual(AppointmentStatus.Completed, appt!.Status);
    }

    [Fact]
    public async Task FinalizeConsultation_AtomicTransaction_LocksRecordAndSetsCompleted()
    {
        var db = CreateInMemoryDb();
        var (doctor, patient, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        var rxItems = new List<StructuredPrescriptionItemRequest>
        {
            new(
                DrugId: null,
                GenericName: "Paracetamol",
                BrandName: "Dolo 650",
                DosageForm: "Tablet",
                Strength: "650mg",
                Route: "Oral",
                Frequency: "1-0-1",
                Duration: 5,
                DurationUnit: "Days",
                Timing: "After food",
                TotalQuantity: 10,
                SpecialInstructions: "Take with warm water",
                ScheduleClass: null,
                IsUnverified: false
            )
        };

        var finalizeRequest = new FinalizeConsultationRequest(
            ChiefComplaint: "Acute throat pain and fever",
            Observations: "Pharyngeal erythema with tonsillar exudates",
            Diagnosis: "Acute Tonsillitis",
            AdvisedInvestigations: "Throat swab culture",
            IcdCode: "J03.90",
            IcdTitle: "Acute tonsillitis, unspecified",
            FollowUpDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)),
            FollowUpNotes: "Review in OPD",
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: rxItems,
            OrderedLabTests: new List<string> { "Complete Blood Count", "CRP" }
        );

        var result = await controller.FinalizeConsultation(appointment.Id, finalizeRequest, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);

        // Verify consultation is finalized & signed
        var finalized = await db.Consultations
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(c => c.AppointmentId == appointment.Id);

        Assert.NotNull(finalized);
        Assert.Equal(ConsultationStatus.Finalized, finalized.Status);
        Assert.NotNull(finalized.SignedAt);
        Assert.Equal(doctor.Id, finalized.SignedByDoctorId);
        Assert.Equal("MCI-2015-88941", finalized.DoctorRegistrationNumber);
        Assert.Equal("MBBS, MD (Medicine)", finalized.DoctorQualifications);
        Assert.NotEmpty(finalized.DigitalSignatureHash!);
        Assert.NotEmpty(finalized.VerificationQrCode!);

        // Verify prescription items
        Assert.NotEmpty(finalized.Prescriptions);
        var item = finalized.Prescriptions.First().Items.First();
        Assert.Equal("Paracetamol", item.GenericName);
        Assert.Equal("Dolo 650", item.BrandName);
        Assert.Equal(10, item.TotalQuantity);

        // Verify appointment marked completed
        var appt = await db.Appointments.FindAsync(appointment.Id);
        Assert.Equal(AppointmentStatus.Completed, appt!.Status);

        // Verify lab orders created
        var labOrders = await db.LabOrders.Where(o => o.ConsultationId == finalized.Id).ToListAsync();
        Assert.Equal(2, labOrders.Count);
    }

    [Fact]
    public async Task FinalizeConsultation_PediatricPrescribingWithoutWeight_ReturnsBadRequest()
    {
        var db = CreateInMemoryDb();
        var (doctor, patient, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        // Set patient to child (< 12 years)
        patient.ApproxAge = 5;
        patient.Dob = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-5));
        await db.SaveChangesAsync();

        var rxItems = new List<StructuredPrescriptionItemRequest>
        {
            new(
                DrugId: null,
                GenericName: "Amoxicillin",
                BrandName: "Novamox Suspension",
                DosageForm: "Syrup",
                Strength: "125mg/5ml",
                Route: "Oral",
                Frequency: "5ml TID",
                Duration: 5,
                DurationUnit: "Days",
                Timing: "After food",
                TotalQuantity: 1,
                SpecialInstructions: null,
                ScheduleClass: "Schedule H",
                IsUnverified: false
            )
        };

        var finalizeRequest = new FinalizeConsultationRequest(
            ChiefComplaint: "Fever and ear tugging",
            Observations: "Otitis media",
            Diagnosis: "Acute Otitis Media",
            AdvisedInvestigations: null,
            IcdCode: null,
            IcdTitle: null,
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: rxItems,
            OrderedLabTests: null
        );

        // Without vitals weight, this MUST fail per pediatric safety requirement
        var result = await controller.FinalizeConsultation(appointment.Id, finalizeRequest, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("pediatric_weight_required", badRequest.Value!.ToString()!);

        // Now record vitals with weight
        db.PatientVitals.Add(new PatientVital
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            AppointmentId = appointment.Id,
            PatientId = patient.Id,
            WeightKg = 18.5m,
            Temperature = 100.4m,
            RecordedByUserId = doctor.Id,
            RecordedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Now finalize should succeed
        var successResult = await controller.FinalizeConsultation(appointment.Id, finalizeRequest, CancellationToken.None);
        Assert.IsType<OkObjectResult>(successResult);
    }

    [Fact]
    public async Task FinalizeConsultation_NdpsDrugPrescribing_BlockedDigitally()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        var rxItems = new List<StructuredPrescriptionItemRequest>
        {
            new(
                DrugId: null,
                GenericName: "Morphine Sulfate",
                BrandName: null,
                DosageForm: "Tablet",
                Strength: "10mg",
                Route: "Oral",
                Frequency: "SOS",
                Duration: 3,
                DurationUnit: "Days",
                Timing: "With food",
                TotalQuantity: 6,
                SpecialInstructions: "Strictly as directed",
                ScheduleClass: "NDPS",
                IsUnverified: false
            )
        };

        var finalizeRequest = new FinalizeConsultationRequest(
            ChiefComplaint: "Severe chronic oncological pain",
            Observations: "Pain score 9/10",
            Diagnosis: "Severe intractable pain",
            AdvisedInvestigations: null,
            IcdCode: null,
            IcdTitle: null,
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: rxItems,
            OrderedLabTests: null
        );

        var result = await controller.FinalizeConsultation(appointment.Id, finalizeRequest, CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ndps_digital_prescribing_prohibited", badRequest.Value!.ToString()!);
    }

    [Fact]
    public async Task AmendConsultation_CreatesImmutableVersion2_WithMandatoryReason()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        // Finalize v1
        var finalizeRequest = new FinalizeConsultationRequest(
            ChiefComplaint: "Cough and mild shortness of breath",
            Observations: "Bilateral rhonchi",
            Diagnosis: "Bronchitis",
            AdvisedInvestigations: "Chest X-Ray",
            IcdCode: "J20.9",
            IcdTitle: "Acute bronchitis, unspecified",
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: null,
            OrderedLabTests: null
        );
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(appointment.Id, finalizeRequest, CancellationToken.None));

        var v1 = await db.Consultations.FirstAsync(c => c.AppointmentId == appointment.Id);

        // Amend without reason -> 400
        var emptyReasonRequest = new AmendConsultationRequest(
            AmendmentReason: "",
            ChiefComplaint: "Worsening cough",
            Observations: "Scattered wheeze",
            Diagnosis: "Acute Asthmatic Bronchitis",
            AdvisedInvestigations: null,
            IcdCode: null,
            IcdTitle: null,
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: null
        );
        var emptyReasonResult = await controller.AmendConsultation(v1.Id, emptyReasonRequest, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptyReasonResult);

        // Amend with reason -> creates v2, preserving v1
        var amendRequest = new AmendConsultationRequest(
            AmendmentReason: "Spirometry result reviewed; patient has acute bronchial hyperreactivity",
            ChiefComplaint: "Worsening cough with wheezing",
            Observations: "Scattered wheezing noted in bilateral lung fields",
            Diagnosis: "Acute Asthmatic Bronchitis",
            AdvisedInvestigations: null,
            IcdCode: null,
            IcdTitle: null,
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: null
        );
        var amendResult = await controller.AmendConsultation(v1.Id, amendRequest, CancellationToken.None);
        var createdResult = Assert.IsType<ObjectResult>(amendResult);
        Assert.Equal(201, createdResult.StatusCode);

        var allVersions = await db.Consultations
            .Where(c => c.AppointmentId == appointment.Id)
            .OrderBy(c => c.Version)
            .ToListAsync();

        Assert.Equal(2, allVersions.Count);
        Assert.Equal(1, allVersions[0].Version);
        Assert.Equal(ConsultationStatus.Finalized, allVersions[0].Status);
        Assert.Equal("Bronchitis", allVersions[0].Diagnosis);
        Assert.Equal("Cough and mild shortness of breath", allVersions[0].ChiefComplaint);

        Assert.Equal(2, allVersions[1].Version);
        Assert.Equal(ConsultationStatus.Amended, allVersions[1].Status);
        Assert.Equal("Acute Asthmatic Bronchitis", allVersions[1].Diagnosis);
        Assert.Equal(allVersions[0].Id, allVersions[1].PreviousVersionId);
        Assert.Equal("Spirometry result reviewed; patient has acute bronchial hyperreactivity", allVersions[1].AmendmentReason);
    }

    [Fact]
    public async Task VerifyBoundPrescription_ReturnsPublicSafeVerificationDetails()
    {
        var db = CreateInMemoryDb();
        var (doctor, patient, appointment, tenant) = SetupBasicClinic(db);
        var controller = CreateController(db, doctor.Id, tenant.Id, appointment.ClinicId);

        var finalizeRequest = new FinalizeConsultationRequest(
            ChiefComplaint: "Acid peptic disease",
            Observations: "Epigastric tenderness",
            Diagnosis: "GERD",
            AdvisedInvestigations: null,
            IcdCode: "K21.9",
            IcdTitle: "Gastro-esophageal reflux disease without esophagitis",
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: new List<StructuredPrescriptionItemRequest>
            {
                new(null, "Pantoprazole", "Pan 40", "Tablet", "40mg", "Oral", "1-0-0", 14, "Days", "30 mins before breakfast", 14, null, "Schedule H", false)
            },
            OrderedLabTests: null
        );
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(appointment.Id, finalizeRequest, CancellationToken.None));

        var finalized = await db.Consultations.FirstAsync(c => c.AppointmentId == appointment.Id);
        var verifyCode = finalized.VerificationQrCode;
        Assert.NotNull(verifyCode);
        Assert.True(PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Prescription, verifyCode));

        // SQL token routing is covered by Part2TenantBoundaryPostgresTests; no authenticated caller is needed after binding.
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var verifyResult = await Part1SaleReadinessTests.VerifyBound(controller, new(tenant.Id, finalized.Id), verifyCode);
        var okResult = Assert.IsType<OkObjectResult>(verifyResult);
        Assert.NotNull(okResult.Value);
        var body = Part1SaleReadinessTests.Json(okResult.Value);
        Assert.True(body.GetProperty("verified").GetBoolean());
        Assert.True(body.GetProperty("signatureValid").GetBoolean());
        Assert.Equal("full_content", body.GetProperty("contentIntegrity").GetString());
        Assert.Equal("valid", body.GetProperty("status").GetString());
        Assert.Equal(verifyCode, body.GetProperty("verificationCode").GetString());
        Assert.Equal("Ra***", body.GetProperty("patientInitials").GetString());
        Assert.Equal(patient.ApproxAge, body.GetProperty("patientAge").GetInt32());
        Assert.Equal(doctor.Name, body.GetProperty("doctorName").GetString());
        Assert.Equal("MCI-2015-88941", body.GetProperty("doctorRegistrationNumber").GetString());
        var medicine = Assert.Single(body.GetProperty("medicines").EnumerateArray());
        Assert.Equal("Pantoprazole", medicine.GetProperty("genericName").GetString());
        Assert.Equal("40mg", medicine.GetProperty("strength").GetString());
        foreach (var privateField in new[] { "patientName", "patientId", "phone", "uhid", "chiefComplaint", "observations", "diagnosis" })
            Assert.False(body.TryGetProperty(privateField, out _), $"Public verification exposes {privateField}.");
        Assert.DoesNotContain(patient.Name, body.GetRawText());
        Assert.DoesNotContain(patient.Phone, body.GetRawText());
        Assert.DoesNotContain(patient.Uhid!, body.GetRawText());
    }
}
