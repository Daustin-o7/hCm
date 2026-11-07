using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1PharmacySafetyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ExplicitSelection_DispensesOnlySelectedLine_AndAdvancesEncounterToken()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([Item(drug.Id), Item(null) with { GenericName = "Uncatalogued medicine" }]), Ct);
        var rx = await db.Prescriptions.SingleAsync();
        var selected = rx.Items.Single(i => i.DrugId == drug.Id);
        var other = rx.Items.Single(i => i.Id != selected.Id);
        var token = visit.UpdatedAt;

        var result = await Pharmacy(db, doctor.Id, tenant).DispensePrescription(
            new(rx.Id, IdempotencyKey: "selected-line", ItemSelections: [new(selected.Id, null, 2, null, null)]), Ct);

        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(2, selected.DispensedQuantity);
        Assert.False(selected.IsDispensed);
        Assert.Equal(0, other.DispensedQuantity);
        Assert.Equal(98, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
        Assert.NotEqual(token, visit.UpdatedAt);
        Assert.Single(db.DispenseItems);
        var line = await db.InvoiceLineItems.SingleAsync();
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(invoice.Total, line.GrossAmount(invoice.InvoiceType));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("invalid_batch")]
    [InlineData("excess_quantity")]
    public async Task InvalidSelection_HasNoTrackedOrPersistedSideEffects(string scenario)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
        var rx = await db.Prescriptions.SingleAsync();
        var item = Assert.Single(rx.Items);
        var selection = new DispenseItemSelection(scenario == "unknown" ? Guid.NewGuid() : item.Id,
            scenario == "invalid_batch" ? Guid.NewGuid() : null, scenario == "excess_quantity" ? 11 : 2, null, null);
        List<DispenseItemSelection> selections = scenario switch
        {
            "empty" => [], "duplicate" => [selection, selection], _ => [selection]
        };
        var token = visit.UpdatedAt;
        Assert.IsType<BadRequestObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(new(rx.Id, IdempotencyKey: "invalid-selection", ItemSelections: selections), Ct));

        Assert.Equal(0, item.DispensedQuantity);
        Assert.Null(item.SubstitutedDrugId);
        Assert.Equal(100, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
        Assert.Equal(token, visit.UpdatedAt);
        Assert.Empty(db.DispenseRecords);
        Assert.Empty(db.DrugStockMovements);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    [Fact]
    public async Task LaterLineFailure_DoesNotMutateEarlierSubstitutionOrStock()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var substitute = Stock(db, tenant);
        substitute.Name = "Substitute brand";
        await db.SaveChangesAsync(Ct);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([Item(drug.Id), Item(drug.Id) with { Frequency = "1-0-0" }]), Ct);
        var rx = await db.Prescriptions.SingleAsync();
        var items = rx.Items.ToList();
        Assert.IsType<BadRequestObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(new(rx.Id, IdempotencyKey: "later-line-failure",
            ItemSelections: [new(items[0].Id, null, 2, substitute.Id, "Same generic brand"), new(items[1].Id, Guid.NewGuid(), 2, null, null)]), Ct));
        Assert.All(items, i => { Assert.Equal(0, i.DispensedQuantity); Assert.Null(i.SubstitutedDrugId); });
        Assert.All(await db.DrugBatches.ToListAsync(Ct), b => Assert.Equal(100, b.QuantityRemaining));
        Assert.Empty(db.DispenseRecords);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    [Fact]
    public async Task SharedBatch_RequiresAggregateStockBeforeAnyMutation()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync();
        batch.QuantityRemaining = 3;
        await db.SaveChangesAsync(Ct);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id,
            Part1SaleReadinessTests.Finalize([Item(drug.Id), Item(drug.Id) with { Frequency = "1-0-0" }]), Ct);
        var rx = await db.Prescriptions.SingleAsync();
        Assert.IsType<BadRequestObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(new(rx.Id, IdempotencyKey: "shared-batch",
            ItemSelections: rx.Items.Select(i => new DispenseItemSelection(i.Id, batch.Id, 2, null, null)).ToList()), Ct));
        Assert.Equal(3, batch.QuantityRemaining);
        Assert.All(rx.Items, i => Assert.Equal(0, i.DispensedQuantity));
        Assert.Empty(db.Invoices);
        Assert.Empty(db.DrugStockMovements);
    }

    [Fact]
    public async Task ActiveAmendment_SuppliesOnlyRemainder_ThenLeavesPendingList()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var clinical = Consult(db, doctor.Id, tenant);
        await clinical.FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
        var original = await db.Consultations.SingleAsync();
        var oldRx = await db.Prescriptions.SingleAsync();
        var oldItem = Assert.Single(oldRx.Items);
        var pharmacy = Pharmacy(db, doctor.Id, tenant);
        var partialRequest = new DispensePrescriptionRequest(oldRx.Id, IdempotencyKey: "partial", ItemSelections: [new(oldItem.Id, null, 2, null, null)]);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await pharmacy.DispensePrescription(partialRequest, Ct)).StatusCode);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await clinical.AmendConsultation(original.Id, Amend(null), Ct)).StatusCode);
        var active = await db.Prescriptions.SingleAsync(p => p.Consultation.PreviousVersionId == original.Id);
        var pending = Json(Assert.IsType<OkObjectResult>(await pharmacy.GetPendingPrescriptions(ct: Ct)).Value);
        Assert.Equal(active.Id, Assert.Single(pending.EnumerateArray()).GetProperty("prescriptionId").GetGuid());
        Assert.IsType<ConflictObjectResult>(await pharmacy.GetPrescriptionForDispense(oldRx.Id, Ct));
        var detail = Json(Assert.IsType<OkObjectResult>(await pharmacy.GetPrescriptionForDispense(active.Id, Ct)).Value);
        Assert.Equal(8, detail.GetProperty("items")[0].GetProperty("remainingQuantity").GetInt32());
        Assert.Equal(201, Assert.IsType<ObjectResult>(await pharmacy.DispensePrescription(new(active.Id, IdempotencyKey: "remainder"), Ct)).StatusCode);
        Assert.Equal(90, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
        Assert.Equal(2, oldItem.DispensedQuantity);
        Assert.Equal(10, Assert.Single(active.Items).DispensedQuantity);
        Assert.True(Assert.Single(active.Items).IsDispensed);
        Assert.Equal(0, Json(Assert.IsType<OkObjectResult>(await pharmacy.GetPendingPrescriptions(ct: Ct)).Value).GetArrayLength());

        Assert.IsType<OkObjectResult>(await pharmacy.DispensePrescription(partialRequest, Ct));
        Assert.IsType<ConflictObjectResult>(await pharmacy.DispensePrescription(new(active.Id, IdempotencyKey: "partial"), Ct));
        Assert.Equal(2, await db.DispenseRecords.CountAsync(Ct));
        Assert.Equal(90, (await db.DrugBatches.SingleAsync()).QuantityRemaining);
    }
}
