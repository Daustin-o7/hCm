using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1PharmacyReceiptTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Checkout_ReplaysOriginalReceiptIncludingTenderAndLines_AfterRefundOrCatalogueChange()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        var request = new PosCheckoutRequest("Customer", "9876543210", null, null, null, null, null,
            "Cash", 30m, "IDEMP-INV-checkout-replay", [new(drug.Id, batch.Id, 2, batch.Mrp, drug.GstRate, drug.HsnCode)]);
        var first = Json(Assert.IsType<ObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(request, Ct)).Value);
        Assert.Equal(request.IdempotencyKey, first.GetProperty("idempotencyKey").GetString());
        Assert.Equal("Completed", first.GetProperty("paymentStatus").GetString());
        Assert.Equal("Confirmed", first.GetProperty("verificationStatus").GetString());
        Assert.Equal(30m, first.GetProperty("tenderedAmount").GetDecimal());
        Assert.Equal(7.60m, first.GetProperty("changeDue").GetDecimal());
        Assert.Equal(batch.Id, first.GetProperty("items")[0].GetProperty("drugBatchId").GetGuid());
        var invoice = await db.Invoices.SingleAsync(Ct);
        invoice.Status = InvoiceStatus.Refunded;
        invoice.PaidAmount = 0;
        invoice.RecalculateBalance();
        batch.Mrp = 50m;
        drug.GstRate = 5m;
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        var replay = Json(Assert.IsType<OkObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(request, Ct)).Value);
        Assert.True(JsonElement.DeepEquals(first, replay));
        Assert.Single(db.Invoices);
        Assert.Single(db.Payments);
        Assert.Equal(98, (await db.DrugBatches.SingleAsync(Ct)).QuantityRemaining);
        Assert.Equal(5, await db.FinanceLedgerEntries.CountAsync(Ct));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("phone")]
    [InlineData("address")]
    [InlineData("prescriber")]
    [InlineData("registration")]
    [InlineData("tender")]
    [InlineData("price")]
    [InlineData("gst")]
    [InlineData("reference")]
    public async Task Checkout_KeyCannotReplayADifferentPayload(string changed)
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        var request = new PosCheckoutRequest("Customer", "9876543210", null, null, "Address", "Doctor", "REG-1",
            "Cash", 20m, "IDEMP-INV-checkout-conflict", [new(drug.Id, batch.Id, 1, batch.Mrp, drug.GstRate, drug.HsnCode)]);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(request, Ct)).StatusCode);
        var different = changed switch
        {
            "customer" => request with { WalkInCustomerName = "Other customer" },
            "phone" => request with { WalkInCustomerPhone = "9876543211" },
            "address" => request with { PatientAddress = "Other address" },
            "prescriber" => request with { PrescriberName = "Other doctor" },
            "registration" => request with { PrescriberRegNo = "REG-2" },
            "tender" => request with { TenderedAmount = 30m },
            "price" => request with { Items = [request.Items[0] with { UnitPrice = 1m }] },
            "gst" => request with { Items = [request.Items[0] with { GstRate = 5m }] },
            _ => request with { PaymentReference = "OTHER-REFERENCE" }
        };
        Assert.IsType<ConflictObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(different, Ct));
        Assert.Single(db.Invoices);
        Assert.Single(db.Payments);
        Assert.Equal(99, batch.QuantityRemaining);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task PharmacyMutations_RequireAStableKey(string? key)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
        var rx = await db.Prescriptions.SingleAsync(Ct);
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(new("Customer", null, null,
            null, null, null, null, "Cash", 0m, key, [new(drug.Id, batch.Id, 1, batch.Mrp, drug.GstRate, drug.HsnCode)]), Ct));
        Assert.IsType<BadRequestObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(new(rx.Id, IdempotencyKey: key), Ct));
        Assert.Equal(100, batch.QuantityRemaining);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.DispenseRecords);
    }

    [Fact]
    public async Task Dispense_StoresNetTaxAndGrossConsistently_AndReplaysOriginalPartialOutcome()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        batch.Mrp = 0.07m;
        drug.GstRate = 12m;
        await db.SaveChangesAsync(Ct);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
        var rx = await db.Prescriptions.SingleAsync(Ct);
        var item = Assert.Single(rx.Items);
        var request = new DispensePrescriptionRequest(rx.Id, IdempotencyKey: "partial-receipt",
            ItemSelections: [new(item.Id, batch.Id, 2, null, null)]);
        var first = Json(Assert.IsType<ObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(request, Ct)).Value);
        var invoice = await db.Invoices.Include(i => i.LineItems).SingleAsync(Ct);
        var line = Assert.Single(invoice.LineItems);
        Assert.Equal(0.13m, line.Amount);
        Assert.Equal(0.01m, line.TaxAmount);
        Assert.Equal(invoice.Total, line.GrossAmount(invoice.InvoiceType));
        Assert.Equal(invoice.Subtotal, line.TaxableAmount(invoice.InvoiceType));
        var entries = await db.FinanceLedgerEntries.ToListAsync(Ct);
        Assert.Equal(entries.Sum(e => e.DebitAmount), entries.Sum(e => e.CreditAmount));
        Assert.Equal(2, first.GetProperty("items")[0].GetProperty("dispensedQuantity").GetInt32());
        Assert.Equal(8, first.GetProperty("items")[0].GetProperty("remainingQuantity").GetInt32());
        Assert.Equal("partial-receipt", first.GetProperty("idempotencyKey").GetString());
        Assert.Equal(201, Assert.IsType<ObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(
            new(rx.Id, IdempotencyKey: "rest-receipt"), Ct)).StatusCode);
        db.ChangeTracker.Clear();
        var replay = Json(Assert.IsType<OkObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(request, Ct)).Value);
        Assert.True(JsonElement.DeepEquals(first, replay));
        Assert.Equal(90, (await db.DrugBatches.SingleAsync(Ct)).QuantityRemaining);
        Assert.Equal(2, await db.DispenseRecords.CountAsync(Ct));
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("quantity")]
    [InlineData("batch")]
    [InlineData("patient")]
    [InlineData("reason")]
    public async Task Dispense_KeyCannotReplayADifferentSelectionOrMetadata(string changed)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
        var rx = await db.Prescriptions.SingleAsync(Ct);
        var selection = new DispenseItemSelection(Assert.Single(rx.Items).Id, null, 2, null, null);
        var request = new DispensePrescriptionRequest(rx.Id, PatientName: "Patient", IdempotencyKey: "dispense-conflict", ItemSelections: [selection]);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(request, Ct)).StatusCode);
        var different = changed switch
        {
            "omitted" => request with { ItemSelections = null },
            "quantity" => request with { ItemSelections = [selection with { Quantity = 3 }] },
            "batch" => request with { ItemSelections = [selection with { DrugBatchId = Guid.NewGuid() }] },
            "patient" => request with { PatientName = "Other patient" },
            _ => request with { ItemSelections = [selection with { SubstitutionReason = "Other reason" }] }
        };
        Assert.IsType<ConflictObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(different, Ct));
        Assert.Equal(98, (await db.DrugBatches.SingleAsync(Ct)).QuantityRemaining);
        Assert.Single(db.DispenseRecords);
        Assert.Single(db.Invoices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalCheckout_WithoutSavedRetryEvidenceFailsClosed(bool matchingHash)
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        var request = new PosCheckoutRequest("Customer", null, null, null, null, null, null, "Cash", 0m,
            "IDEMP-INV-historical", [new(drug.Id, batch.Id, 1, batch.Mrp, drug.GstRate, drug.HsnCode)]);
        db.Invoices.Add(new Invoice
        {
            TenantId = tenant, InvoiceType = InvoiceType.Pharmacy, IdempotencyKey = request.IdempotencyKey,
            CheckoutRequestHash = matchingHash ? PharmacyCommon.RequestHash(request) : null,
            Status = InvoiceStatus.Paid, Total = batch.Mrp, PaidAmount = batch.Mrp
        });
        await db.SaveChangesAsync(Ct);

        Assert.IsType<ConflictObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(request, Ct));
        Assert.Single(db.Invoices);
        Assert.Equal(100, batch.QuantityRemaining);
        Assert.Empty(db.Payments);
        Assert.Empty(db.DrugStockMovements);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalDispense_WithoutSavedRetryEvidenceFailsClosed(bool matchingHash)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
        var rx = await db.Prescriptions.SingleAsync(Ct);
        var request = new DispensePrescriptionRequest(rx.Id, IdempotencyKey: "historical-supply");
        db.DispenseRecords.Add(new DispenseRecord
        {
            TenantId = tenant, PrescriptionId = rx.Id, IdempotencyKey = request.IdempotencyKey,
            RequestHash = matchingHash ? PharmacyCommon.RequestHash(request) : null, DispensedBy = doctor.Id
        });
        await db.SaveChangesAsync(Ct);

        Assert.IsType<ConflictObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(request, Ct));
        Assert.Single(db.DispenseRecords);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.DrugStockMovements);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.Equal(100, (await db.DrugBatches.SingleAsync(Ct)).QuantityRemaining);
        Assert.Equal(0, Assert.Single(rx.Items).DispensedQuantity);
    }

    [Theory]
    [InlineData("UPI")]
    [InlineData("Card")]
    [InlineData("NetBanking")]
    public async Task ElectronicCheckout_RequiresActualReferenceAndExactTender(string method)
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        var request = new PosCheckoutRequest("Customer", null, null, null, null, null, null, method, 0m,
            "IDEMP-INV-electronic", [new(drug.Id, batch.Id, 1, batch.Mrp, drug.GstRate, drug.HsnCode)], "   ");
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(request, Ct));
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(
            request with { PaymentReference = "TERMINAL-1", TenderedAmount = batch.Mrp + 1m }, Ct));
        Assert.Empty(db.Invoices);
        Assert.Empty(db.Payments);
        Assert.Equal(100, batch.QuantityRemaining);

        var valid = request with { PaymentReference = " TERMINAL-1 ", TenderedAmount = batch.Mrp };
        var saved = Json(Assert.IsType<ObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(valid, Ct)).Value);
        Assert.Equal("TERMINAL-1", saved.GetProperty("paymentReference").GetString());
        Assert.Equal(method, saved.GetProperty("paymentMethod").GetString());
        Assert.Equal(batch.Mrp, saved.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(0m, saved.GetProperty("changeDue").GetDecimal());
        Assert.Equal("Completed", saved.GetProperty("paymentStatus").GetString());
        Assert.Equal("Confirmed", saved.GetProperty("verificationStatus").GetString());
        Assert.Contains(db.FinanceLedgerEntries, e => e.EntryType == FinanceEntryType.PaymentConfirmed && e.AccountCategory == "Bank" && e.DebitAmount == batch.Mrp);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 12)]
    [InlineData(true, 0)]
    [InlineData(true, 12)]
    public async Task PharmacyInvoice_ReadAndPrintResolveNetTaxAndGross(bool prescriptionSupply, int gstRate)
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        drug.GstRate = gstRate;
        await db.SaveChangesAsync(Ct);
        var batch = await db.DrugBatches.SingleAsync(Ct);
        if (prescriptionSupply)
        {
            await Consult(db, doctor.Id, tenant).FinalizeConsultation(visit.Id, Part1SaleReadinessTests.Finalize([Item(drug.Id)]), Ct);
            var rx = await db.Prescriptions.SingleAsync(Ct);
            Assert.Equal(201, Assert.IsType<ObjectResult>(await Pharmacy(db, doctor.Id, tenant).DispensePrescription(
                new(rx.Id, IdempotencyKey: "projection-supply"), Ct)).StatusCode);
        }
        else
            Assert.Equal(201, Assert.IsType<ObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(
                new("Customer", null, null, null, null, null, null, "Cash", 0m, "IDEMP-INV-projection",
                    [new(drug.Id, batch.Id, 2, batch.Mrp, drug.GstRate, drug.HsnCode)]), Ct)).StatusCode);

        var invoice = await db.Invoices.Include(i => i.LineItems).SingleAsync(Ct);
        var controller = new InvoicesController(db, Config, new RazorpayOrderClient(new HttpClient(), Config));
        Caller(controller, doctor.Id, tenant, "Doctor");
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim("clinicId", visit.ClinicId.ToString()));
        foreach (var outcome in new[] { await controller.GetInvoice(invoice.Id, Ct), await controller.PrintInvoice(invoice.Id, Ct) })
        {
            var saved = Json(Assert.IsType<OkObjectResult>(outcome).Value);
            var line = Assert.Single(saved.GetProperty("lineItems").EnumerateArray());
            Assert.Equal(invoice.Subtotal, line.GetProperty("taxableAmount").GetDecimal());
            Assert.Equal(invoice.GstAmount, line.GetProperty("taxAmount").GetDecimal());
            Assert.Equal(invoice.Total, line.GetProperty("totalAmount").GetDecimal());
            Assert.Equal(gstRate == 0 ? "Exempt" : "Taxable", line.GetProperty("taxCategory").GetString());
        }
        Assert.Equal(gstRate == 0 ? InvoiceDocumentType.BillOfSupply : InvoiceDocumentType.TaxInvoice, invoice.DocumentType);
    }
}
