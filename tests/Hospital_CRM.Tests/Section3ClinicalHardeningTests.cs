using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// Section 3 hardening: the gaps found while re-verifying the consultation / e-Rx claims.
/// </summary>
public class Section3ClinicalHardeningTests
{
    private HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static void SetCaller(ControllerBase controller, Guid userId, string role, Guid tenantId, Guid clinicId)
    {
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", role),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("clinicId", clinicId.ToString())
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    private static JsonElement ToJson(object? value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static User SeedNurse(HospitalCrmDbContext db, Guid tenantId, Guid clinicId)
    {
        var nurse = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Nurse,
            Name = "Synthetic nurse", Email = "nurse@test.invalid", PasswordHash = "not-a-login" };
        db.Users.Add(nurse);
        db.TenantMemberships.Add(new TenantMembership { UserId = nurse.Id, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Nurse });
        db.SaveChanges();
        return nurse;
    }

    private (User Doctor, Patient Patient, Appointment Appointment, Guid TenantId, Guid ClinicId) SetupClinic(
        HospitalCrmDbContext db, AppointmentStatus status = AppointmentStatus.CheckedIn)
    {
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var patientId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Hardening Clinic",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active,
            ActivatedModules = "core,lab",
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Clinics.Add(new Clinic
        {
            Id = clinicId,
            TenantId = tenantId,
            Name = "Hardening OPD",
            Address = "1 Test Road",
            Phone = "+919876500000",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var doctor = new User
        {
            Id = doctorId,
            TenantId = tenantId,
            Name = "Dr. Hardening",
            Email = "hardening@clinic.test",
            Role = UserRole.Doctor,
            ClinicId = clinicId,
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(doctor);

        db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = doctorId,
            ClinicId = clinicId,
            Role = UserRole.Doctor,
            RegistrationNumber = "MCI-2015-88941",
            Qualifications = "MBBS, MD",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var patient = new Patient
        {
            Id = patientId,
            TenantId = tenantId,
            Name = "Test Patient",
            Phone = "+919900112233",
            Gender = Gender.Male,
            ApproxAge = 34,
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
            TimeSlot = "09:00",
            Type = AppointmentType.Scheduled,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);
        db.SaveChanges();

        return (doctor, patient, appointment, tenantId, clinicId);
    }

    private static FinalizeConsultationRequest FinalizeRequest(
        List<StructuredPrescriptionItemRequest>? items = null, string diagnosis = "Viral fever") =>
        new(
            ChiefComplaint: "Fever",
            Observations: "Febrile, hydrated",
            Diagnosis: diagnosis,
            AdvisedInvestigations: null,
            IcdCode: "R50.9",
            IcdTitle: "Fever, unspecified",
            FollowUpDate: null,
            FollowUpNotes: null,
            TemplateId: null,
            TemplateVersion: null,
            StructuredObservationsJson: null,
            PrescriptionItems: items,
            OrderedLabTests: null);

    [Fact]
    public void ContextPanel_IsServedAtTheDocumentedClinicalContextRoute()
    {
        var template = typeof(ConsultationsController)
            .GetMethod(nameof(ConsultationsController.GetPatientContextPanel))!
            .GetCustomAttributes(typeof(HttpMethodAttribute), false)
            .Cast<HttpMethodAttribute>()
            .Select(a => a.Template)
            .ToList();

        Assert.Contains("patients/{patientId:guid}/clinical-context", template);
    }

    [Fact]
    public async Task Finalize_SecondTime_IsRejectedAsAlreadyFinalized()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db);
        var controller = new ConsultationsController(db);
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        var first = await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);

        var second = await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(), CancellationToken.None) as ConflictObjectResult;

        Assert.NotNull(second);
        Assert.Equal("consultation_already_finalized", ToJson(second.Value).GetProperty("error").GetString());
        Assert.Equal(1, await db.Consultations.CountAsync(c => c.AppointmentId == appointment.Id));
    }

