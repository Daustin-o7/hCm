using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1SyncSafetyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Registration_PreservesClientIdConsentMetadataAndStableReplay()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var controller = Sync(db, doctor.Id, tenant);
        var payload = new PatientSyncPayload("Synthetic child", "9876543210", null, 7, "Female", "Synthetic address",
            new(true, "direct_care"), true, "Synthetic parent", "Mother", "v2.0", "hi", "receptionist_assisted");
        var item = PushItem(1, payload);
        Assert.Equal("created", Assert.Single(await Push(controller, item)).Status);
        var patient = await db.Patients.SingleAsync(p => p.Id == item.Id);
        Assert.Equal(tenant, patient.TenantId);
        Assert.StartsWith("UHID-", patient.Uhid);
        Assert.Equal(doctor.Id, patient.CreatedBy);
        var consents = await db.PatientConsents.Where(c => c.PatientId == item.Id).ToListAsync();
        Assert.Equal(2, consents.Count);
        Assert.All(consents, c => { Assert.Equal(tenant, c.TenantId); Assert.Equal(doctor.Id, c.CapturedBy);
            Assert.Equal("v2.0", c.NoticeVersion); Assert.Equal("hi", c.Language); Assert.Equal("Synthetic parent", c.GuardianName); });

        var replay = Assert.Single(await Push(controller, item));
        Assert.Equal("idempotent_duplicate", replay.Status);
        Assert.Equal(item.Id, replay.Id);
        Assert.Equal(2, await db.Patients.CountAsync());
        Assert.Equal(2, await db.PatientConsents.CountAsync());
        Assert.Equal("conflict", Assert.Single(await Push(controller, item with
        { PayloadJson = JsonSerializer.Serialize(payload with { WhatsAppOptIn = false }) })).Status);
        Assert.Equal(2, await db.PatientConsents.CountAsync());
    }

    [Fact]
    public async Task Registration_FamilyPhoneRequiresReviewButLostResponseDoesNot()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var controller = Sync(db, doctor.Id, tenant);
        var first = PushItem(1, Adult("Parent", "9876543210"));
        Assert.Equal("created", Assert.Single(await Push(controller, first)).Status);
        var relative = PushItem(1, Adult("Relative", "9876543210"));
        Assert.Equal("duplicate_patient", Assert.Single(await Push(controller, relative)).Status);
        Assert.Equal(2, await db.Patients.CountAsync());
        Assert.Equal("created", Assert.Single(await Push(controller, relative with
        { PayloadJson = JsonSerializer.Serialize(Adult("Relative", "9876543210") with { ConfirmDifferentPersonOnSamePhone = true }) })).Status);
        Assert.Equal("idempotent_duplicate", Assert.Single(await Push(controller, first)).Status);
        Assert.Equal(3, await db.Patients.CountAsync());
    }

    [Theory]
    [InlineData("", "direct_care", 7)]
    [InlineData("Synthetic parent", "", 7)]
    [InlineData(null, "direct_care", -1)]
    public async Task Registration_InvalidGuardianPurposeOrAgeHasNoRows(string? guardian, string purpose, int age)
    {
        await using var db = MemoryDb();
        var item = PushItem(1, new PatientSyncPayload("Synthetic child", "9876543210", null, age, "Female", null,
            new(true, purpose), GuardianName: guardian, GuardianRelationship: "Mother"));
        Assert.Equal("invalid_payload", Assert.Single(await Push(Sync(db, Guid.NewGuid(), Guid.NewGuid()), item)).Status);
        Assert.Empty(db.Patients);
        Assert.Empty(db.PatientConsents);
    }

    [Fact]
    public async Task Sync_MixedBatchKeepsOutcomesAndRejectsClientIdReuse()
    {
        await using var db = MemoryDb();
        var controller = Sync(db, Guid.NewGuid(), Guid.NewGuid());
        var first = PushItem(1, Adult("First adult", "9876543210"));
        var results = await Push(controller, first, PushItem(1, Adult("Invalid adult", "invalid")),
            PushItem(1, Adult("Second adult", "9876543211")), first,
            PushItem(1, Adult("Different adult", "9876543212")) with { Id = first.Id });
        Assert.Equal(new[] { "created", "invalid_payload", "created", "idempotent_duplicate", "conflict" }, results.Select(r => r.Status));
        Assert.Equal(2, await db.Patients.CountAsync());
        Assert.Equal(2, await db.PatientConsents.CountAsync());
        Assert.Equal(2, (await db.Patients.ToListAsync()).Select(p => p.Uhid).Distinct().Count());
    }

    [Fact]
    public async Task Sync_NullBatchEntryIsRejectedBeforeAcknowledgedWrites()
    {
        await using var db = MemoryDb();
        Assert.IsType<BadRequestObjectResult>(await Sync(db, Guid.NewGuid(), Guid.NewGuid()).Push(
            new([PushItem(1, Adult("First adult", "9876543210")), null!]), Ct));
        Assert.Empty(db.Patients);
        Assert.Empty(db.PatientConsents);
    }

    [Fact]
    public async Task Sync_ZeroTotalCashBillRecordsWaiverNotInventedCashReceipt()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var controller = Sync(db, doctor.Id, tenant);
        var item = PushItem(2, new InvoiceSyncPayload(patient.Id, 999m, 999m, 999m, "Cash", null, null, "Paid",
            visit.Id, [new("Waived consultation", 100m)], 100m, "Full waiver"));
        Assert.Equal("created", Assert.Single(await Push(controller, item)).Status);
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(0m, invoice.Total);
        Assert.Equal(0m, invoice.PaidAmount);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Empty(db.Payments);
        var journal = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Equal(journal.Sum(e => e.DebitAmount), journal.Sum(e => e.CreditAmount));
        Assert.DoesNotContain(journal, e => e.AccountCategory is "Cash" or "Bank");
        Assert.Equal("idempotent_duplicate", Assert.Single(await Push(controller, item)).Status);
        Assert.Equal(journal.Count, await db.FinanceLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Sync_ReusedInvoiceKeyCannotTurnUnpaidBillIntoCashOrAlterLines()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var controller = Sync(db, doctor.Id, tenant);
        var payload = new InvoiceSyncPayload(patient.Id, 100m, 0m, 100m, "Cash", null, null, "Unpaid",
            visit.Id, [new("Consultation", 100m)]);
        var item = PushItem(2, payload);
        Assert.Equal("created", Assert.Single(await Push(controller, item)).Status);
        Assert.Equal("conflict", Assert.Single(await Push(controller, item with
        { PayloadJson = JsonSerializer.Serialize(payload with { Status = "Paid" }) })).Status);
        Assert.Equal("conflict", Assert.Single(await Push(controller, item with
        { PayloadJson = JsonSerializer.Serialize(payload with { LineItems = [new("Consultation", 200m)] }) })).Status);
        Assert.Empty(db.Payments);
        Assert.Equal(100m, (await db.Invoices.SingleAsync()).BalanceDue);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Invoice_ReplayUsesLineMultisetAndRejectsChangedRegimen()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var controller = new InvoicesController(db, config, new Hospital_CRM.Api.Services.RazorpayOrderClient(new HttpClient(), config));
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");
        var request = new GenerateInvoiceRequest(visit.Id, [new("First line", 50m), new("Second line", 75m)], $"IDEMP-INV-{Guid.NewGuid()}");
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.GenerateInvoice(request, Ct)).StatusCode);
        Assert.IsType<OkObjectResult>(await controller.GenerateInvoice(request with { LineItems = [request.LineItems[1], request.LineItems[0]] }, Ct));
        Assert.IsType<ConflictObjectResult>(await controller.GenerateInvoice(request with { LineItems = [request.LineItems[0], request.LineItems[0]] }, Ct));
        Assert.Single(db.Invoices);
        Assert.Equal(2, await db.InvoiceLineItems.CountAsync());
    }

    private static PatientSyncPayload Adult(string name, string phone) => new(name, phone, null, 30, "Male", null, new(true, "direct_care"));
    private static async Task<List<SyncItemResultDto>> Push(SyncController controller, params SyncPushItem[] items) =>
        Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(await controller.Push(new(items.ToList()), Ct)).Value).Results;
}
