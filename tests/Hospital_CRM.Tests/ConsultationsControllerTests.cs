using System.Security.Claims;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class ConsultationsControllerTests
{
    private HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private ConsultationsController CreateController(HospitalCrmDbContext db, Guid doctorId, Guid tenantId, Guid clinicId)
    {
        var controller = new ConsultationsController(db);
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

    // Clinical notes and prescriptions are doctor/admin-only; front-desk staff book appointments, they do not read charts.
    [Theory]
    [InlineData("List")]
    [InlineData("Get")]
    [InlineData("GetPrescriptions")]
    public void ClinicalReadActions_DoNotAdmitReceptionist(string action)
    {
        var roles = typeof(ConsultationsController)
            .GetMethod(action)!
            .GetCustomAttributes(typeof(AuthorizeRolesAttribute), inherit: false)
            .Cast<AuthorizeRolesAttribute>()
            .SelectMany(a => (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        Assert.DoesNotContain("Receptionist", roles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Doctor", roles);
        Assert.Contains("ClinicAdmin", roles);
    }

    [Fact]
    public async Task CreateConsultation_SavesNote_AndCanBeAmended()
    {
        var db = CreateInMemoryDb();
        var doctorId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Synthetic clinic tenant" });
        db.Clinics.Add(new Clinic { Id = clinicId, TenantId = tenantId, Name = "Synthetic clinic" });

        var doctor = new User
        {
            Id = doctorId,
            Name = "Dr. Test",
            Role = UserRole.Doctor,
            ClinicId = clinicId,
            TenantId = tenantId,
            Email = "dr.test@samstack.ai",
            PasswordHash = "hash"
        };
        db.Users.Add(doctor);

        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Patient One",
            Phone = "9988776655",
            CreatedBy = doctorId
        };
        db.Patients.Add(patient);

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            DoctorId = doctorId,
            PatientId = patient.Id,
            ClinicId = clinicId,
            TenantId = tenantId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            TimeSlot = "10:30",
            Status = AppointmentStatus.CheckedIn,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);
        db.TenantMemberships.Add(new TenantMembership { UserId = doctorId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Doctor, RegistrationNumber = "TEST-REG", Qualifications = "TEST-QUAL" });
        await db.SaveChangesAsync();

        var controller = CreateController(db, doctorId, tenantId, clinicId);

        // 1. Initial Consultation (v1)
        var createRequest = new CreateConsultationRequest(
            ChiefComplaint: "Fever and sore throat",
            Observations: "Tonsillar hyperemia",
            Diagnosis: "Acute Pharyngitis",
            PreviousVersionId: null
        );

        var result = await controller.CreateConsultation(appointment.Id, createRequest, CancellationToken.None);
        var createdResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, createdResult.StatusCode);

        var savedConsultation = await db.Consultations.FirstOrDefaultAsync(c => c.AppointmentId == appointment.Id);
        Assert.NotNull(savedConsultation);
        Assert.Equal(1, savedConsultation.Version);
        Assert.Equal("Fever and sore throat", savedConsultation.ChiefComplaint);
        Assert.Equal(ConsultationStatus.Finalized, savedConsultation.Status);
        var originalSeal = savedConsultation.DigitalSignatureHash;
        Assert.StartsWith("v2:", originalSeal);

        // 2. Amend Consultation (v2) - Non-destructive clinical amendment
        var amendRequest = new AmendConsultationRequest(
            AmendmentReason: "Patient reported persistent mild cough on review",
            ChiefComplaint: "Fever, sore throat, and mild cough",
            Observations: "Tonsillar hyperemia + clear chest",
            Diagnosis: "Acute Viral Pharyngitis",
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

        var amendResult = await controller.AmendConsultation(savedConsultation.Id, amendRequest, CancellationToken.None);
        var amendCreated = Assert.IsType<ObjectResult>(amendResult);
        Assert.Equal(201, amendCreated.StatusCode);

        var allVersions = await db.Consultations.Where(c => c.AppointmentId == appointment.Id).OrderBy(c => c.Version).ToListAsync();
        Assert.Equal(2, allVersions.Count);
        Assert.Equal(1, allVersions[0].Version);
        Assert.Equal(2, allVersions[1].Version);
        Assert.Equal(allVersions[0].Id, allVersions[1].PreviousVersionId);
        Assert.Equal("Fever and sore throat", allVersions[0].ChiefComplaint);
        Assert.Equal("Acute Pharyngitis", allVersions[0].Diagnosis);
        Assert.Equal(originalSeal, allVersions[0].DigitalSignatureHash);
        Assert.Equal("Acute Viral Pharyngitis", allVersions[1].Diagnosis);
        Assert.Equal(amendRequest.AmendmentReason, allVersions[1].AmendmentReason);
    }

    [Fact]
    public async Task AddPrescription_AttachesPrescriptionItems()
    {
        var db = CreateInMemoryDb();
        var (doctor, _, appointment, tenantId) = Part1SaleReadinessTests.Seed(db);
        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment.Id,
            DoctorId = doctor.Id,
            ChiefComplaint = "Cough",
            Diagnosis = "Bronchitis",
            Status = ConsultationStatus.Draft,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Consultations.Add(consultation);
        await db.SaveChangesAsync();

        var controller = CreateController(db, doctor.Id, tenantId, appointment.ClinicId);

        var rxRequest = new AddPrescriptionRequest(new List<PrescriptionItemRequest>
        {
            new("Tab. Azithromycin 500mg", "1 Tab", "OD", "3 Days"),
            new("Syp. CoughRelief", "10ml", "TID", "5 Days")
        });

        var result = await controller.AddPrescription(consultation.Id, rxRequest, CancellationToken.None);
        var createdResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, createdResult.StatusCode);

        var prescription = await db.Prescriptions.FirstOrDefaultAsync(p => p.ConsultationId == consultation.Id);
        Assert.NotNull(prescription);

        var items = await db.PrescriptionItems.Where(pi => pi.PrescriptionId == prescription.Id).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(tenantId, prescription.TenantId);
        Assert.Contains(items, i => i.MedicineText == "Tab. Azithromycin 500mg" && i.DosageText == "1 Tab" && i.FrequencyText == "OD" && i.DurationText == "3 Days");
        Assert.Contains(items, i => i.MedicineText == "Syp. CoughRelief" && i.DosageText == "10ml" && i.FrequencyText == "TID" && i.DurationText == "5 Days");
        Assert.Equal(ConsultationStatus.Draft, consultation.Status);
        Assert.Null(consultation.SignedAt);
        Assert.NotNull(consultation.UpdatedAt);
    }
}
