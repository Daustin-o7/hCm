using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part2ClinicalContractsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    internal static ConsultationsController Scoped(HospitalCrmDbContext db, User doctor, Guid tenant, Guid? clinic = null)
        => Consult(db, doctor.Id, tenant, clinic ?? doctor.ClinicId);

    [Fact]
    public void LabDraft_PostgresModelUsesNullableTextArray()
    {
        using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseNpgsql("Host=localhost;Database=synthetic_model_only").Options, TestMaintenanceTenantContext.Instance);
        var entity = db.Model.FindEntityType(typeof(Consultation))!;
        var property = entity.FindProperty(nameof(Consultation.DraftLabTests))!;
        Assert.Equal("Consultations", entity.GetTableName());
        Assert.Equal("DraftLabTests", property.GetColumnName());
        Assert.Equal("text[]", property.GetColumnType());
        Assert.True(property.IsNullable);
    }

    [Fact]
    public async Task DraftRoundtrip_PreservesMetadataMedicinesAndDental_ThenExplicitlyClears()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        var request = Draft("", [Item(null)]) with { FollowUpDate = new DateOnly(2027, 1, 20), FollowUpNotes = "Return with results",
            OdontogramJson = "[{\"id\":11,\"status\":\"caries\"}]", IcdCode = "K02", IcdTitle = "Synthetic diagnosis",
            TemplateId = Guid.NewGuid(), TemplateVersion = 3, StructuredObservationsJson = "{\"notes\":\"synthetic\"}",
            OrderedLabTests = ["CBC", "சிறுநீர் பரிசோதனை"] };
        var saved = Json(Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, request, Ct)).Value);
        var draft = await db.Consultations.SingleAsync();
        Assert.Equal(0, draft.UpdatedAt!.Value.Ticks % 10);
        db.ChangeTracker.Clear();
        var detail = Json(Assert.IsType<OkObjectResult>(await controller.Get(draft.Id, Ct)).Value);
        Assert.Equal("2027-01-20", detail.GetProperty("followUpDate").GetString());
        Assert.Equal(request.OdontogramJson, detail.GetProperty("odontogramJson").GetString());
        Assert.Equal(request.TemplateId, detail.GetProperty("templateId").GetGuid());
        Assert.Equal(request.StructuredObservationsJson, detail.GetProperty("structuredObservationsJson").GetString());
        Assert.Equal(request.OrderedLabTests, detail.GetProperty("orderedLabTests").EnumerateArray().Select(t => t.GetString()));
        Assert.Empty(db.LabOrders);
        Assert.Single(detail.GetProperty("prescriptions")[0].GetProperty("items").EnumerateArray());
        var clear = Draft("", []) with { ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version,
            ExpectedUpdatedAt = saved.GetProperty("updatedAt").GetDateTimeOffset(), OdontogramJson = "[]", OrderedLabTests = [] };
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, clear, Ct));
        db.ChangeTracker.Clear();
        var reloaded = await db.Consultations.SingleAsync();
        Assert.Null(reloaded.FollowUpDate);
        Assert.Null(reloaded.FollowUpNotes);
        Assert.Null(reloaded.TemplateId);
        Assert.Equal("[]", reloaded.OdontogramJson);
        Assert.Empty(reloaded.DraftLabTests!);
        Assert.Empty(db.LabOrders);
        Assert.Empty(db.PrescriptionItems);
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("cleared")]
    [InlineData("replaced")]
    public async Task LabDraft_FinalizationConsumesSelectionsOnce_AndAmendmentPreservesOrderLink(string choice)
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id,
            Draft("Original", []) with { OrderedLabTests = [" CBC "] }, Ct));
        var draft = await db.Consultations.SingleAsync();
        db.ChangeTracker.Clear();
        // An older client that omits selections must not erase them during a notes-only save.
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Notes only", []) with {
            ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt }, Ct));
        draft = await db.Consultations.SingleAsync();
        Assert.Equal(new[] { "CBC" }, draft.DraftLabTests);
        Assert.Empty(db.LabOrders);
        Assert.IsType<ConflictObjectResult>(await controller.SaveDraft(visit.Id, Draft("Stale") with { OrderedLabTests = [] }, Ct));
        Assert.Equal(new[] { "CBC" }, draft.DraftLabTests);

        var request = Part1SaleReadinessTests.Finalize([]) with { ExpectedConsultationId = draft.Id,
            ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt,
            OrderedLabTests = choice == "stored" ? null : choice == "cleared" ? [] : ["CRP"] };
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, request, Ct));
        db.ChangeTracker.Clear();
        var signed = await db.Consultations.SingleAsync();
        Assert.Null(signed.DraftLabTests);
        var orders = await db.LabOrders.ToListAsync();
        if (choice == "cleared") Assert.Empty(orders);
        else
        {
            var order = Assert.Single(orders);
            Assert.Equal(choice == "stored" ? "CBC" : "CRP", order.TestName);
            Assert.Equal(signed.Id, order.ConsultationId);
            Assert.Equal(tenant, order.TenantId);
            Assert.Equal(patient.Id, order.PatientId);
            Assert.Equal(doctor.Id, order.DoctorId);
            Assert.Equal(LabOrderStatus.Ordered, order.Status);
        }
        var detail = Json(Assert.IsType<OkObjectResult>(await controller.Get(signed.Id, Ct)).Value);
        Assert.Equal(orders.Select(o => o.TestName), detail.GetProperty("orderedLabTests").EnumerateArray().Select(t => t.GetString()));
        Assert.IsType<ConflictObjectResult>(await controller.FinalizeConsultation(visit.Id, request, Ct));
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.AmendConsultation(signed.Id,
            Amend([]) with { Diagnosis = "Clinical correction" }, Ct)).StatusCode);
        db.ChangeTracker.Clear();
        var amended = await db.Consultations.SingleAsync(c => c.PreviousVersionId == signed.Id);
        Assert.Null(amended.DraftLabTests);
        Assert.Equal(orders.Count, await db.LabOrders.CountAsync());
        Assert.All(await db.LabOrders.ToListAsync(), o => Assert.Equal(signed.Id, o.ConsultationId));
        Assert.True(Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, amended.Id), signed.VerificationQrCode!)).Value)
            .GetProperty("signatureValid").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too long")]
    public async Task InvalidLabSelection_RejectsBeforeDraftOrFinalizationMutations(string? invalid)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Original", []) with { OrderedLabTests = ["CBC"] }, Ct));
        var draft = await db.Consultations.SingleAsync();
        var updatedAt = draft.UpdatedAt;
        var tests = new List<string> { invalid == "too long" ? new string('X', 201) : invalid! };
        var save = Assert.IsType<BadRequestObjectResult>(await controller.SaveDraft(visit.Id, Draft("Changed", []) with {
            ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = updatedAt, OrderedLabTests = tests }, Ct));
        Assert.Equal("invalid_lab_test", Json(save.Value).GetProperty("error").GetString());
        var finalize = Assert.IsType<BadRequestObjectResult>(await controller.FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([]) with { ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version,
                ExpectedUpdatedAt = updatedAt, OrderedLabTests = tests }, Ct));
        Assert.Equal("invalid_lab_test", Json(finalize.Value).GetProperty("error").GetString());
        Assert.Equal("Original", draft.ChiefComplaint);
        Assert.Equal(updatedAt, draft.UpdatedAt);
        Assert.Equal(new[] { "CBC" }, draft.DraftLabTests);
        Assert.Equal(ConsultationStatus.Draft, draft.Status);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
        Assert.Null(draft.SignedAt);
        Assert.Empty(db.LabOrders);
    }

    [Fact]
    public async Task MissingMedicineFields_RemainIncompleteInDraft_AndCannotBeFinalized()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        var item = Item(null) with { DosageForm = null, Route = null, Frequency = null, Duration = null,
            DurationUnit = null, Timing = null, TotalQuantity = null };
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Synthetic", [item]), Ct));
        var savedItem = await db.PrescriptionItems.SingleAsync();
        Assert.Equal(0, savedItem.TotalQuantity);
        Assert.Equal(0, savedItem.Duration);
        Assert.Equal("", savedItem.DosageForm);
        Assert.Equal("", savedItem.Route);
        Assert.Equal("", savedItem.Frequency);
        Assert.Equal("", savedItem.Timing);
        var draft = await db.Consultations.SingleAsync();
        var result = Assert.IsType<BadRequestObjectResult>(await controller.FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize(null) with { ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt }, Ct));
        Assert.Equal("invalid_prescription_item", Json(result.Value).GetProperty("error").GetString());
        Assert.Equal(ConsultationStatus.Draft, draft.Status);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
    }

    [Fact]
    public async Task FreeTextDuration_RoundTripsAndClearsInDraft_ButRequiresStructuredDurationToFinalize()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        var item = Item(null) with { Duration = null, DurationUnit = null, DurationText = "Until review" };
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Synthetic", [item]), Ct));
        var draft = await db.Consultations.SingleAsync();
        db.ChangeTracker.Clear();
        var detail = Json(Assert.IsType<OkObjectResult>(await controller.Get(draft.Id, Ct)).Value);
        Assert.Equal("Until review", detail.GetProperty("prescriptions")[0].GetProperty("items")[0].GetProperty("duration").GetString());
        var error = Assert.IsType<BadRequestObjectResult>(await controller.FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize(null) with { ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt }, Ct));
        Assert.Equal("invalid_prescription_item", Json(error.Value).GetProperty("error").GetString());
        var cleared = await controller.SaveDraft(visit.Id, Draft("Synthetic", [item with { DurationText = "" }]) with {
            ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt }, Ct);
        Assert.IsType<OkObjectResult>(cleared);
        db.ChangeTracker.Clear();
        Assert.Equal("", (await db.PrescriptionItems.SingleAsync()).DurationText);
        Assert.Equal(ConsultationStatus.Draft, (await db.Consultations.SingleAsync()).Status);
    }

    [Fact]
    public async Task ExistingDraft_RequiresCurrentRevisionForSaveAndFinalize()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("original"), Ct));
        var draft = await db.Consultations.SingleAsync();
        Assert.IsType<ConflictObjectResult>(await controller.SaveDraft(visit.Id, Draft("overwritten"), Ct));
        Assert.IsType<ConflictObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        var revision = Part1SaleReadinessTests.Finalize([]) with { ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt };
        Assert.IsType<ConflictObjectResult>(await controller.FinalizeConsultation(visit.Id, revision with { ExpectedUpdatedAt = draft.UpdatedAt!.Value.AddSeconds(-1) }, Ct));
        Assert.Equal("original", draft.ChiefComplaint);
        Assert.Equal(ConsultationStatus.Draft, draft.Status);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, revision, Ct));
        Assert.Equal(ConsultationStatus.Finalized, draft.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeWithoutMedicines_CompletesVisitWithoutInventingPrescription(bool savedEmptyDraft)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        visit.QueueTrackingExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var controller = Scoped(db, doctor, tenant);
        if (savedEmptyDraft) Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Synthetic", []), Ct));
        var draft = await db.Consultations.SingleOrDefaultAsync();
        var result = Json(Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([]) with { ExpectedConsultationId = draft?.Id, ExpectedVersion = draft?.Version, ExpectedUpdatedAt = draft?.UpdatedAt }, Ct)).Value);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("prescriptionId").ValueKind);
        Assert.Equal("finalized", result.GetProperty("status").GetString());
        Assert.Equal(AppointmentStatus.Completed, visit.Status);
        Assert.Equal(visit.UpdatedAt.AddHours(2), visit.QueueTrackingExpiresAt);
        Assert.Empty(db.PrescriptionItems);
        if (!savedEmptyDraft) Assert.Empty(db.Prescriptions);
    }

    [Fact]
    public async Task LegacySigning_ForwardsRevisionAndRetainsStoredMedicines()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("original", [Item(null)]), Ct));
        var draft = await db.Consultations.SingleAsync();
        var request = new CreateConsultationRequest("Complaint", null, "Diagnosis", null, draft.Id, draft.Version, draft.UpdatedAt);
        foreach (var stale in new[] { request with { ExpectedConsultationId = null }, request with { ExpectedVersion = null },
            request with { ExpectedUpdatedAt = null }, request with { ExpectedUpdatedAt = draft.UpdatedAt!.Value.AddSeconds(-1) } })
        {
            var conflict = Assert.IsType<ConflictObjectResult>(await controller.CreateConsultation(visit.Id, stale, Ct));
            Assert.Equal("consultation_changed_concurrently", Json(conflict.Value).GetProperty("error").GetString());
        }
        Assert.Equal("original", draft.ChiefComplaint);
        Assert.Equal(ConsultationStatus.Draft, draft.Status);
        var created = Assert.IsType<ObjectResult>(await controller.CreateConsultation(visit.Id, request, Ct));
        Assert.Equal(201, created.StatusCode);
        var result = Json(created.Value);
        Assert.Equal(draft.Id, result.GetProperty("consultationId").GetGuid());
        Assert.Equal(ConsultationStatus.Finalized, draft.Status);
        Assert.Equal(AppointmentStatus.Completed, visit.Status);
        Assert.Equal("Synthetic drug", (await db.PrescriptionItems.SingleAsync()).GenericName);
        Assert.True(Json(Assert.IsType<OkObjectResult>(await VerifyBound(controller, new(tenant, draft.Id), draft.VerificationQrCode!)).Value).GetProperty("verified").GetBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingDraft_InsertsNewMedicineRows_WhenSavingOrFinalizing(bool hasPrescription, bool finalize)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Synthetic", hasPrescription ? [Item(null)] : null), Ct));
        var draft = await db.Consultations.SingleAsync();
        db.ChangeTracker.Clear();
        var medicine = Item(null) with { Strength = "7mg" };
        var result = finalize
            ? await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([medicine]) with {
                ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt }, Ct)
            : await controller.SaveDraft(visit.Id, Draft("Synthetic", [medicine]) with {
                ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt }, Ct);
        Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal("7mg", (await db.PrescriptionItems.SingleAsync()).Strength);
        Assert.Single(db.Prescriptions);
        Assert.Equal(finalize ? ConsultationStatus.Finalized : ConsultationStatus.Draft, (await db.Consultations.SingleAsync()).Status);
    }

    [Fact]
    public async Task LegacyAppend_AdvancesRevisionAndRejectsOldAutosave()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        await controller.SaveDraft(visit.Id, Draft("original"), Ct);
        var draft = await db.Consultations.SingleAsync();
        draft.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var old = Draft("stale", []) with { ExpectedConsultationId = draft.Id, ExpectedVersion = draft.Version, ExpectedUpdatedAt = draft.UpdatedAt };
        Assert.IsType<ObjectResult>(await controller.AddPrescription(draft.Id, new([new("Synthetic", "5mg", "Daily", "5 Days")]), Ct));
        Assert.True(draft.UpdatedAt > old.ExpectedUpdatedAt);
        Assert.IsType<ConflictObjectResult>(await controller.SaveDraft(visit.Id, old, Ct));
        var item = Assert.Single(db.PrescriptionItems);
        Assert.Equal(0, item.Duration);
        Assert.Equal(0, item.TotalQuantity);
        Assert.Equal("5 Days", item.DurationText);
        Assert.Equal("", item.Timing);
    }

    [Theory]
    [InlineData("missing_claim")]
    [InlineData("other_clinic")]
    [InlineData("inactive")]
    public async Task SelectedMembership_DeniesWritesWithoutSideEffects(string scenario)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant, scenario == "other_clinic" ? Guid.NewGuid() : null);
        if (scenario == "missing_claim") ((ClaimsIdentity)controller.User.Identity!).RemoveClaim(controller.User.FindFirst("clinicId")!);
        if (scenario == "inactive") { (await db.TenantMemberships.SingleAsync()).Status = "suspended"; await db.SaveChangesAsync(); }
        Assert.IsType<ForbidResult>(await controller.SaveDraft(visit.Id, Draft("foreign"), Ct));
        Assert.IsType<ForbidResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        Assert.IsType<ForbidResult>(await controller.ClaimAppointment(visit.Id, Ct));
        Assert.IsType<ForbidResult>(await controller.ParkAppointment(visit.Id, Ct));
        Assert.IsType<ForbidResult>(await controller.RecallAppointment(visit.Id, Ct));
        Assert.IsType<ForbidResult>(await controller.GetPatientContextPanel(visit.PatientId, Ct));
        Assert.Empty(db.Consultations);
        Assert.Empty(db.Prescriptions);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
    }

    [Fact]
    public async Task SelectedClinic_ProtectsQueueActionsAndContext_EvenWithActiveMembership()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var otherTenant = new Tenant { Id = Guid.NewGuid(), Name = "Other synthetic tenant" };
        var other = new Clinic { Id = Guid.NewGuid(), TenantId = otherTenant.Id, Name = "Other synthetic clinic" };
        db.Tenants.Add(otherTenant);
        db.Clinics.Add(other);
        db.TenantMemberships.Add(new TenantMembership { UserId = doctor.Id, TenantId = otherTenant.Id, ClinicId = other.Id, Role = UserRole.Doctor });
        await db.SaveChangesAsync();
        var controller = Scoped(db, doctor, otherTenant.Id, other.Id);
        Assert.IsType<ForbidResult>(await controller.ClaimAppointment(visit.Id, Ct));
        Assert.IsType<ForbidResult>(await controller.ParkAppointment(visit.Id, Ct));
        Assert.IsType<ForbidResult>(await controller.RecallAppointment(visit.Id, Ct));
        Assert.IsType<NotFoundObjectResult>(await controller.GetPatientContextPanel(patient.Id, Ct));
        Assert.Empty(db.PatientAuditLogs);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
        var permitted = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await permitted.ClaimAppointment(visit.Id, Ct));
        Assert.IsType<OkObjectResult>(await permitted.ParkAppointment(visit.Id, Ct));
        Assert.IsType<OkObjectResult>(await permitted.RecallAppointment(visit.Id, Ct));
        Assert.Equal(AppointmentStatus.InConsultation, visit.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PatientContext_ExcludesAnotherClinicHistoryVitalsAndAllergies(bool crossLinkedTenant)
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var otherTenant = new Tenant { Id = Guid.NewGuid(), Name = "Other synthetic tenant" };
        var other = new Clinic { Id = Guid.NewGuid(), TenantId = otherTenant.Id, Name = "Other synthetic clinic" };
        db.Tenants.Add(otherTenant);
        db.Clinics.Add(other);
        // One clinic per tenant; deliberately cross-link rows to test clinic checks independently of tenant filtering.
        var rowTenant = crossLinkedTenant ? tenant : otherTenant.Id;
        var otherVisit = new Appointment { Id = Guid.NewGuid(), TenantId = rowTenant, ClinicId = other.Id, PatientId = patient.Id,
            DoctorId = doctor.Id, Date = visit.Date, Status = AppointmentStatus.Completed };
        db.Appointments.Add(otherVisit);
        db.Consultations.Add(new Consultation { Id = Guid.NewGuid(), TenantId = rowTenant, AppointmentId = otherVisit.Id,
            DoctorId = doctor.Id, Status = ConsultationStatus.Finalized, ChiefComplaint = "Foreign branch note" });
        db.PatientVitals.Add(new PatientVital { Id = Guid.NewGuid(), TenantId = rowTenant, AppointmentId = otherVisit.Id,
            PatientId = patient.Id, WeightKg = 70, Allergies = "Foreign vitals allergy" });
        db.PrecheckSubmissions.Add(new PrecheckSubmission { Id = Guid.NewGuid(), TenantId = rowTenant, AppointmentId = otherVisit.Id,
            TokenHash = new string('A', 64), Allergies = "Foreign precheck allergy" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var result = Json(Assert.IsType<OkObjectResult>(await Scoped(db, doctor, tenant).GetPatientContextPanel(patient.Id, Ct)).Value);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("lastConsultation").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("todayVitals").ValueKind);
        Assert.Empty(result.GetProperty("patient").GetProperty("allergies").EnumerateArray());
        Assert.Equal(0, result.GetProperty("totalCompletedVisits").GetInt32());
        Assert.Single(db.PatientAuditLogs);
    }

    [Fact]
    public async Task FollowUp_RejectsClinicClosureBeforeFinalizationOrLabOrders()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        db.ClinicHolidays.Add(new ClinicHoliday { Id = Guid.NewGuid(), ClinicId = visit.ClinicId, Name = "Synthetic closure", StartDate = date.ToString("yyyy-MM-dd"), EndDate = date.ToString("yyyy-MM-dd") });
        await db.SaveChangesAsync();
        var result = Assert.IsType<ConflictObjectResult>(await Scoped(db, doctor, tenant).FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([]) with { FollowUpDate = date, OrderedLabTests = ["Synthetic test"] }, Ct));
        Assert.Equal("follow_up_booking_unavailable", Json(result.Value).GetProperty("error").GetString());
        Assert.Empty(db.Consultations);
        Assert.Empty(db.LabOrders);
        Assert.Single(db.Appointments);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
    }

    [Fact]
    public async Task Amendment_ExplicitlyClearsClinicalFieldsWithoutChangingOriginal()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([]), Ct));
        var original = await db.Consultations.SingleAsync();
        // Synthetic legacy metadata; this test exercises replacement semantics, not seal verification.
        original.FollowUpDate = new DateOnly(2027, 1, 20);
        original.FollowUpNotes = "Old instruction";
        original.AdvisedInvestigations = "Old investigation";
        original.StructuredObservationsJson = "{\"old\":true}";
        original.OdontogramJson = "[]";
        original.TemplateId = Guid.NewGuid();
        original.TemplateVersion = 2;
        await db.SaveChangesAsync();
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.AmendConsultation(original.Id, Amend(null) with { ReplaceClinicalFields = true }, Ct)).StatusCode);
        var amended = await db.Consultations.SingleAsync(c => c.Version == 2);
        Assert.Null(amended.FollowUpDate);
        Assert.Null(amended.FollowUpNotes);
        Assert.Null(amended.AdvisedInvestigations);
        Assert.Null(amended.StructuredObservationsJson);
        Assert.Null(amended.OdontogramJson);
        Assert.Null(amended.TemplateId);
        Assert.Null(amended.TemplateVersion);
        Assert.NotNull(amended.UpdatedAt);
        Assert.Equal(0, amended.UpdatedAt.Value.Ticks % 10);
        Assert.Equal("Old instruction", original.FollowUpNotes);
        Assert.Equal("Old investigation", original.AdvisedInvestigations);
        Assert.Equal(original.Id, amended.PreviousVersionId);
    }

    [Fact]
    public async Task PublicVerification_WithoutTenantRoutingFailsClosed()
    {
        await using var db = MemoryDb();
        var result = Assert.IsType<ObjectResult>(await new ConsultationsController(db).VerifyPrescription("RX-123456789ABC", Ct));
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("verification_routing_unavailable", Json(result.Value).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("தமிழ்")]
    [InlineData("العربية")]
    [InlineData("Кириллица")]
    public async Task Pdf_UsesAppointmentClinic_AllLegacyRows_AndRejectsUnrenderableTextWithoutPrintLog(string unsupported)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Scoped(db, doctor, tenant);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(null)]), Ct));
        var consultation = await db.Consultations.SingleAsync();
        var legacy = new Prescription { Id = Guid.NewGuid(), TenantId = tenant, ConsultationId = consultation.Id };
        legacy.Items.Add(new PrescriptionItem { Id = Guid.NewGuid(), MedicineText = "Legacy second row", DosageText = "7mg", FrequencyText = "Nightly", DurationText = "3 Days", Frequency = "", Duration = 0 });
        db.Prescriptions.Add(legacy);
        doctor.ClinicId = Guid.NewGuid(); // Global default must not select the letterhead.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var file = Assert.IsType<FileContentResult>(await controller.GetConsultationPrescriptionPdf(consultation.Id, new PdfService(), Ct));
        var text = PdfTestReader.ReadText(file.FileContents);
        Assert.Contains("Synthetic clinic", text);
        Assert.Contains("Legacy second row", text);
        Assert.Contains("7mg", text);
        Assert.Contains("Nightly", text);
        Assert.Contains("3 Days", text);
        Assert.Contains("Synthetic drug", text);
        Assert.Single(db.PrescriptionPrintLogs);
        var rx = await db.Prescriptions.Include(p => p.Items).FirstAsync(p => p.Items.Any());
        rx.Items.First().SpecialInstructions = unsupported;
        await db.SaveChangesAsync();
        var error = Assert.IsType<UnprocessableEntityObjectResult>(await controller.GetPrescriptionPdf(rx.Id, new PdfService(), Ct));
        Assert.Equal("prescription_pdf_font_unsupported", Json(error.Value).GetProperty("error").GetString());
        Assert.Single(db.PrescriptionPrintLogs);
        var other = Scoped(db, doctor, tenant, Guid.NewGuid());
        Assert.IsType<ForbidResult>(await other.GetPrescriptionPdf(rx.Id, new PdfService(), Ct));
    }
}
