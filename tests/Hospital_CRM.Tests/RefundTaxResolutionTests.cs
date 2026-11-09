using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part1RefundSafetyTests;
using static Hospital_CRM.Tests.Part1SaleReadinessTests;

namespace Hospital_CRM.Tests;

public class RefundTaxResolutionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static string Key() => $"IDEMP-TAX-RES-{Guid.NewGuid()}";
    private static JsonElement Json(ObjectResult result) => JsonSerializer.SerializeToElement(result.Value);

    [Fact]
    public async Task ProcessRefund_MissingTaxResolution_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Customer requested cancellation",
            IdempotencyKey: Key(),
            RefundCreditNoteId: null,
            RefundNonConsiderationReason: null
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("refund_tax_resolution_required", error);
    }

    [Fact]
    public async Task ProcessRefund_ConflictingTaxResolution_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Customer requested cancellation",
            IdempotencyKey: Key(),
            RefundCreditNoteId: Guid.NewGuid(),
            RefundNonConsiderationReason: "Non-consideration reason provided alongside CN"
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("conflicting_refund_tax_resolution", error);
    }

    [Fact]
    public async Task ProcessRefund_NonConsiderationReasonIdenticalToOperationalReason_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Duplicate charge adjustment",
            IdempotencyKey: Key(),
            RefundCreditNoteId: null,
            RefundNonConsiderationReason: "Duplicate charge adjustment"
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("invalid_non_consideration_reason", error);
    }

    [Fact]
    public async Task ProcessRefund_NonConsiderationReasonExceeds500Chars_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Billing mistake correction",
            IdempotencyKey: Key(),
            RefundCreditNoteId: null,
            RefundNonConsiderationReason: new string('x', 501)
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("invalid_non_consideration_reason", error);
    }

    [Fact]
    public async Task ProcessRefund_NonExistentCreditNote_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Refund via credit note",
            IdempotencyKey: Key(),
            RefundCreditNoteId: Guid.NewGuid(),
            RefundNonConsiderationReason: null
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("invalid_refund_credit_note", error);
    }

    [Fact]
    public async Task ProcessRefund_CreditNoteBelongsToDifferentInvoice_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice1, _, tenant, user) = await Paid(db);
        var (invoice2, _, _, _) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var cn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            InvoiceId = invoice2.Id,
            CreditNoteNumber = "CN-2026-001",
            Amount = 50m,
            Reason = "Credit on other invoice",
            Status = CreditNoteStatus.Approved,
            ApprovedByUserId = user
        };
        db.CreditNotes.Add(cn);
        await db.SaveChangesAsync(Ct);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Refund via wrong credit note",
            IdempotencyKey: Key(),
            RefundCreditNoteId: cn.Id,
            RefundNonConsiderationReason: null
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice1.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("invalid_refund_credit_note", error);
    }

    [Fact]
    public async Task ProcessRefund_RejectedCreditNote_ReturnsBadRequest()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var cn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            InvoiceId = invoice.Id,
            CreditNoteNumber = "CN-2026-002",
            Amount = 50m,
            Reason = "Disallowed return",
            Status = CreditNoteStatus.Rejected,
            ApprovedByUserId = user
        };
        db.CreditNotes.Add(cn);
        await db.SaveChangesAsync(Ct);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Refund via rejected credit note",
            IdempotencyKey: Key(),
            RefundCreditNoteId: cn.Id,
            RefundNonConsiderationReason: null
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("credit_note_rejected", error);
    }

    [Fact]
    public async Task ProcessRefund_PendingApprovalCreditNote_ReturnsBadRequestWhenFinalizingCashRefund()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var cn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            InvoiceId = invoice.Id,
            CreditNoteNumber = "CN-2026-003",
            Amount = 50m,
            Reason = "Under managerial review",
            Status = CreditNoteStatus.PendingApproval,
            ApprovedByUserId = user
        };
        db.CreditNotes.Add(cn);
        await db.SaveChangesAsync(Ct);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-001",
            Reason: "Refund via pending credit note",
            IdempotencyKey: Key(),
            RefundCreditNoteId: cn.Id,
            RefundNonConsiderationReason: null
        );

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        var error = Json(result).GetProperty("error").GetString();
        Assert.Equal("credit_note_not_approved", error);
    }

    [Fact]
    public async Task ProcessRefund_ValidApprovedCreditNote_CreatesAndPersistsRefundCreditNoteId()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var cn = new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            InvoiceId = invoice.Id,
            CreditNoteNumber = "CN-2026-004",
            Amount = 50m,
            Reason = "Statutory pricing revision",
            Status = CreditNoteStatus.Approved,
            ApprovedByUserId = user
        };
        db.CreditNotes.Add(cn);
        await db.SaveChangesAsync(Ct);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-CN-1",
            Reason: "Statutory credit note refund",
            IdempotencyKey: Key(),
            RefundCreditNoteId: cn.Id,
            RefundNonConsiderationReason: null
        );

        var response = await controller.ProcessRefund(invoice.Id, request, Ct);
        var objResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(201, objResult.StatusCode);

        var refundPayment = await db.Payments.SingleAsync(p => p.InvoiceId == invoice.Id && p.Amount < 0);
        Assert.Equal(cn.Id, refundPayment.RefundCreditNoteId);
        Assert.Null(refundPayment.RefundNonConsiderationReason);
        Assert.Equal(PaymentStatus.Refunded, refundPayment.Status);
    }

    [Fact]
    public async Task ProcessRefund_ValidNonConsiderationReason_CreatesAndPersistsReason()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-NC-1",
            Reason: "Patient dissatisfied with waiting time",
            IdempotencyKey: Key(),
            RefundCreditNoteId: null,
            RefundNonConsiderationReason: "Commercial goodwill gesture - statutory tax not adjusted"
        );

        var response = await controller.ProcessRefund(invoice.Id, request, Ct);
        var objResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(201, objResult.StatusCode);

        var refundPayment = await db.Payments.SingleAsync(p => p.InvoiceId == invoice.Id && p.Amount < 0);
        Assert.Null(refundPayment.RefundCreditNoteId);
        Assert.Equal("Commercial goodwill gesture - statutory tax not adjusted", refundPayment.RefundNonConsiderationReason);
        Assert.Equal(PaymentStatus.Refunded, refundPayment.Status);
    }

    [Fact]
    public async Task ProcessRefund_ReplayWithChangedTaxResolution_ReturnsConflict()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var key = Key();
        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-REP-1",
            Reason: "Initial refund reason",
            IdempotencyKey: key,
            RefundCreditNoteId: null,
            RefundNonConsiderationReason: "Original non-consideration reason"
        );

        var response = await controller.ProcessRefund(invoice.Id, request, Ct);
        Assert.Equal(201, Assert.IsType<ObjectResult>(response).StatusCode);

        // Replay with identical payload returns OK (idempotent)
        var replay = Assert.IsType<OkObjectResult>(await controller.ProcessRefund(invoice.Id, request, Ct));
        Assert.Equal("refunded", Json(replay).GetProperty("status").GetString());

        // Replay with changed non-consideration reason returns Conflict
        var conflictingRequest = request with { RefundNonConsiderationReason = "Altered tax resolution reason" };
        var conflictResult = Assert.IsType<ConflictObjectResult>(await controller.ProcessRefund(invoice.Id, conflictingRequest, Ct));
        Assert.Equal("refund_key_conflict", Json(conflictResult).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProcessRefund_ReplayOmittingTaxResolution_ReturnsSavedRefund()
    {
        await using var db = MemoryDb();
        var (invoice, _, tenant, user) = await Paid(db);
        var controller = Controller(db, tenant, user);

        var key = Key();
        var request = new CreateRefundRequest(
            Amount: 50m,
            Method: PaymentMethod.Cash,
            RefundReference: "VOUCHER-POLL-1",
            Reason: "Initial refund with tax resolution",
            IdempotencyKey: key,
            RefundCreditNoteId: null,
            RefundNonConsiderationReason: "Original non-consideration reason"
        );

        var response = await controller.ProcessRefund(invoice.Id, request, Ct);
        Assert.Equal(201, Assert.IsType<ObjectResult>(response).StatusCode);

        // Status poll or retry without resending tax resolution fields should succeed and return saved refund
        var pollRequest = request with { RefundCreditNoteId = null, RefundNonConsiderationReason = null };
        var replay = Assert.IsType<OkObjectResult>(await controller.ProcessRefund(invoice.Id, pollRequest, Ct));
        Assert.Equal("refunded", Json(replay).GetProperty("status").GetString());
    }
}

