using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1PatientRegistrationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static PatientRegisterRequest Adult() => new("Synthetic adult", "9876543210", null, 30, "PreferNotToSay", null,
        new(true, "care_delivery"), $"IDEMP-PAT-{Guid.NewGuid()}", NoticeVersion: "v1.0", Language: "en", CaptureMethod: "receptionist_assisted");

    private static PatientsController Controller(HospitalCrmDbContext db, Guid? tenant = null)
    {
        var controller = new PatientsController(db, new FakePatientSearchService());
        Caller(controller, Guid.NewGuid(), tenant ?? Guid.NewGuid(), "Receptionist");
        return controller;
    }

    private static JsonElement Body(IActionResult action) => JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(action).Value);

    [Fact]
    public async Task Registration_ReturnsPersistedIdentityAndPurposeSpecificConsentEvidence()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        var request = Adult() with { WhatsAppOptIn = true };
        var result = Assert.IsType<ObjectResult>(await controller.Register(request, Ct));
        Assert.Equal(201, result.StatusCode);
        var body = Body(result);
        var patient = await db.Patients.SingleAsync();
        Assert.Equal(patient.Id, body.GetProperty("patientId").GetGuid());
        Assert.Equal(patient.Uhid, body.GetProperty("uhid").GetString());
        Assert.Equal(request.IdempotencyKey, body.GetProperty("idempotencyKey").GetString());
        var evidence = body.GetProperty("consents").EnumerateArray().ToList();
        Assert.Equal(2, evidence.Count);
        foreach (var consent in await db.PatientConsents.ToListAsync())
        {
            var saved = Assert.Single(evidence, c => c.GetProperty("id").GetGuid() == consent.Id);
            Assert.Equal(consent.PatientId, saved.GetProperty("patientId").GetGuid());
            Assert.Equal(consent.Purpose, saved.GetProperty("purpose").GetString());
            Assert.Equal(consent.CapturedBy, saved.GetProperty("capturedBy").GetGuid());
            Assert.Equal(consent.CapturedAt, saved.GetProperty("capturedAt").GetDateTimeOffset());
            Assert.Equal("v1.0", saved.GetProperty("noticeVersion").GetString());
        }
        Assert.Single(db.PatientAuditLogs);
    }

    [Fact]
    public async Task Replay_OriginalRequestSurvivesDemographicEditAndWithdrawal_ChangedRequestConflicts()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        var request = Adult() with { WhatsAppOptIn = true };
        await controller.Register(request, Ct);
        var patient = await db.Patients.SingleAsync();
        Assert.IsType<OkObjectResult>(await controller.Patch(patient.Id, new("Corrected name", null, null, null, null, "Corrected address"), Ct));
        var consent = await db.PatientConsents.SingleAsync(c => c.Purpose == "care_delivery");
        await controller.WithdrawConsent(patient.Id, consent.Id, Ct);
        var auditCount = await db.PatientAuditLogs.CountAsync();
        db.ChangeTracker.Clear();

        var body = Body(Assert.IsType<OkObjectResult>(await controller.Register(request, Ct)));
        Assert.True(body.GetProperty("idempotentReplay").GetBoolean());
        Assert.Equal(patient.Id, body.GetProperty("patientId").GetGuid());
        Assert.Equal("Corrected name", body.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("consents").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == consent.Id).GetProperty("withdrawnAt").ValueKind);
        Assert.IsType<ConflictObjectResult>(await controller.Register(request with { Name = "Corrected name", Address = "Corrected address" }, Ct));
        Assert.IsType<ConflictObjectResult>(await controller.Register(request with { WhatsAppOptIn = false }, Ct));
        Assert.IsType<ConflictObjectResult>(await controller.Register(request with { ConfirmDifferentPersonOnSamePhone = true }, Ct));
        Assert.Single(db.Patients);
        Assert.Equal(2, await db.PatientConsents.CountAsync());
        Assert.Equal(auditCount, await db.PatientAuditLogs.CountAsync());
    }

    [Theory]
    [InlineData("missing-age")]
    [InlineData("too-old")]
    [InlineData("future-dob")]
    [InlineData("old-dob")]
    [InlineData("guardian")]
    [InlineData("gender")]
    [InlineData("name")]
    [InlineData("phone")]
    [InlineData("consent")]
    [InlineData("wrong-purpose")]
    public async Task InvalidRegistration_HasNoPatientConsentOrAuditWrites(string scenario)
    {
        await using var db = MemoryDb();
        var today = new TimezoneService().GetClinicToday(null);
        var request = scenario switch
        {
            "missing-age" => Adult() with { ApproxAge = null },
            "too-old" => Adult() with { ApproxAge = 131 },
            "future-dob" => Adult() with { Dob = today.AddDays(1) },
            "old-dob" => Adult() with { Dob = today.AddYears(-130).AddDays(-1) },
            "guardian" => Adult() with { Dob = today.AddYears(-18).AddDays(1), ApproxAge = 30 },
            "gender" => Adult() with { Gender = "Prefer not to say" },
            "name" => Adult() with { Name = "  " },
            "phone" => Adult() with { Phone = "1234567890" },
            "consent" => Adult() with { Consent = new(false, "care_delivery") },
            _ => Adult() with { Consent = new(true, "whatsapp_opt_in") },
        };
        Assert.IsType<BadRequestObjectResult>(await Controller(db).Register(request, Ct));
        Assert.Empty(db.Patients);
        Assert.Empty(db.PatientConsents);
        Assert.Empty(db.PatientAuditLogs);
    }

    [Fact]
    public async Task MinorAndBirthdayBoundary_UsesDobAndSavesGuardianOnBothPurposes()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        var today = new TimezoneService().GetClinicToday(null);
        var child = Adult() with { Dob = today.AddYears(-18).AddDays(1), ApproxAge = 30,
            GuardianName = " Synthetic guardian ", GuardianRelationship = " Parent ", WhatsAppOptIn = true };
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.Register(child, Ct)).StatusCode);
        Assert.All(await db.PatientConsents.ToListAsync(), c => { Assert.Equal("Synthetic guardian", c.GuardianName); Assert.Equal("Parent", c.GuardianRelationship); });
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.Register(Adult() with { Phone = "9876543211", Dob = today.AddYears(-18), ApproxAge = null }, Ct)).StatusCode);
    }

    [Fact]
    public async Task ExistingPerson_ReturnsExistingChartWithoutInventingNewConsent()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        var request = Adult() with { Dob = new DateOnly(1996, 1, 1) };
        var original = Body(await controller.Register(request, Ct));
        var existing = Body(Assert.IsType<OkObjectResult>(await controller.Register(request with { IdempotencyKey = $"IDEMP-PAT-{Guid.NewGuid()}" }, Ct)));
        Assert.Equal(original.GetProperty("id").GetGuid(), existing.GetProperty("possibleDuplicateOf").GetGuid());
        Assert.Equal(original.GetProperty("uhid").GetString(), existing.GetProperty("uhid").GetString());
        Assert.Single(db.Patients);
        Assert.Single(db.PatientConsents);
    }

    [Fact]
    public async Task ExistingPerson_SharedNameAndPhone_MatchesTheCorrectRelativesDob()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        await controller.Register(Adult() with { Dob = new DateOnly(1976, 1, 1) }, Ct);
        var relative = Adult() with { Dob = new DateOnly(1996, 1, 1), ConfirmDifferentPersonOnSamePhone = true };
        var original = Body(await controller.Register(relative, Ct));

        var existing = Body(Assert.IsType<OkObjectResult>(await controller.Register(relative with { IdempotencyKey = $"IDEMP-PAT-{Guid.NewGuid()}" }, Ct)));
        Assert.Equal(original.GetProperty("patientId").GetGuid(), existing.GetProperty("possibleDuplicateOf").GetGuid());
        Assert.Equal(original.GetProperty("uhid").GetString(), existing.GetProperty("uhid").GetString());
        Assert.Equal(2, await db.Patients.CountAsync());
        Assert.Equal(2, await db.PatientConsents.CountAsync());
        Assert.Equal(2, await db.PatientAuditLogs.CountAsync());
    }

    [Fact]
    public async Task SharedPhone_RequiresConfirmation_AndConfirmedRequestReplays()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        await controller.Register(Adult(), Ct);
        var family = Adult() with { Name = "Synthetic relative" };
        var review = Body(Assert.IsType<OkObjectResult>(await controller.Register(family, Ct)));
        Assert.True(review.GetProperty("sharedPhoneDetected").GetBoolean());
        Assert.Single(db.Patients);
        Assert.Single(db.PatientConsents);
        Assert.Single(db.PatientAuditLogs);
        family = family with { ConfirmDifferentPersonOnSamePhone = true };
        var saved = Body(await controller.Register(family, Ct));
        var replay = Body(Assert.IsType<OkObjectResult>(await controller.Register(family, Ct)));
        Assert.Equal(saved.GetProperty("patientId").GetGuid(), replay.GetProperty("patientId").GetGuid());
        Assert.True(replay.GetProperty("idempotentReplay").GetBoolean());
        Assert.Equal(2, await db.Patients.CountAsync());
        Assert.Equal(2, await db.PatientConsents.CountAsync());
        Assert.Equal(2, await db.PatientAuditLogs.CountAsync());
    }

    [Fact]
    public async Task ListAndDetail_ReturnWithdrawnCareEvidenceSeparatelyFromActiveWhatsApp()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        await controller.Register(Adult() with { WhatsAppOptIn = true }, Ct);
        var patient = await db.Patients.SingleAsync();
        var care = await db.PatientConsents.SingleAsync(c => c.Purpose == "care_delivery");
        await controller.WithdrawConsent(patient.Id, care.Id, Ct);
        db.ChangeTracker.Clear();

        foreach (var body in new[] { Body(await controller.GetAll(Ct))[0], Body(await controller.GetById(patient.Id, Ct)) })
        {
            Assert.Equal(patient.Uhid, body.GetProperty("uhid").GetString());
            var evidence = body.GetProperty("consents").EnumerateArray().ToList();
            Assert.Equal(2, evidence.Count);
            Assert.Equal(care.WithdrawnAt, evidence.Single(c => c.GetProperty("purpose").GetString() == "care_delivery")
                .GetProperty("withdrawnAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, evidence.Single(c => c.GetProperty("purpose").GetString() == "whatsapp_opt_in")
                .GetProperty("withdrawnAt").ValueKind);
        }
    }

    [Fact]
    public async Task RegistrationAndPatch_AwaitIndexingOfPersistedDemographics()
    {
        await using var db = MemoryDb();
        var indexedNames = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new RecordingSearch(async patient =>
        {
            var persisted = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id);
            Assert.Equal(patient.Name, persisted.Name);
            indexedNames.Add(persisted.Name);
            started.TrySetResult();
            await release.Task;
        });
        var controller = new PatientsController(db, search);
        Caller(controller, Guid.NewGuid(), Guid.NewGuid(), "Receptionist");
        var registration = controller.Register(Adult(), Ct);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(registration.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var body = Body(await registration);
        await controller.Patch(body.GetProperty("patientId").GetGuid(), new("Corrected name", null, null, null, null, null), Ct);
        Assert.Equal(new[] { "Synthetic adult", "Corrected name" }, indexedNames);
    }

    [Fact]
    public async Task Patch_ApproximateAgeReplacesDob_AndEveryChangedFieldIsAudited()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        await controller.Register(Adult() with { Dob = new DateOnly(1996, 1, 1), ApproxAge = null, Address = "Old address" }, Ct);
        var patient = await db.Patients.SingleAsync();

        Assert.IsType<OkObjectResult>(await controller.Patch(patient.Id,
            new("Corrected name", "9876543211", null, 31, "female", ""), Ct));
        db.ChangeTracker.Clear();
        patient = await db.Patients.SingleAsync();
        Assert.Null(patient.Dob);
        Assert.False(patient.DobHasValue);
        Assert.Equal(31, patient.ApproxAge);
        Assert.Equal("", patient.Address);
        var logs = await db.PatientAuditLogs.Where(a => a.FieldName != "REGISTRATION_REQUEST_V1").ToListAsync();
        foreach (var field in new[] { "name", "phone", "dob", "dobHasValue", "approxAge", "gender", "address" })
            Assert.Single(logs, a => a.FieldName == field);
        Assert.Equal("1996-01-01", logs.Single(a => a.FieldName == "dob").OldValue);
        Assert.Equal("", logs.Single(a => a.FieldName == "dob").NewValue);
    }

    [Fact]
    public async Task Patch_FutureDobMakesNoDemographicOrAuditChanges()
    {
        await using var db = MemoryDb();
        var controller = Controller(db);
        await controller.Register(Adult(), Ct);
        var patient = await db.Patients.SingleAsync();
        var audits = await db.PatientAuditLogs.CountAsync();

        Assert.IsType<BadRequestObjectResult>(await controller.Patch(patient.Id,
            new("Changed name", null, new DateOnly(9999, 1, 1), null, null, null), Ct));
        db.ChangeTracker.Clear();
        Assert.Equal("Synthetic adult", (await db.Patients.SingleAsync()).Name);
        Assert.Equal(audits, await db.PatientAuditLogs.CountAsync());
    }

    private sealed class RecordingSearch(Func<Patient, Task> index) : FakePatientSearchService
    {
        public override Task IndexAsync(Patient patient, CancellationToken ct) => index(patient);
    }
}