    [Fact]
    public async Task Finalize_RejectsAndDraftDoesNotReopenAClosedVisit()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db, AppointmentStatus.Completed);
        var controller = new ConsultationsController(db);
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        var finalize = await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(), CancellationToken.None) as ConflictObjectResult;
        Assert.NotNull(finalize);
        Assert.Equal("appointment_not_in_consultation", ToJson(finalize.Value).GetProperty("error").GetString());

        var draft = await controller.SaveDraft(
            appointment.Id,
            new SaveConsultationDraftRequest("Fever", null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None) as ConflictObjectResult;

        Assert.NotNull(draft);
        Assert.Equal("appointment_closed", ToJson(draft.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task SaveDraft_AfterFinalize_IsRejected()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db);
        var controller = new ConsultationsController(db);
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(), CancellationToken.None));

        var draft = await controller.SaveDraft(
            appointment.Id,
            new SaveConsultationDraftRequest("Fever", null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None) as ConflictObjectResult;

        Assert.NotNull(draft);
        Assert.Equal("consultation_already_finalized", ToJson(draft.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Amend_WithoutItems_KeepsThePrescription()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db);
        var controller = new ConsultationsController(db);
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        var items = new List<StructuredPrescriptionItemRequest>
        {
            new(null, "Paracetamol", "Dolo 650", "Tablet", "650mg", "Oral", "1-1-1", 3, "Days", "After food", 9, null, "General", false)
        };
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(items), CancellationToken.None));
        var v1 = await db.Consultations.FirstAsync(c => c.AppointmentId == appointment.Id);

        var amended = await controller.AmendConsultation(
            v1.Id,
            new AmendConsultationRequest(
                AmendmentReason: "Diagnosis refined after review",
                ChiefComplaint: null, Observations: null, Diagnosis: "Dengue fever",
                AdvisedInvestigations: null, IcdCode: null, IcdTitle: null, FollowUpDate: null,
                FollowUpNotes: null, TemplateId: null, TemplateVersion: null,
                StructuredObservationsJson: null, PrescriptionItems: null),
            CancellationToken.None);

        Assert.Equal(201, Assert.IsType<ObjectResult>(amended).StatusCode);

        var v2 = await db.Consultations
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .FirstAsync(c => c.PreviousVersionId == v1.Id);

        Assert.Equal(2, v2.Version);
        var item = Assert.Single(v2.Prescriptions.SelectMany(p => p.Items));
        Assert.Equal("Paracetamol", item.GenericName);
        Assert.Equal("650mg", item.Strength);
        Assert.Equal("1-1-1", item.Frequency);
        Assert.Equal(9, item.TotalQuantity);
        Assert.Equal("Viral fever", v1.Diagnosis);
        Assert.Equal("Dengue fever", v2.Diagnosis);
        Assert.NotEqual(Assert.Single(v1.Prescriptions.SelectMany(p => p.Items)).Id, item.Id);
    }

    [Fact]
    public async Task Claim_ByAnotherDoctor_DoesNotStealTheVisit()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db);

        var otherDoctor = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Dr. Other",
            Email = "other@clinic.test",
            Role = UserRole.Doctor,
            ClinicId = clinicId,
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(otherDoctor);
        db.TenantMemberships.Add(new TenantMembership { UserId = otherDoctor.Id, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Doctor });
        await db.SaveChangesAsync();

        var controller = new ConsultationsController(db);
        SetCaller(controller, otherDoctor.Id, "Doctor", tenantId, clinicId);

        var result = await controller.ClaimAppointment(appointment.Id, CancellationToken.None) as ConflictObjectResult;

        Assert.NotNull(result);
        Assert.Equal("appointment_assigned_to_another_doctor", ToJson(result.Value).GetProperty("error").GetString());

        var stored = await db.Appointments.FindAsync(appointment.Id);
        Assert.Equal(doctor.Id, stored!.DoctorId);
        Assert.Equal(AppointmentStatus.CheckedIn, stored.Status);
    }

    [Fact]
    public async Task Finalize_BlocksNdpsSourcedFromTheDrugCatalogue()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db);

        var drug = new Drug
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Morphine 10mg",
            GenericName = "Morphine",
            TherapeuticCategory = "Analgesic",
            ScheduleClass = ScheduleClass.NDPS
        };
        db.Drugs.Add(drug);
        await db.SaveChangesAsync();

        var controller = new ConsultationsController(db);
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        // No scheduleClass in the request: the catalogue alone must block it.
        var items = new List<StructuredPrescriptionItemRequest>
        {
            new(drug.Id, "Morphine", null, "Injection", "10mg", "IV", "PRN", 1, "Days", "Bedtime", 1, null, null, false)
        };

        var result = await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(items), CancellationToken.None) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal("ndps_digital_prescribing_prohibited", ToJson(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task VerifyBound_RecomputesTheSignatureAndReturnsTheLatestVersion()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId, clinicId) = SetupClinic(db);
        var controller = new ConsultationsController(db, NullLogger<ConsultationsController>.Instance, tenantContext: new TenantContext());
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(appointment.Id, FinalizeRequest(), CancellationToken.None));
        var v1 = await db.Consultations.FirstAsync(c => c.AppointmentId == appointment.Id);
        Assert.True(PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Prescription, v1.VerificationQrCode));

        // SQL token routing is covered by Part2TenantBoundaryPostgresTests; exercise the bound clinical stage here.
        var verified = await Part1SaleReadinessTests.VerifyBound(controller, new(tenantId, v1.Id), v1.VerificationQrCode!) as OkObjectResult;
        Assert.NotNull(verified);
        var body = ToJson(verified.Value);
        Assert.True(body.GetProperty("signatureValid").GetBoolean());
        Assert.Equal(1, body.GetProperty("version").GetInt32());

        // The same code must resolve to the newest version after an amendment.
        var amendment = await controller.AmendConsultation(
            v1.Id,
            new AmendConsultationRequest(
                AmendmentReason: "Added diagnosis detail",
                ChiefComplaint: null, Observations: null, Diagnosis: "Viral fever with rash",
                AdvisedInvestigations: null, IcdCode: null, IcdTitle: null, FollowUpDate: null,
                FollowUpNotes: null, TemplateId: null, TemplateVersion: null,
                StructuredObservationsJson: null, PrescriptionItems: null),
            CancellationToken.None);
        Assert.Equal(201, Assert.IsType<ObjectResult>(amendment).StatusCode);
        var v2 = await db.Consultations.SingleAsync(c => c.PreviousVersionId == v1.Id);

        var afterAmend = await Part1SaleReadinessTests.VerifyBound(controller, new(tenantId, v2.Id), v1.VerificationQrCode!) as OkObjectResult;
        Assert.NotNull(afterAmend);
        var amendedBody = ToJson(afterAmend.Value);
        Assert.Equal(2, amendedBody.GetProperty("version").GetInt32());
        Assert.True(amendedBody.GetProperty("amended").GetBoolean());
        Assert.True(amendedBody.GetProperty("signatureValid").GetBoolean());
        Assert.Equal("valid", amendedBody.GetProperty("status").GetString());

        var original = ToJson(Assert.IsType<OkObjectResult>(await Part1SaleReadinessTests.VerifyBound(controller, new(tenantId, v2.Id), v1.VerificationQrCode!, 1)).Value);
        Assert.Equal(1, original.GetProperty("version").GetInt32());
        Assert.True(original.GetProperty("signatureValid").GetBoolean());
        Assert.Equal("superseded", original.GetProperty("status").GetString());

        v2.Diagnosis = "Tampered diagnosis";
        await db.SaveChangesAsync();
        var tampered = ToJson(Assert.IsType<OkObjectResult>(await Part1SaleReadinessTests.VerifyBound(controller, new(tenantId, v2.Id), v1.VerificationQrCode!)).Value);
        Assert.False(tampered.GetProperty("signatureValid").GetBoolean());
        Assert.False(tampered.GetProperty("verified").GetBoolean());
        Assert.Equal("invalid", tampered.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LabReview_RequiresAResult()
    {
        var db = CreateInMemoryDb();
        var (doctor, patient, appointment, tenantId, clinicId) = SetupClinic(db);

        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment.Id,
            DoctorId = doctor.Id,
            Status = ConsultationStatus.Finalized,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Consultations.Add(consultation);

        var order = new LabOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConsultationId = consultation.Id,
            PatientId = patient.Id,
            DoctorId = doctor.Id,
            TestName = "CBC",
            Status = LabOrderStatus.Ordered,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LabOrders.Add(order);
        await db.SaveChangesAsync();

        var controller = new LabOrdersController(db, new ConfigurationBuilder().Build());
        SetCaller(controller, doctor.Id, "Doctor", tenantId, clinicId);

        var result = await controller.DoctorReview(order.Id, new DoctorReviewLabOrderRequest("Looks fine", null), CancellationToken.None)
            as ConflictObjectResult;

        Assert.NotNull(result);
        Assert.Equal("no_result_to_review", ToJson(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Vitals_Overwrite_IsAuditedWithThePriorValues()
    {
        var db = CreateInMemoryDb();
        var (_, patient, appointment, tenantId, clinicId) = SetupClinic(db);
        var nurse = SeedNurse(db, tenantId, clinicId);
        var controller = new VitalsController(db, NullLogger<VitalsController>.Instance);
        SetCaller(controller, nurse.Id, "Nurse", tenantId, clinicId);

        Assert.IsType<OkObjectResult>(await controller.RecordVitals(appointment.Id,
            new RecordVitalsRequest(120, 80, 72, 98.6m, 16, 98, 70m, 170m, 95m, null, null), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.RecordVitals(appointment.Id,
            new RecordVitalsRequest(150, 95, 88, 100.4m, 20, 96, 70m, 170m, 95m, null, null), CancellationToken.None));

        var audit = await db.PatientAuditLogs.SingleAsync(a => a.FieldName == "VITALS_AMENDED");
        Assert.Equal(patient.Id, audit.PatientId);
        Assert.Equal(nurse.Id, audit.ChangedBy);
        Assert.Contains("BP 120/80", audit.OldValue);
        Assert.Contains("BP 150/95", audit.NewValue);
    }

    [Fact]
    public async Task Vitals_UnchangedResubmit_DoesNotWriteAnAmendment()
    {
        var db = CreateInMemoryDb();
        var (_, _, appointment, tenantId, clinicId) = SetupClinic(db);
        var nurse = SeedNurse(db, tenantId, clinicId);
        var controller = new VitalsController(db, NullLogger<VitalsController>.Instance);
        SetCaller(controller, nurse.Id, "Nurse", tenantId, clinicId);

        var request = new RecordVitalsRequest(120, 80, 72, 98.6m, 16, 98, 70m, 170m, 95m, null, null);
        Assert.IsType<OkObjectResult>(await controller.RecordVitals(appointment.Id, request, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.RecordVitals(appointment.Id, request, CancellationToken.None));

        Assert.Equal(0, await db.PatientAuditLogs.CountAsync(a => a.FieldName == "VITALS_AMENDED"));
    }
}
