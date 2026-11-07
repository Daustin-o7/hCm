using System.Security.Cryptography;
using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1ClinicalSafetyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task FullContentSeals_VerifyBothVersions_AndExcludeFulfillment()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([Item(null), Item(null) with { GenericName = "Second medicine" }]) with
            { StructuredObservationsJson = "{\"b\":2.00,\"a\":[1e0,true]}", OdontogramJson = "{\"tooth\":11}" }, Ct));
        var original = await db.Consultations.SingleAsync();
        var hash = original.DigitalSignatureHash;
        Assert.StartsWith("v2:", hash);
        var verified = Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, original.Id), original.VerificationQrCode!)).Value);
        Assert.True(verified.GetProperty("verified").GetBoolean());
        Assert.Equal("full_content", verified.GetProperty("contentIntegrity").GetString());
        Assert.Equal("valid", verified.GetProperty("status").GetString());

        var rx = await db.Prescriptions.SingleAsync();
        rx.Items = rx.Items.Reverse().ToList();
        original.StructuredObservationsJson = "{\"a\":[1,true],\"b\":2}";
        var supplied = rx.Items.First();
        supplied.IsDispensed = false;
        supplied.DispensedQuantity = 2;
        supplied.DispensedBatchId = Guid.NewGuid();
        supplied.SubstitutionReason = "Pharmacist recorded substitution";
        supplied.SubstitutedDrugId = Guid.NewGuid();
        await db.SaveChangesAsync();
        Assert.True(Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, original.Id), original.VerificationQrCode!)).Value).GetProperty("signatureValid").GetBoolean());

        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.AmendConsultation(original.Id, Amend(null), Ct)).StatusCode);
        var amended = await db.Consultations.SingleAsync(c => c.PreviousVersionId == original.Id);
        Assert.Equal(hash, original.DigitalSignatureHash);
        Assert.NotEqual(hash, amended.DigitalSignatureHash);
        var older = Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, original.Id), original.VerificationQrCode!, 1)).Value);
        Assert.True(older.GetProperty("verified").GetBoolean());
        Assert.Equal("superseded", older.GetProperty("status").GetString());
        var active = Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, original.Id), original.VerificationQrCode!, 2)).Value);
        Assert.True(active.GetProperty("verified").GetBoolean());
        Assert.Equal("valid", active.GetProperty("status").GetString());
        var missing = Json(Assert.IsType<NotFoundObjectResult>(await VerifyBound(controller, new(tenant, amended.Id), original.VerificationQrCode!, 3)).Value);
        Assert.Equal("prescription_version_not_found", missing.GetProperty("error").GetString());
        Assert.Equal(2, missing.GetProperty("latestVersion").GetInt32());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("v2:BAD")]
    [InlineData("")]
    public async Task LegacyAndMalformedSeals_CannotClaimFullVerification(string seal)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        var record = await db.Consultations.SingleAsync();
        record.DigitalSignatureHash = seal == "legacy"
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{record.DoctorId}|{record.AppointmentId}|{record.Id}|{record.Diagnosis}|{record.SignedAt!.Value.ToUnixTimeSeconds()}")))
            : seal;
        await db.SaveChangesAsync();
        var result = Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, record.Id), record.VerificationQrCode!)).Value);
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.False(result.GetProperty("signatureValid").GetBoolean());
        Assert.Equal(seal == "legacy" ? "legacy_limited" : "invalid", result.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("suspended")]
    [InlineData("invited")]
    [InlineData("wrong_role")]
    [InlineData("wrong_clinic")]
    [InlineData("wrong_tenant")]
    [InlineData("blank_registration")]
    [InlineData("blank_qualifications")]
    [InlineData("fabricated_registration")]
    public async Task InvalidSigningMembership_DeniesFinalizeAndAmendWithoutSideEffects(string scenario)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        var original = await db.Consultations.SingleAsync();
        var membership = await db.TenantMemberships.SingleAsync();
        switch (scenario)
        {
            case "missing": db.TenantMemberships.Remove(membership); break;
            case "suspended": case "invited": membership.Status = scenario; break;
            case "wrong_role": membership.Role = UserRole.Receptionist; break;
            case "wrong_clinic": membership.ClinicId = Guid.NewGuid(); break;
            case "wrong_tenant": membership.TenantId = Guid.NewGuid(); break;
            case "blank_registration": membership.RegistrationNumber = "  "; break;
            case "blank_qualifications": membership.Qualifications = "  "; break;
            case "fabricated_registration": membership.RegistrationNumber = " NMC-VERIFIED-PRACTITIONER "; break;
        }
        var audits = await db.PatientAuditLogs.CountAsync();
        await db.SaveChangesAsync();
        AssertSigningDenied(await controller.AmendConsultation(original.Id, Amend([Item(null)]), Ct));
        Assert.Single(db.Consultations);
        Assert.Equal(audits, await db.PatientAuditLogs.CountAsync());
        Assert.Empty(db.PrescriptionItems);
        Assert.Equal("SYNTHETIC-REG", original.DoctorRegistrationNumber);

        var newVisit = new Appointment { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = visit.ClinicId, DoctorId = doctor.Id,
            PatientId = visit.PatientId, Date = visit.Date.AddDays(1), TimeSlot = "11:00", Status = AppointmentStatus.CheckedIn };
        db.Appointments.Add(newVisit);
        await db.SaveChangesAsync();
        AssertSigningDenied(await controller.FinalizeConsultation(newVisit.Id,
            Part1SaleReadinessTests.Finalize([Item(null)]) with { OrderedLabTests = ["Test"], FollowUpDate = visit.Date.AddDays(7) }, Ct));
        Assert.Single(db.Consultations);
        Assert.Empty(db.LabOrders);
        Assert.Equal(2, await db.Appointments.CountAsync());
        Assert.Equal(AppointmentStatus.CheckedIn, newVisit.Status);
        Assert.Equal(audits, await db.PatientAuditLogs.CountAsync());

        void AssertSigningDenied(IActionResult result)
        {
            if (scenario is "blank_registration" or "blank_qualifications" or "fabricated_registration")
                Assert.Equal("signing_credentials_required", Json(Assert.IsType<BadRequestObjectResult>(result).Value).GetProperty("error").GetString());
            else Assert.IsType<ForbidResult>(result);
        }
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("zero", false)]
    [InlineData("negative", false)]
    [InlineData("stale", false)]
    [InlineData("other_encounter", false)]
    [InlineData("current", true)]
    public async Task PediatricWeight_MustBePositiveCurrentEncounterMeasurement(string scenario, bool allowed)
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        patient.ApproxAge = 7;
        if (scenario != "missing")
            db.PatientVitals.Add(new PatientVital { Id = Guid.NewGuid(), TenantId = tenant, PatientId = patient.Id,
                AppointmentId = scenario == "other_encounter" ? Guid.NewGuid() : visit.Id, RecordedByUserId = doctor.Id,
                WeightKg = scenario == "zero" ? 0m : scenario == "negative" ? -1m : 20m,
                RecordedAt = scenario == "stale" ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var result = await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(null)]), Ct);
        if (allowed) Assert.IsType<OkObjectResult>(result);
        else
        {
            Assert.Equal("pediatric_weight_required", Json(Assert.IsType<BadRequestObjectResult>(result).Value).GetProperty("error").GetString());
            Assert.Empty(db.Consultations);
            Assert.Empty(db.PatientAuditLogs);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PediatricAge_UsesBirthdayNotYearDifference(bool twelfthBirthdayToday)
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var today = new TimezoneService().GetClinicToday((await db.Clinics.SingleAsync()).Timezone);
        patient.Dob = today.AddYears(-12).AddDays(twelfthBirthdayToday ? 0 : 1);
        await db.SaveChangesAsync();
        var result = await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(null)]), Ct);
        if (twelfthBirthdayToday) Assert.IsType<OkObjectResult>(result);
        else Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task FinalizeAndAmend_ReturnSameAllergyAndDuplicateWarnings()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        db.PatientVitals.Add(new PatientVital { Id = Guid.NewGuid(), TenantId = tenant, PatientId = patient.Id,
            AppointmentId = visit.Id, RecordedByUserId = doctor.Id, Allergies = "Synthetic drug", RecordedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var controller = Consult(db, doctor.Id, tenant);
        var first = Json(Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(null), Item(null)]), Ct)).Value);
        var original = await db.Consultations.SingleAsync();
        var next = Json(Assert.IsType<ObjectResult>(await controller.AmendConsultation(original.Id, Amend(null), Ct)).Value);
        Assert.Equal(first.GetProperty("warnings").GetRawText(), next.GetProperty("warnings").GetRawText());
        Assert.Equal(3, first.GetProperty("warnings").GetArrayLength());
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("duration")]
    [InlineData("name")]
    [InlineData("json")]
    public async Task InvalidEffectiveContent_IsRejectedBeforeLabFollowUpOrSigning(string field)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var item = field switch { "quantity" => Item(null) with { TotalQuantity = 0 }, "duration" => Item(null) with { Duration = 0 },
            "name" => Item(null) with { GenericName = " " }, _ => Item(null) };
        var result = await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([item]) with
            { OrderedLabTests = ["Test"], FollowUpDate = visit.Date.AddDays(7), StructuredObservationsJson = field == "json" ? "{\"a\":1,\"a\":2}" : null }, Ct);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.Consultations);
        Assert.Empty(db.LabOrders);
        Assert.Empty(db.PatientAuditLogs);
        Assert.Single(db.Appointments);
    }

    [Fact]
    public async Task LegacySigning_ValidatesStoredList_AndRequiresReasonedAmendmentRoute()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Draft", [Item(null) with { ScheduleClass = "NDPS" }]), Ct));
        var draft = await db.Consultations.SingleAsync();
        var request = new CreateConsultationRequest("Complaint", null, "Diagnosis", null, draft.Id, draft.Version, draft.UpdatedAt);
        var ndps = Assert.IsType<BadRequestObjectResult>(await controller.CreateConsultation(visit.Id, request, Ct));
        Assert.Equal("ndps_digital_prescribing_prohibited", Json(ndps.Value).GetProperty("error").GetString());
        Assert.Equal(ConsultationStatus.Draft, (await db.Consultations.SingleAsync()).Status);
        var amendment = Assert.IsType<BadRequestObjectResult>(await controller.CreateConsultation(visit.Id, request with { PreviousVersionId = Guid.NewGuid() }, Ct));
        Assert.Equal("amendment_endpoint_required", Json(amendment.Value).GetProperty("error").GetString());
        Assert.Single(db.Consultations);
        Assert.Equal(ScheduleClass.NDPS, (await db.PrescriptionItems.SingleAsync()).ScheduleClass);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("resource")]
    [InlineData("token")]
    [InlineData("draft")]
    public async Task BoundVerification_RechecksExactTenantResourceTokenAndSignedStatus(string mismatch)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        var signed = await db.Consultations.SingleAsync();
        var binding = new PublicTokenBinding(mismatch == "tenant" ? Guid.NewGuid() : tenant, mismatch == "resource" ? Guid.NewGuid() : signed.Id);
        if (mismatch == "draft") { signed.Status = ConsultationStatus.Draft; await db.SaveChangesAsync(); }
        var result = Assert.IsType<NotFoundObjectResult>(await VerifyBound(controller, binding, mismatch == "token" ? "RX-000000000000" : signed.VerificationQrCode!));
        Assert.Equal("prescription_not_found_or_unverified", Json(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task BoundVerification_PushesTenantForFilteredQueries_AndRestoresPriorScope()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var seed = new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
        var (doctor, _, visit, tenant) = Seed(seed);
        Assert.IsType<OkObjectResult>(await Consult(seed, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        var signed = await seed.Consultations.SingleAsync();
        var other = Seed(seed);
        Assert.IsType<OkObjectResult>(await Consult(seed, other.Doctor.Id, other.Tenant).FinalizeConsultation(other.Visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));

        var tenantContext = new TenantContext();
        using var priorScope = tenantContext.PushTenant(other.Tenant);
        await using var scopedDb = new HospitalCrmDbContext(options, tenantContext);
        Assert.Equal(other.Visit.Id, (await scopedDb.Consultations.SingleAsync()).AppointmentId);
        var controller = new ConsultationsController(scopedDb, NullLogger<ConsultationsController>.Instance, tenantContext: tenantContext);
        var result = Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, signed.Id), signed.VerificationQrCode!)).Value);
        Assert.True(result.GetProperty("verified").GetBoolean());
        Assert.Equal(signed.VerificationQrCode, result.GetProperty("verificationCode").GetString());
        Assert.Equal(other.Tenant, tenantContext.CurrentTenantId);
        Assert.Equal(other.Visit.Id, (await scopedDb.Consultations.SingleAsync()).AppointmentId);
    }
}
