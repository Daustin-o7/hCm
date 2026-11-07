using System.Security.Claims;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class PharmacyGateEndpointTests
{
    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: $"PharmGateDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static (PharmacyComplianceController controller, HospitalCrmDbContext db, Guid tenantId, Guid doctorId, Guid prescriptionId) CreateControllerWithFixture(
        string activatedModules,
        bool? pharmacyFlagOverride)
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Gate Test Hospital",
            ActivatedModules = activatedModules,
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);

        if (pharmacyFlagOverride.HasValue)
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FlagName = "pharmacy",
                Enabled = pharmacyFlagOverride.Value,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = Guid.NewGuid()
            });
        }

        var clinic = new Clinic
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Gate Test Clinic",
            PharmacyLicenceNumber20 = "FORM20-TEST-123"
        };
        db.Clinics.Add(clinic);

        var doctor = new User
        {
            Id = doctorId,
            TenantId = tenantId,
            ClinicId = clinic.Id,
            Email = "doc@test.com",
            Name = "Dr. Test",
            Role = UserRole.Doctor
        };
        db.Users.Add(doctor);

        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "John Patient",
            DobHasValue = true,
            Dob = new DateOnly(1990, 1, 1),
            Gender = Gender.Male
        };
        db.Patients.Add(patient);

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinic.Id,
            DoctorId = doctorId,
            PatientId = patient.Id,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            TimeSlot = "10:00",
            Status = AppointmentStatus.Completed
        };
        db.Appointments.Add(appointment);

        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            DoctorId = doctorId,
            ChiefComplaint = "Fever",
            Observations = "Normal vitals",
            Diagnosis = "Viral",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Consultations.Add(consultation);

        var prescription = new Prescription
        {
            Id = Guid.NewGuid(),
            ConsultationId = consultation.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Prescriptions.Add(prescription);

        var drug = new Drug
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Paracetamol 500mg",
            GenericName = "Paracetamol",
            ScheduleClass = ScheduleClass.General,
            GstRate = 12m
        };
        db.Drugs.Add(drug);

        var batch = new DrugBatch
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = drug.Id,
            BatchNumber = "BATCH-001",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(12)),
            QuantityReceived = 100,
            QuantityRemaining = 100,
            Mrp = 20m,
            PurchaseRate = 12m
        };
        db.DrugBatches.Add(batch);

        db.SaveChanges();

        var controller = new PharmacyComplianceController(db, NullLogger<PharmacyComplianceController>.Instance);

        var claims = new[]
        {
            new Claim("sub", doctorId.ToString()),
            new Claim("role", "Doctor"),
            new Claim("tenant_id", tenantId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return (controller, db, tenantId, doctorId, prescription.Id);
    }

    private static async Task<bool> ModuleGateAllows(ControllerBase controller, HospitalCrmDbContext db)
    {
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IModuleGateService, ModuleGateService>()
            .BuildServiceProvider();
        controller.ControllerContext.HttpContext.RequestServices = services;

        var actionContext = new ActionContext(controller.ControllerContext.HttpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller);
        var executed = false;

        await new RequireModuleAttribute(ModuleRegistry.Pharmacy).OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], controller));
        });

        if (!executed)
        {
            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(403, result.StatusCode);
            Assert.Contains("module_not_activated", System.Text.Json.JsonSerializer.Serialize(result.Value));
        }

        return executed;
    }

    [Fact]
    public async Task Module_Attribute_Blocks_When_Module_Off()
    {
        // Tenant does NOT have pharmacy in ActivatedModules
        var (controller, db, _, _, _) = CreateControllerWithFixture(
            activatedModules: "core",
            pharmacyFlagOverride: null);

        Assert.False(await ModuleGateAllows(controller, db));
    }

    [Fact]
    public async Task Module_Attribute_Blocks_When_Flag_Overridden_Off()
    {
        // Tenant has pharmacy entitled, but kill-switch override set to false
        var (controller, db, _, _, _) = CreateControllerWithFixture(
            activatedModules: "core,pharmacy",
            pharmacyFlagOverride: false);

        Assert.False(await ModuleGateAllows(controller, db));
    }

    [Fact]
    public async Task Dispense_Proceeds_Past_Gate_When_Module_On()
    {
        // Tenant has pharmacy entitled (with inventory dependency) and flag enabled
        var (controller, db, tenantId, doctorId, prescriptionId) = CreateControllerWithFixture(
            activatedModules: "core,pharmacy,inventory",
            pharmacyFlagOverride: true);

        Assert.True(await ModuleGateAllows(controller, db));

        var req = new DispensePrescriptionRequest(prescriptionId, IdempotencyKey: "module-gate");
        var result = await controller.DispensePrescription(req, CancellationToken.None);

        // Status code must NOT be 403 pharmacy_feature_disabled
        if (result is ObjectResult objResult)
        {
            Assert.NotEqual(403, objResult.StatusCode);
        }
    }
}
