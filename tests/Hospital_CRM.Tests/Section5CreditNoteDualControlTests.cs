using System.Security.Claims;
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
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// P2 gap: dual-control credit notes. Amounts at or above TenantTaxProfile.ThresholdCreditNoteApproval
/// need a second ClinicAdmin/Owner approval before the ledger moves; below it the old single-approver
/// behaviour is kept.
/// </summary>
public class Section5CreditNoteDualControlTests
{
    private static HospitalCrmDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase($"Sec5CnDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, TestMaintenanceTenantContext.Instance);

    private static ClaimsPrincipal Principal(Guid userId, Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", "ClinicAdmin"),
            new Claim("tenant_id", tenantId.ToString())
        }, "TestAuth"));

    private static InvoicesController ControllerFor(HospitalCrmDbContext db, Guid userId, Guid tenantId)
    {
        var config = new ConfigurationBuilder().Build();
        var controller = new InvoicesController(db, config, new RazorpayOrderClient(new HttpClient(), config));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, tenantId) }
        };
        return controller;
    }

    private static async Task<Invoice> SeedFinalizedInvoice(HospitalCrmDbContext db, Guid tenantId, decimal total = 2000m)
    {
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceNumber = 7,
            Series = "INV",
            Total = total,
            PaidAmount = 0m,
            BalanceDue = total,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    [Fact]
    public async Task CreditNoteAtOrAboveThreshold_IsPendingApproval_AndDoesNotMoveLedgerOrBalance()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        db.TenantTaxProfiles.Add(new TenantTaxProfile { TenantId = tenantId, ThresholdCreditNoteApproval = 1000m });
        var invoice = await SeedFinalizedInvoice(db, tenantId);
        var controller = ControllerFor(db, Guid.NewGuid(), tenantId);

        var result = await controller.CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(1500m, "Waiver"), CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(created.Value));
        Assert.True(doc.RootElement.GetProperty("requiresSecondApproval").GetBoolean());
        Assert.Equal("pendingapproval", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2000m, doc.RootElement.GetProperty("newBalanceDue").GetDecimal());

        Assert.Empty(await db.FinanceLedgerEntries.Where(e => e.ReferenceType == "CreditNote").ToListAsync());
        Assert.Equal(2000m, (await db.Invoices.SingleAsync(i => i.Id == invoice.Id)).BalanceDue);
    }

    [Fact]
    public async Task CreditNoteBelowThreshold_IsAutoApproved_PostsReversalAndReducesBalance()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var invoice = await SeedFinalizedInvoice(db, tenantId);
        var controller = ControllerFor(db, Guid.NewGuid(), tenantId);

        var result = await controller.CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(500m, "Waiver"), CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(created.Value));
        Assert.False(doc.RootElement.GetProperty("requiresSecondApproval").GetBoolean());
        Assert.Equal("approved", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1500m, doc.RootElement.GetProperty("newBalanceDue").GetDecimal());

        var creditNote = await db.CreditNotes.SingleAsync();
        Assert.Contains(await db.FinanceLedgerEntries.ToListAsync(),
            e => e.ReferenceType == "CreditNote" && e.DebitAmount == 500m && e.ReferenceId == creditNote.Id);
    }

    [Fact]
    public async Task PendingCreditNote_CannotBeSelfApproved_AndSecondApproverPostsReversal()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var firstApproverId = Guid.NewGuid();
        db.TenantTaxProfiles.Add(new TenantTaxProfile { TenantId = tenantId, ThresholdCreditNoteApproval = 10m });
        var invoice = await SeedFinalizedInvoice(db, tenantId);

        await ControllerFor(db, firstApproverId, tenantId)
            .CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(800m, "Waiver"), CancellationToken.None);
        var creditNote = await db.CreditNotes.SingleAsync();

        // Same user cannot supply the second approval.
        var selfApprove = await ControllerFor(db, firstApproverId, tenantId)
            .ApproveCreditNote(creditNote.Id, new ApproveCreditNoteRequest(), CancellationToken.None);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(selfApprove).StatusCode);
        Assert.Equal(CreditNoteStatus.PendingApproval, (await db.CreditNotes.SingleAsync()).Status);

        // A different admin can.
        var secondApproverId = Guid.NewGuid();
        var approved = await ControllerFor(db, secondApproverId, tenantId)
            .ApproveCreditNote(creditNote.Id, new ApproveCreditNoteRequest(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(approved);

        var stored = await db.CreditNotes.SingleAsync();
        Assert.Equal(CreditNoteStatus.Approved, stored.Status);
        Assert.Equal(secondApproverId, stored.SecondApprovedByUserId);
        Assert.Equal(1200m, (await db.Invoices.SingleAsync(i => i.Id == invoice.Id)).BalanceDue);

        var ledger = await db.FinanceLedgerEntries.ToListAsync();
        Assert.Contains(ledger, e => e.AccountCategory == "SalesReturnsAndAllowances" && e.DebitAmount == 800m);
        Assert.Contains(ledger, e => e.AccountCategory == "AccountsReceivable" && e.CreditAmount == 800m);
    }

    [Fact]
    public async Task PendingCreditNote_CannotBeSelfRejected_AndRejectedNoteCannotBeApprovedLater()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var firstApproverId = Guid.NewGuid();
        db.TenantTaxProfiles.Add(new TenantTaxProfile { TenantId = tenantId, ThresholdCreditNoteApproval = 10m });
        var invoice = await SeedFinalizedInvoice(db, tenantId);

        await ControllerFor(db, firstApproverId, tenantId)
            .CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(800m, "Waiver"), CancellationToken.None);
        var creditNote = await db.CreditNotes.SingleAsync();

        var selfReject = await ControllerFor(db, firstApproverId, tenantId)
            .RejectCreditNote(creditNote.Id, new RejectCreditNoteRequest(), CancellationToken.None);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(selfReject).StatusCode);

        var secondApproverId = Guid.NewGuid();
        var rejected = await ControllerFor(db, secondApproverId, tenantId)
            .RejectCreditNote(creditNote.Id, new RejectCreditNoteRequest(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(rejected);
        Assert.Equal(CreditNoteStatus.Rejected, (await db.CreditNotes.SingleAsync()).Status);

        var laterApprove = await ControllerFor(db, secondApproverId, tenantId)
            .ApproveCreditNote(creditNote.Id, new ApproveCreditNoteRequest(), CancellationToken.None);
        Assert.Equal(400, Assert.IsType<BadRequestObjectResult>(laterApprove).StatusCode);
        Assert.Empty(await db.FinanceLedgerEntries.ToListAsync());
        Assert.Equal(2000m, (await db.Invoices.SingleAsync(i => i.Id == invoice.Id)).BalanceDue);
    }

    [Fact]
    public async Task PaymentAfterCreditNote_UsesTheCredit_AndDoesNotRebillIt()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceNumber = 9,
            Series = "INV",
            Total = 1000m,
            PaidAmount = 400m,
            BalanceDue = 600m,
            Status = InvoiceStatus.PartiallyPaid
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        await ControllerFor(db, adminId, tenantId)
            .CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(300m, "Waiver"), CancellationToken.None);

        var afterCreditNote = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(300m, afterCreditNote.CreditedAmount);
        Assert.Equal(300m, afterCreditNote.BalanceDue);

        var payments = new PaymentsController(db);
        payments.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(adminId, tenantId) }
        };
        var res = await payments.ConfirmPayment(invoice.Id, new ConfirmPaymentRequest("cash", 300m), CancellationToken.None);
        Assert.IsType<OkObjectResult>(res);

        var settled = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(0m, settled.BalanceDue);
        Assert.Equal(InvoiceStatus.Paid, settled.Status);
        Assert.Equal(300m, settled.CreditedAmount);
    }

    [Fact]
    public async Task CreditNote_RemainsAppendOnly_ExceptForTheApprovalDecision()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var invoice = await SeedFinalizedInvoice(db, tenantId);
        await ControllerFor(db, Guid.NewGuid(), tenantId)
            .CreateCreditNote(invoice.Id, new CreateCreditNoteRequest(500m, "Waiver"), CancellationToken.None);

        var creditNote = await db.CreditNotes.SingleAsync();
        creditNote.Amount = 1m;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);
    }
}
