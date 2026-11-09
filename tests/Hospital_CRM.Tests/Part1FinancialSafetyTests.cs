using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class Part1FinancialSafetyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(0.001)]
    [InlineData(-1)]
    public async Task Advance_RejectsInvalidMoneyWithoutAllocating(decimal amount)
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var invoice = Bill(tenant, 1, 100m);
        invoice.AppointmentId = visit.Id;
        var deposit = new PatientDeposit { TenantId = tenant, PatientId = patient.Id, Amount = 100m, Balance = 100m };
        db.Invoices.Add(invoice);
        db.PatientDeposits.Add(deposit);
        await db.SaveChangesAsync();
        var controller = new PatientDepositsController(db);
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");

        Assert.IsType<BadRequestObjectResult>(await controller.ApplyAdvanceToInvoice(invoice.Id, new(amount), Ct));
        Assert.Equal(100m, deposit.Balance);
        Assert.Equal(0m, invoice.PaidAmount);
        Assert.Empty(db.Payments);
        Assert.Empty(db.PatientDepositAllocations);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Deposit_ReusedKeyWithDifferentMoneyIsConflict()
    {
        await using var db = MemoryDb();
        var (doctor, patient, _, tenant) = Seed(db);
        var controller = new PatientDepositsController(db);
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");
        var key = $"DEPOSIT-{Guid.NewGuid():N}";
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.CollectDeposit(patient.Id, new(50m, PaymentMethod.Cash, IdempotencyKey: key), Ct)).StatusCode);
        Assert.IsType<ConflictObjectResult>(await controller.CollectDeposit(patient.Id, new(75m, PaymentMethod.Cash, IdempotencyKey: key), Ct));
        Assert.Single(db.PatientDeposits);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Theory]
    [InlineData(PaymentMethod.Cash, null)]
    [InlineData(PaymentMethod.UPI, "  UTR-DEP-123  ")]
    public async Task Deposit_RetryReturnsSavedIdentityReferenceKeyAndTime(PaymentMethod method, string? reference)
    {
        await using var db = MemoryDb();
        var (doctor, patient, _, tenant) = Seed(db);
        var controller = new PatientDepositsController(db);
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");
        var request = new CreatePatientDepositRequest(50.25m, method, reference, IdempotencyKey: $"DEP-{Guid.NewGuid():N}");

        var first = JsonSerializer.SerializeToElement(Assert.IsType<ObjectResult>(await controller.CollectDeposit(patient.Id, request, Ct)).Value);
        db.ChangeTracker.Clear();
        var replay = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.CollectDeposit(patient.Id, request, Ct)).Value);

        foreach (var field in new[] { "depositId", "patientId", "receiptNumber", "financialYear", "sequenceNumber", "amount", "method", "referenceNumber", "idempotencyKey", "createdAt" })
            Assert.Equal(first.GetProperty(field).GetRawText(), replay.GetProperty(field).GetRawText());
        Assert.Equal(patient.Id, replay.GetProperty("patientId").GetGuid());
        Assert.Equal(request.IdempotencyKey, replay.GetProperty("idempotencyKey").GetString());
        Assert.Equal(reference?.Trim(), replay.GetProperty("referenceNumber").GetString());
        Assert.True(replay.GetProperty("idempotentReplay").GetBoolean());
        Assert.False(first.GetProperty("idempotentReplay").GetBoolean());
        Assert.Single(db.PatientDeposits);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Theory]
    [InlineData(PaymentMethod.UPI)]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.NetBanking)]
    public async Task Deposit_ElectronicReceiptRequiresActualReference(PaymentMethod method)
    {
        await using var db = MemoryDb();
        var (doctor, patient, _, tenant) = Seed(db);
        var controller = new PatientDepositsController(db);
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");

        Assert.IsType<BadRequestObjectResult>(await controller.CollectDeposit(patient.Id,
            new(50m, method, "   ", IdempotencyKey: $"DEP-{Guid.NewGuid():N}"), Ct));
        Assert.Empty(db.PatientDeposits);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Advance_RetryReturnsOriginalAllocationsAndDebitsLiabilityOnlyOnce()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var invoice = Bill(tenant, 1, 100m);
        invoice.AppointmentId = visit.Id;
        var deposit = new PatientDeposit { TenantId = tenant, PatientId = patient.Id, Amount = 200m, Balance = 200m };
        db.Invoices.Add(invoice);
        db.PatientDeposits.Add(deposit);
        await db.SaveChangesAsync();
        var controller = new PatientDepositsController(db);
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");
        var request = new ApplyAdvanceRequest(50m, deposit.Id, $"ADVANCE-{Guid.NewGuid():N}");

        var first = Assert.IsType<OkObjectResult>(await controller.ApplyAdvanceToInvoice(invoice.Id, request, Ct));
        var retry = Assert.IsType<OkObjectResult>(await controller.ApplyAdvanceToInvoice(invoice.Id, request, Ct));
        var firstJson = JsonSerializer.SerializeToElement(first.Value);
        var retryJson = JsonSerializer.SerializeToElement(retry.Value);
        Assert.Equal(firstJson.GetProperty("paymentId").GetGuid(), retryJson.GetProperty("paymentId").GetGuid());
        foreach (var field in new[] { "invoiceId", "amount", "method", "paymentStatus", "verificationStatus", "idempotencyKey", "confirmedAt" })
            Assert.Equal(firstJson.GetProperty(field).GetRawText(), retryJson.GetProperty(field).GetRawText());
        Assert.Equal(50m, retryJson.GetProperty("amount").GetDecimal());
        Assert.Equal("AdvanceDeposit", retryJson.GetProperty("method").GetString());
        Assert.Equal("completed", retryJson.GetProperty("paymentStatus").GetString());
        Assert.Equal("confirmed", retryJson.GetProperty("verificationStatus").GetString());
        Assert.Equal(request.IdempotencyKey, retryJson.GetProperty("idempotencyKey").GetString());
        Assert.NotEqual(default, retryJson.GetProperty("confirmedAt").GetDateTimeOffset());
        Assert.True(retryJson.GetProperty("idempotentReplay").GetBoolean());
        Assert.Single(retryJson.GetProperty("allocations").EnumerateArray());
        Assert.Equal(firstJson.GetProperty("allocations")[0].GetProperty("allocationId").GetGuid(),
            retryJson.GetProperty("allocations")[0].GetProperty("allocationId").GetGuid());
        Assert.Equal(150m, deposit.Balance);
        Assert.Equal(50m, invoice.PaidAmount);
        Assert.Single(db.Payments);
        Assert.Single(db.PatientDepositAllocations);
        var entries = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.AccountCategory == "PatientDeposits" && e.DebitAmount == 50m);
        Assert.Contains(entries, e => e.AccountCategory == "AccountsReceivable" && e.CreditAmount == 50m);
        Assert.DoesNotContain(entries, e => e.AccountCategory is "Bank" or "Cash");
        Assert.IsType<ConflictObjectResult>(await controller.ApplyAdvanceToInvoice(invoice.Id, request with { Amount = 75m }, Ct));
        Assert.Equal(150m, deposit.Balance);
    }

    [Fact]
    public async Task Deposit_CannotConfirmProviderOrAdvanceMoneyByAssertion()
    {
        await using var db = MemoryDb();
        var (doctor, patient, _, tenant) = Seed(db);
        var controller = new PatientDepositsController(db);
        Caller(controller, doctor.Id, tenant, "ClinicAdmin");
        foreach (var method in new[] { PaymentMethod.Razorpay, PaymentMethod.AdvanceDeposit, (PaymentMethod)999 })
            Assert.IsType<BadRequestObjectResult>(await controller.CollectDeposit(patient.Id, new(100m, method, IdempotencyKey: $"DEP-{Guid.NewGuid():N}"), Ct));
        Assert.Empty(db.PatientDeposits);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Draft_CreateAndEditUseIdenticalRoundingAndStoredDetail()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Invoices(db, doctor.Id, tenant);
        var lines = new List<InvoiceLineItemRequest>
        {
            new("Small taxable line", 0.10m, GstRate: 5m, TaxCategory: "Taxable"),
            new("Discounted line", 100.05m, 3, 33.35m, "Taxable", "999312", 18m, 10.05m)
        };
        Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.GenerateInvoice(new(visit.Id, lines), Ct)).StatusCode);
        var invoice = await db.Invoices.Include(i => i.LineItems).SingleAsync();
        var original = LineValues(invoice);

        Assert.IsType<OkObjectResult>(await controller.UpdateInvoiceDraft(invoice.Id, new(LineItems: lines.Select(i =>
            new UpdateInvoiceLineItemRequest(i.Description, i.Quantity, i.UnitPrice, i.Amount, i.DiscountAmount, i.HsnSacCode, i.TaxCategory, i.GstRate)).ToList()), Ct));

        Assert.Equal(original, LineValues(invoice));
        Assert.Equal(invoice.Total, invoice.LineItems.Sum(i => i.Amount));
        Assert.All(invoice.LineItems, i => Assert.Equal(i.Quantity * i.UnitPrice - i.DiscountAmount + i.TaxAmount, i.Amount));
    }

    [Fact]
    public async Task Draft_RejectedDiscountDoesNotStageReplacementLines()
    {
        await using var db = MemoryDb();
        var (doctor, _, visit, tenant) = Seed(db);
        var controller = Invoices(db, doctor.Id, tenant);
        await controller.GenerateInvoice(new(visit.Id, [new("Original consultation", 100m)]), Ct);
        var invoice = await db.Invoices.Include(i => i.LineItems).SingleAsync();
        var original = Assert.Single(invoice.LineItems).Id;

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateInvoiceDraft(invoice.Id,
            new(LineItems: [new("Replacement consultation", 1, 50m)], DiscountAmount: 500m, DiscountReason: "Invalid discount"), Ct));
        await db.SaveChangesAsync(Ct);
        Assert.Equal(original, (await db.InvoiceLineItems.SingleAsync()).Id);
        Assert.Equal(100m, invoice.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Pos_InvalidQuantityHasNoStockOrFinanceEffects(int quantity)
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync();
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(
            new("Customer", null, null, null, null, null, null, "Cash", 0m, "IDEMP-INV-invalid-quantity", [new(drug.Id, batch.Id, quantity, batch.Mrp, drug.GstRate, drug.HsnCode)]), Ct));
        Assert.Equal(100, batch.QuantityRemaining);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.Payments);
        Assert.Empty(db.DrugStockMovements);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Pos_DuplicateBatchCannotOversell()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync();
        var item = new PosCheckoutItem(drug.Id, batch.Id, 60, batch.Mrp, drug.GstRate, drug.HsnCode);
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(new("Customer", null, null, null, null, null, null, "Cash", 0m, "IDEMP-INV-duplicate-batch", [item, item]), Ct));
        Assert.Equal(100, batch.QuantityRemaining);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.DrugStockMovements);
    }

    [Theory]
    [InlineData("Razorpay")]
    [InlineData("AdvanceDeposit")]
    [InlineData("not-a-method")]
    public async Task Pos_CannotInventProviderOrAdvanceConfirmation(string method)
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var drug = Stock(db, tenant);
        var batch = await db.DrugBatches.SingleAsync();
        Assert.IsType<BadRequestObjectResult>(await Pos(db, doctor.Id, tenant).CheckoutPos(new("Customer", null, null, null, null, null, null, method, 0m, "IDEMP-INV-unsupported-method", [new(drug.Id, batch.Id, 1, batch.Mrp, drug.GstRate, drug.HsnCode)]), Ct));
        Assert.Equal(100, batch.QuantityRemaining);
        Assert.Empty(db.Payments);
    }

    [Fact]
    public async Task Sync_ConsentIsRequiredEvenForValidDemographics()
    {
        await using var db = MemoryDb();
        var result = Assert.IsType<OkObjectResult>(await Sync(db, Guid.NewGuid(), Guid.NewGuid()).Push(
            new([PushItem(1, new PatientSyncPayload("Valid adult", "9876543210", null, 30, "Male", null, null))]), Ct));
        Assert.Equal(0, Assert.IsType<SyncPushResponse>(result.Value).Synced);
        Assert.Empty(db.Patients);
        Assert.Empty(db.PatientConsents);
    }

    [Fact]
    public async Task Sync_ValidCashInvoiceUsesAuthoritativeLinesAndSafeReplay()
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var controller = Sync(db, doctor.Id, tenant);
        var item = PushItem(2, new
        {
            appointmentId = visit.Id, patientId = patient.Id,
            subtotal = 999m, gstAmount = 999m, total = 1998m,
            paymentMethod = "Cash", status = "Paid",
            lineItems = new[] { new InvoiceLineItemRequest("Offline consultation", 100m) }
        });
        var first = Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(await controller.Push(new([item]), Ct)).Value);
        Assert.Equal(1, first.Synced);
        var invoice = await db.Invoices.Include(i => i.LineItems).SingleAsync();
        Assert.Equal(100m, invoice.Total);
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Equal(0m, invoice.BalanceDue);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Single(invoice.LineItems);
        var payment = await db.Payments.SingleAsync();
        Assert.Equal(tenant, payment.TenantId);
        Assert.Equal(PaymentVerificationStatus.Confirmed, payment.VerificationStatus);
        var entries = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Equal(4, entries.Count);
        Assert.Equal(entries.Sum(e => e.DebitAmount), entries.Sum(e => e.CreditAmount));
        var replay = Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(await controller.Push(new([item]), Ct)).Value);
        Assert.Equal(1, replay.Synced);
        Assert.Single(db.Invoices);
        Assert.Single(db.Payments);
        Assert.Equal(4, await db.FinanceLedgerEntries.CountAsync());
    }

    [Theory]
    [InlineData("Razorpay")]
    [InlineData("UPI")]
    public async Task Sync_ProviderPaidAssertionIsRejected(string method)
    {
        await using var db = MemoryDb();
        var (doctor, patient, visit, tenant) = Seed(db);
        var result = Assert.IsType<SyncPushResponse>(Assert.IsType<OkObjectResult>(await Sync(db, doctor.Id, tenant).Push(new([PushItem(2, new
        {
            appointmentId = visit.Id, patientId = patient.Id, subtotal = 100m, gstAmount = 0m, total = 100m,
            paymentMethod = method, status = "Paid", lineItems = new[] { new InvoiceLineItemRequest("Consultation", 100m) }
        })]), Ct)).Value);
        Assert.Equal(0, result.Synced);
        Assert.Empty(db.Invoices);
        Assert.Empty(db.Payments);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Refund_ReportedButUnconfirmedReceiptIsNotRefundable()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var invoice = PaidBill(db, tenant, PaymentVerificationStatus.Reported);
        Assert.IsType<BadRequestObjectResult>(await Invoices(db, doctor.Id, tenant).ProcessRefund(invoice.Id, new(50m, PaymentMethod.Cash, "VOUCHER-1", "Recorded return", $"IDEMP-REF-{Guid.NewGuid()}", null, null, "Non-consideration refund test"), Ct));
        Assert.Single(db.Payments);
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Refund_PendingPositiveOrderDoesNotReserveRefundFunds()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var invoice = PaidBill(db, tenant);
        db.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Amount = 100m, Method = PaymentMethod.Razorpay, Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Pending });
        await db.SaveChangesAsync();
        Assert.Equal(201, Assert.IsType<ObjectResult>(await Invoices(db, doctor.Id, tenant).ProcessRefund(invoice.Id, new(50m, PaymentMethod.Cash, "VOUCHER-1", "Recorded return", $"IDEMP-REF-{Guid.NewGuid()}", null, null, "Non-consideration refund test"), Ct)).StatusCode);
        Assert.Equal(50m, invoice.PaidAmount);
        Assert.Equal(2, await db.FinanceLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Refund_ApprovalCannotTurnIncomingPaymentIntoPayout()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var invoice = PaidBill(db, tenant);
        var incoming = new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Amount = 50m, Method = PaymentMethod.Cash, Status = PaymentStatus.Pending, VerificationStatus = PaymentVerificationStatus.Pending, ConfirmedByUserId = Guid.NewGuid() };
        db.Payments.Add(incoming);
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Invoices(db, doctor.Id, tenant).ApproveRefund(invoice.Id, incoming.Id, null, Ct));
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, incoming.Status);
        Assert.Empty(db.FinanceLedgerEntries);
    }

    [Fact]
    public async Task Refund_MissingCapturedProviderIdCannotClaimCompletion()
    {
        await using var db = MemoryDb();
        var (doctor, _, _, tenant) = Seed(db);
        var invoice = PaidBill(db, tenant);
        (await db.Payments.SingleAsync()).Method = PaymentMethod.Razorpay;
        await db.SaveChangesAsync();
        var result = await Invoices(db, doctor.Id, tenant).ProcessRefund(invoice.Id, new(50m, PaymentMethod.Razorpay, null, "Requested provider refund", $"IDEMP-REF-{Guid.NewGuid()}", null, null, "Non-consideration refund test"), Ct);
        Assert.True(result is BadRequestObjectResult || result is ObjectResult { StatusCode: 503 });
        Assert.Equal(100m, invoice.PaidAmount);
        Assert.Empty(db.FinanceLedgerEntries);
        Assert.DoesNotContain(db.Payments, p => p.Amount < 0 && p.Status == PaymentStatus.Refunded);
    }

    private static InvoicesController Invoices(Hospital_CRM.Infrastructure.Data.HospitalCrmDbContext db, Guid user, Guid tenant)
    {
        var config = new ConfigurationBuilder().Build();
        var controller = new InvoicesController(db, config, new RazorpayOrderClient(new HttpClient(), config));
        Caller(controller, user, tenant, "ClinicAdmin");
        return controller;
    }

    private static string LineValues(Invoice invoice) => JsonSerializer.Serialize(invoice.LineItems.OrderBy(i => i.Description).Select(i =>
        new { i.Description, i.Quantity, i.UnitPrice, i.DiscountAmount, i.GstRate, i.TaxAmount, i.Amount }));

    private static Invoice PaidBill(Hospital_CRM.Infrastructure.Data.HospitalCrmDbContext db, Guid tenant, PaymentVerificationStatus verification = PaymentVerificationStatus.Confirmed)
    {
        var invoice = Bill(tenant, 1, 100m);
        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAmount = 100m;
        invoice.BalanceDue = 0m;
        invoice.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenant, InvoiceId = invoice.Id, Amount = 100m, Method = PaymentMethod.Cash, Status = PaymentStatus.Completed, VerificationStatus = verification });
        db.Invoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }
}
