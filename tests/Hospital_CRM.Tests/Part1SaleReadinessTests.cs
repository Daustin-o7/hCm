using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part1SaleReadinessTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly IConfiguration Config = new ConfigurationBuilder().Build();

    [Fact]
    public async Task ForeignDoctor_CannotCreateOrOverwriteDraft()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var foreign = Consult(db, Guid.NewGuid(), tenant);
        Assert.IsType<ForbidResult>(await foreign.SaveDraft(visit.Id, Draft("Foreign note"), Ct));
        Assert.Empty(db.Consultations);
        Assert.Empty(db.PatientAuditLogs);

        await Consult(db, doctor.Id, tenant).SaveDraft(visit.Id, Draft("Original note"), Ct);
        var auditCount = db.PatientAuditLogs.Count();
        Assert.IsType<ForbidResult>(await foreign.SaveDraft(visit.Id, Draft("Overwrite"), Ct));
        Assert.Equal("Original note", (await db.Consultations.SingleAsync()).ChiefComplaint);
        Assert.Equal(auditCount, db.PatientAuditLogs.Count());
    }

    [Fact]
    public async Task LegacyPrescription_CannotWriteAnotherDoctorsDraft()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var colleague = new User { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = doctor.ClinicId, Name = "Colleague", Email = "colleague@test.invalid", PasswordHash = "not-a-login", Role = UserRole.Doctor };
        db.Users.Add(colleague);
        await Consult(db, doctor.Id, tenant).SaveDraft(visit.Id, Draft("Original"), Ct);
        var draft = await db.Consultations.SingleAsync();
        var result = await Consult(db, colleague.Id, tenant).AddPrescription(draft.Id, new([new("Drug", "500mg", "1-0-1", "5 days")]), Ct);
        Assert.IsType<ForbidResult>(result);
        Assert.Empty(db.PrescriptionItems);
    }

    [Fact]
    public async Task Nurse_CannotWriteAnotherNursesAvailability()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var nurse = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenant, UserId = Guid.NewGuid(), Designation = "Staff Nurse" };
        db.Users.Add(new User { Id = nurse.UserId, TenantId = tenant, Role = UserRole.Nurse, Name = "Synthetic nurse", Email = "nurse@test.invalid", PasswordHash = "not-a-login" });
        db.NurseProfiles.Add(nurse);
        await db.SaveChangesAsync();
        var controller = new NurseController(db, NullLogger<NurseController>.Instance);
        Caller(controller, Guid.NewGuid(), tenant, "Nurse");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.IsType<ForbidResult>(await controller.SetAvailability(nurse.Id, new(today, today, ShiftPreference.Any, null, true, true, false, null), Ct));
        Assert.Empty(db.NurseAvailabilities);
    }

    [Fact]
    public async Task TreatingDoctor_CanSaveDraftButCannotTakeOverForeignAuthoredDraft()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        Assert.IsType<OkObjectResult>(await controller.SaveDraft(visit.Id, Draft("Original", [Item(null)]), Ct));
        Assert.Single(db.PrescriptionItems);
        var draft = await db.Consultations.SingleAsync();
        draft.DoctorId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var audits = db.PatientAuditLogs.Count();
        Assert.IsType<ForbidResult>(await controller.SaveDraft(visit.Id, Draft("Takeover", []), Ct));
        Assert.Equal("Original", draft.ChiefComplaint);
        Assert.Single(db.PrescriptionItems);
        Assert.Equal(audits, db.PatientAuditLogs.Count());
    }

    [Theory]
    [InlineData("Nurse")]
    [InlineData("ClinicAdmin")]
    public async Task NurseAvailability_OwnerAndSameClinicAdminAllowed(string role)
    {
        await using var db = MemoryDb();
        var (admin, _, _, tenant) = Seed(db);
        var nurseUser = new User { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = admin.ClinicId, Role = UserRole.Nurse, Name = "Synthetic nurse", Email = "nurse@test.invalid", PasswordHash = "not-a-login" };
        var nurse = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenant, UserId = nurseUser.Id, Designation = "Staff Nurse" };
        db.Users.Add(nurseUser);
        db.NurseProfiles.Add(nurse);
        await db.SaveChangesAsync();
        var controller = new NurseController(db, NullLogger<NurseController>.Instance);
        Caller(controller, role == "Nurse" ? nurseUser.Id : admin.Id, tenant, role);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.IsType<OkObjectResult>(await controller.SetAvailability(nurse.Id, new(today, today, ShiftPreference.Any, null, true, true, false, null), Ct));
        Assert.Equal(tenant, (await db.NurseAvailabilities.SingleAsync()).TenantId);
        Assert.IsType<BadRequestObjectResult>(await controller.SetAvailability(nurse.Id, new(today.AddDays(1), today, ShiftPreference.Any, null, true, true, false, null), Ct));
        Assert.Single(db.NurseAvailabilities);
    }

    [Theory]
    [InlineData("missing_caller")]
    [InlineData("missing_profile")]
    [InlineData("foreign_tenant")]
    [InlineData("foreign_clinic")]
    public async Task NurseAvailability_InvalidTargetsMakeNoWrites(string scenario)
    {
        await using var db = MemoryDb();
        var (admin, _, _, tenant) = Seed(db);
        var nurseUser = new User { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = scenario == "foreign_clinic" ? Guid.NewGuid() : admin.ClinicId, Role = UserRole.Nurse, Name = "Synthetic nurse", Email = "nurse@test.invalid", PasswordHash = "not-a-login" };
        var nurse = new NurseProfile { Id = Guid.NewGuid(), TenantId = scenario == "foreign_tenant" ? Guid.NewGuid() : tenant, UserId = nurseUser.Id, Designation = "Staff Nurse" };
        db.Users.Add(nurseUser);
        db.NurseProfiles.Add(nurse);
        await db.SaveChangesAsync();
        var controller = new NurseController(db, NullLogger<NurseController>.Instance);
        Caller(controller, admin.Id, tenant, "ClinicAdmin");
        if (scenario == "missing_caller") controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await controller.SetAvailability(scenario == "missing_profile" ? Guid.NewGuid() : nurse.Id, new(today, today, ShiftPreference.Any, null, true, true, false, null), Ct);
        Assert.True(result is UnauthorizedObjectResult or NotFoundObjectResult or ForbidResult);
        Assert.Empty(db.NurseAvailabilities);
    }

    [Fact]
    public async Task Finalize_ExplicitEmptyListClearsDraftMedicines()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        await controller.SaveDraft(visit.Id, Draft("Draft", [Item(null) with { ScheduleClass = "NDPS" }]), Ct);
        Assert.IsType<OkObjectResult>(await controller.FinalizeConsultation(visit.Id, Finalize([]), Ct));
        Assert.Empty(db.PrescriptionItems);
    }

    [Fact]
    public async Task Finalize_OmittedListValidatesStoredMedicines()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        await controller.SaveDraft(visit.Id, Draft("Draft", [Item(null) with { ScheduleClass = "NDPS" }]), Ct);
        var result = Assert.IsType<BadRequestObjectResult>(await controller.FinalizeConsultation(visit.Id, Finalize(null), Ct));
        Assert.Equal("ndps_digital_prescribing_prohibited", Json(result.Value).GetProperty("error").GetString());
        Assert.Equal(ConsultationStatus.Draft, (await db.Consultations.SingleAsync()).Status);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
    }

    [Fact]
    public async Task Amendment_RequiresPediatricWeight()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        patient.ApproxAge = 7;
        var controller = Consult(db, doctor.Id, tenant);
        await controller.FinalizeConsultation(visit.Id, Finalize([]), Ct);
        var original = await db.Consultations.SingleAsync();
        var result = Assert.IsType<BadRequestObjectResult>(await controller.AmendConsultation(original.Id, Amend([Item(null)]), Ct));
        Assert.Equal("pediatric_weight_required", Json(result.Value).GetProperty("error").GetString());
        Assert.Single(db.Consultations);
    }

    [Fact]
    public async Task Amendment_CatalogueNdpsCannotBeDownplayed()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        drug.ScheduleClass = ScheduleClass.NDPS;
        await db.SaveChangesAsync();
        var controller = Consult(db, doctor.Id, tenant);
        await controller.FinalizeConsultation(visit.Id, Finalize([]), Ct);
        var original = await db.Consultations.SingleAsync();
        var result = Assert.IsType<BadRequestObjectResult>(await controller.AmendConsultation(original.Id, Amend([Item(drug.Id) with { ScheduleClass = "General" }]), Ct));
        Assert.Equal("ndps_digital_prescribing_prohibited", Json(result.Value).GetProperty("error").GetString());
        Assert.Single(db.Consultations);
    }

    [Fact]
    public async Task Finalize_MissingCredentialsCannotInventRegistration()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        db.TenantMemberships.RemoveRange(db.TenantMemberships);
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Finalize([]), Ct));
        Assert.Empty(db.Consultations);
        Assert.Empty(db.PatientAuditLogs);
        Assert.Equal(AppointmentStatus.CheckedIn, visit.Status);
    }

    [Fact]
    public async Task Amendment_UsesCurrentSignersCredentials()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Finalize([]), Ct);
        var original = await db.Consultations.SingleAsync();
        var colleague = new User { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = doctor.ClinicId, Name = "Colleague", Email = "colleague@test.invalid", PasswordHash = "not-a-login", Role = UserRole.Doctor };
        db.Users.Add(colleague);
        db.TenantMemberships.Add(new TenantMembership { UserId = colleague.Id, TenantId = tenant, ClinicId = doctor.ClinicId, Role = UserRole.Doctor, RegistrationNumber = "COLLEAGUE-REG", Qualifications = "COLLEAGUE-QUAL" });
        await db.SaveChangesAsync();
        Assert.Equal(201, Assert.IsType<ObjectResult>(await Consult(db, colleague.Id, tenant).AmendConsultation(original.Id, Amend(null), Ct)).StatusCode);
        var amended = await db.Consultations.SingleAsync(c => c.PreviousVersionId == original.Id);
        Assert.Equal("COLLEAGUE-REG", amended.DoctorRegistrationNumber);
        Assert.Equal("COLLEAGUE-QUAL", amended.DoctorQualifications);
        Assert.Equal("SYNTHETIC-REG", original.DoctorRegistrationNumber);
    }

    [Theory]
    [InlineData("medicine")]
    [InlineData("strength")]
    [InlineData("frequency")]
    [InlineData("quantity")]
    [InlineData("notes")]
    [InlineData("credentials")]
    public async Task Verification_RejectsClinicalContentTampering(string field)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Consult(db, doctor.Id, tenant);
        await controller.FinalizeConsultation(visit.Id, Finalize([Item(null)]), Ct);
        var signed = await db.Consultations.SingleAsync();
        var item = await db.PrescriptionItems.SingleAsync();
        switch (field)
        {
            case "medicine": item.GenericName = "Different drug"; break;
            case "strength": item.Strength = "900mg"; break;
            case "frequency": item.Frequency = "1-1-1"; break;
            case "quantity": item.TotalQuantity = 999; break;
            case "notes": signed.Observations = "Changed observations"; break;
            case "credentials": signed.DoctorRegistrationNumber = "Changed registration"; break;
        }
        await db.SaveChangesAsync();
        var result = Assert.IsType<OkObjectResult>(await controller.VerifyPrescription(signed.VerificationQrCode!, Ct));
        Assert.False(Json(result.Value).GetProperty("signatureValid").GetBoolean());
        Assert.False(Json(result.Value).GetProperty("verified").GetBoolean());
        Assert.NotEqual("valid", Json(result.Value).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Amendment_PreservesDispensedQuantity(bool explicitList)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var controller = Consult(db, doctor.Id, tenant);
        await controller.FinalizeConsultation(visit.Id, Finalize([Item(drug.Id)]), Ct);
        var original = await db.Consultations.SingleAsync();
        var oldRx = await db.Prescriptions.SingleAsync();
        var item = await db.PrescriptionItems.SingleAsync();
        var pharmacy = Pharmacy(db, doctor.Id, tenant);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await pharmacy.DispensePrescription(new(oldRx.Id, ItemSelections: [new(item.Id, null, 2, null, null)]), Ct)).StatusCode);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.AmendConsultation(original.Id, Amend(explicitList ? [Item(drug.Id)] : null), Ct)).StatusCode);
        var currentRx = await db.Prescriptions.SingleAsync(p => p.Consultation.PreviousVersionId == original.Id);
        Assert.Equal(2, Assert.Single(currentRx.Items).DispensedQuantity);
        Assert.Equal(2, item.DispensedQuantity);
    }

    [Fact]
    public async Task Pharmacy_RejectsSupersededPrescriptionWithoutStockChanges()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var controller = Consult(db, doctor.Id, tenant);
        await controller.FinalizeConsultation(visit.Id, Finalize([Item(drug.Id)]), Ct);
        var original = await db.Consultations.SingleAsync();
        var oldRx = await db.Prescriptions.SingleAsync();
        await controller.AmendConsultation(original.Id, Amend(null), Ct);
        Assert.IsType<ConflictObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(new(oldRx.Id), Ct));
        Assert.Equal(100, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.DrugStockMovements);
    }

    [Fact]
    public async Task Pharmacy_PartialSupplyRemainsPending()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Finalize([Item(drug.Id)]), Ct);
        var rx = await db.Prescriptions.SingleAsync();
        var item = await db.PrescriptionItems.SingleAsync();
        var pharmacy = Pharmacy(db, doctor.Id, tenant);
        await pharmacy.DispensePrescription(new(rx.Id, ItemSelections: [new(item.Id, null, 2, null, null)]), Ct);
        var pending = Assert.IsType<OkObjectResult>(await pharmacy.GetPendingPrescriptions(ct: Ct));
        Assert.Equal(1, Json(pending.Value).GetArrayLength());
    }

    [Fact]
    public async Task PaymentMatching_UsesFullInvoiceReferenceNotNumericSubstring()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var one = Bill(tenant, 1, 100m);
        var eleven = Bill(tenant, 11, 100m);
        eleven.CreatedAt = one.CreatedAt.AddMinutes(-1);
        db.Invoices.AddRange(one, eleven);
        await db.SaveChangesAsync();
        Assert.Equal(eleven.Id, (await Match(db, tenant, new("event", 100m, null, "INV-2026-27-000011")))?.Id);
    }

    [Fact]
    public async Task ManualPaymentMatching_RejectsCancelledInvoice()
    {
        await using var db = MemoryDb();
        var tenant = Guid.NewGuid();
        var invoice = Bill(tenant, 1, 100m);
        invoice.Status = InvoiceStatus.Cancelled;
        var evt = new ProcessedWebhookEvent { Id = Guid.NewGuid(), TenantId = tenant, Provider = "paytm", EventId = "cancelled", Status = ProcessedWebhookEvent.Unmatched, Amount = 100m };
        db.Invoices.Add(invoice);
        db.ProcessedWebhookEvents.Add(evt);
        await db.SaveChangesAsync();
        var controller = Integrations(db);
        Caller(controller, Guid.NewGuid(), tenant, "ClinicAdmin");
        Assert.IsType<BadRequestObjectResult>(await controller.MatchUnmatched(evt.Id, new(invoice.Id), Ct));
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(ProcessedWebhookEvent.Unmatched, evt.Status);
    }

    [Fact]
    public async Task DraftInvoiceEdit_PreservesDiscountAndTaxInclusiveLine()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var invoice = Bill(tenant, 1, 0m);
        invoice.AppointmentId = visit.Id;
        invoice.Status = InvoiceStatus.Draft;
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var controller = new InvoicesController(db, Config, new RazorpayOrderClient(new HttpClient(), Config));
        Caller(controller, doctor.Id, tenant, "Doctor");
        Assert.IsType<OkObjectResult>(await controller.UpdateInvoiceDraft(invoice.Id, new(LineItems: [new("Taxable service", 1, 100m, DiscountAmount: 10m, HsnSacCode: "999312", TaxCategory: "Taxable", GstRate: 18m)]), Ct));
        var line = await db.InvoiceLineItems.SingleAsync();
        Assert.Equal(10m, line.DiscountAmount);
        Assert.Equal(106.20m, line.Amount);
        Assert.Equal(16.20m, line.TaxAmount);
        Assert.Equal(line.Amount, invoice.Total);
    }

    [Fact]
    public async Task Pos_PersistsLineTaxAndBalancedInvoiceAndPaymentJournal()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync();
        var controller = Pos(db, doctor.Id, tenant);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.CheckoutPos(new("Synthetic customer", null, null, null, null, null, null, "Cash", 22.40m, null, [new(drug.Id, batch.Id, 2, batch.Mrp, drug.GstRate, drug.HsnCode)]), Ct)).StatusCode);
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(invoice.GstAmount, (await db.InvoiceLineItems.SingleAsync()).TaxAmount);
        var entries = await db.FinanceLedgerEntries.ToListAsync();
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Sum(e => e.DebitAmount), entries.Sum(e => e.CreditAmount));
        Assert.Contains(entries, e => e.EntryType == FinanceEntryType.InvoiceFinalized);
        Assert.Contains(entries, e => e.EntryType == FinanceEntryType.PaymentConfirmed && e.AccountCategory == "Cash");
    }

    [Fact]
    public async Task Sync_InvalidRegistrationCannotBypassOnlineRules()
    {
        await using var db = MemoryDb();
        var controller = Sync(db, Guid.NewGuid(), Guid.NewGuid());
        var result = Assert.IsType<OkObjectResult>(await controller.Push(new([PushItem(1, new PatientSyncPayload("Patient", "not-a-phone", null, -1, "Male", null, null))]), Ct));
        Assert.Equal(0, Assert.IsType<SyncPushResponse>(result.Value).Synced);
        Assert.Empty(db.Patients);
        Assert.Empty(db.PatientConsents);
    }

    [Fact]
    public async Task Sync_PaidHeaderWithoutLinesIsRejected()
    {
        await using var db = MemoryDb();
        var result = Assert.IsType<OkObjectResult>(await Sync(db, Guid.NewGuid(), Guid.NewGuid()).Push(new([PushItem(2, new InvoiceSyncPayload(null, 100m, 18m, 118m, "Cash", "Walk-in", null, "Paid"))]), Ct));
        Assert.Equal(0, Assert.IsType<SyncPushResponse>(result.Value).Synced);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task PharmacyReturn_UnpaidInvoiceCannotRefundOrRestoreStock()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync();
        var invoice = Bill(tenant, 1, 22.40m);
        invoice.InvoiceType = InvoiceType.Pharmacy;
        invoice.LineItems.Add(new InvoiceLineItem { Id = Guid.NewGuid(), DrugBatchId = batch.Id, Description = drug.Name, Quantity = 2, UnitPrice = batch.Mrp, Amount = 20m, GstRate = 12m });
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).ProcessReturn(invoice.Id, new("Customer return", null, "RETURN-1"), Ct));
        Assert.Equal(100, batch.QuantityRemaining);
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Empty(db.DrugStockMovements);
    }

    internal static HospitalCrmDbContext MemoryDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    internal static (User Doctor, Patient Patient, Appointment Visit, Guid Tenant) Seed(HospitalCrmDbContext db)
    {
        var tenant = Guid.NewGuid();
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenant, Name = "Synthetic clinic", Address = "No real address", Phone = "9999999999", PharmacyLicenceNumber20 = "SYNTHETIC-ONLY", ActivatedModules = "core,precheck,pharmacy" };
        var doctor = new User { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = clinic.Id, Name = "Synthetic doctor", Email = $"{Guid.NewGuid():N}@test.invalid", Role = UserRole.Doctor, PasswordHash = "not-a-login" };
        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenant, Name = "Synthetic patient", Phone = "9999999999", ApproxAge = 30, CreatedBy = doctor.Id, CreatedAt = DateTimeOffset.UtcNow };
        var visit = new Appointment { Id = Guid.NewGuid(), TenantId = tenant, ClinicId = clinic.Id, DoctorId = doctor.Id, PatientId = patient.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow), TimeSlot = "09:00", Status = AppointmentStatus.CheckedIn, CreatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(new Tenant { Id = tenant, Name = clinic.Name, SubscriptionStatus = SubscriptionStatus.Active, ActivatedModules = "core,precheck,pharmacy" });
        db.Clinics.Add(clinic);
        db.Users.Add(doctor);
        db.Patients.Add(patient);
        db.Appointments.Add(visit);
        db.TenantMemberships.Add(new TenantMembership { UserId = doctor.Id, TenantId = tenant, ClinicId = clinic.Id, Role = UserRole.Doctor, RegistrationNumber = "SYNTHETIC-REG", Qualifications = "SYNTHETIC-QUAL" });
        db.SaveChanges();
        return (doctor, patient, visit, tenant);
    }
    internal static Drug Stock(HospitalCrmDbContext db, Guid tenant)
    {
        var drug = new Drug { Id = Guid.NewGuid(), TenantId = tenant, Name = "Synthetic drug 500mg", GenericName = "Synthetic drug", Strength = "500mg", DosageForm = "Tablet", GstRate = 12m, HsnCode = "3004" };
        drug.Batches.Add(new DrugBatch { Id = Guid.NewGuid(), TenantId = tenant, DrugId = drug.Id, BatchNumber = "SYNTHETIC", ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1), QuantityRemaining = 100, QuantityReceived = 100, Mrp = 11.20m, PurchaseRate = 5m });
        db.Drugs.Add(drug);
        db.SaveChanges();
        return drug;
    }
    internal static void Caller(ControllerBase c, Guid user, Guid tenant, string role) => c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString()), new Claim("tenant_id", tenant.ToString()), new Claim("role", role), new Claim(ClaimTypes.Role, role)], "TestAuth")) } };
    internal static ConsultationsController Consult(HospitalCrmDbContext db, Guid doctor, Guid tenant) { var c = new ConsultationsController(db); Caller(c, doctor, tenant, "Doctor"); return c; }
    internal static PharmacyComplianceController Pharmacy(HospitalCrmDbContext db, Guid user, Guid tenant) { var c = new PharmacyComplianceController(db, NullLogger<PharmacyComplianceController>.Instance); Caller(c, user, tenant, "Doctor"); return c; }
    internal static PharmacyPosController Pos(HospitalCrmDbContext db, Guid user, Guid tenant) { var c = new PharmacyPosController(db, NullLogger<PharmacyPosController>.Instance, new TimezoneService()); Caller(c, user, tenant, "ClinicAdmin"); return c; }
    internal static PaymentIntegrationsController Integrations(HospitalCrmDbContext db) => new(db, new TestSecretProtector(), Config, NullLogger<PaymentIntegrationsController>.Instance);
    internal static SyncController Sync(HospitalCrmDbContext db, Guid user, Guid tenant) { var c = new SyncController(db, new FakePatientSearchService(), NullLogger<SyncController>.Instance); Caller(c, user, tenant, "Receptionist"); return c; }
    internal static Task<Invoice?> Match(HospitalCrmDbContext db, Guid tenant, PaymentEvent evt) => (Task<Invoice?>)typeof(PaymentIntegrationsController).GetMethod("MatchInvoiceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Integrations(db), [tenant, evt, Ct])!;
    internal static StructuredPrescriptionItemRequest Item(Guid? drug) => new(drug, "Synthetic drug", null, "Tablet", "500mg", "Oral", "1-0-1", 5, "Days", "After food", 10, null, null, null);
    internal static SaveConsultationDraftRequest Draft(string note, List<StructuredPrescriptionItemRequest>? items = null) => new(note, null, "Synthetic diagnosis", null, null, null, null, null, null, null, null, items);
    internal static FinalizeConsultationRequest Finalize(List<StructuredPrescriptionItemRequest>? items) => new("Complaint", null, "Diagnosis", null, null, null, null, null, null, null, null, items, null);
    internal static AmendConsultationRequest Amend(List<StructuredPrescriptionItemRequest>? items) => new("Reasoned clinical amendment", null, null, null, null, null, null, null, null, null, null, null, items);
    internal static Invoice Bill(Guid tenant, int number, decimal total) => new() { Id = Guid.NewGuid(), TenantId = tenant, FinancialYear = "2026-27", Series = "INV", InvoiceNumber = number, Total = total, Subtotal = total, BalanceDue = total, Status = InvoiceStatus.Finalized, CreatedAt = DateTimeOffset.UtcNow };
    internal static SyncPushItem PushItem(int type, object payload) => new(Guid.NewGuid(), type, $"IDEMP-{(type == 1 ? "PAT" : "INV")}-{Guid.NewGuid()}", JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow);
    internal static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
