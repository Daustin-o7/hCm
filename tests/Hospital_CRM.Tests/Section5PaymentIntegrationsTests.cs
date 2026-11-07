using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
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

/// <summary>
/// The PSP webhook, the device bridge and the manual "match" queue all close an invoice through
/// one helper. Money that arrives through them must land on the invoice and in the ledger exactly
/// like a staff-confirmed payment.
/// </summary>
public class Section5PaymentIntegrationsTests
{
    private static HospitalCrmDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase($"Sec5Integrations_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, TestMaintenanceTenantContext.Instance);

    private static PaymentIntegrationsController ControllerFor(HospitalCrmDbContext db, Guid tenantId, Guid userId)
    {
        var controller = new PaymentIntegrationsController(
            db, new TestSecretProtector(), new ConfigurationBuilder().Build(),
            NullLogger<PaymentIntegrationsController>.Instance);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, "ClinicAdmin")
        }, "TestAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task MatchedWebhookPayment_UpdatesInvoiceBalanceAndPostsLedger()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FinancialYear = "2026-27",
            Series = "INV",
            InvoiceNumber = 11,
            Status = InvoiceStatus.Finalized,
            Subtotal = 1000m,
            Total = 1000m,
            PaidAmount = 0m,
            BalanceDue = 1000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Invoices.Add(invoice);

        var evt = new ProcessedWebhookEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Provider = "paytm",
            EventId = "evt-1",
            Status = ProcessedWebhookEvent.Unmatched,
            Amount = 400m,
            Utr = "UTR-MATCH-1"
        };
        db.ProcessedWebhookEvents.Add(evt);
        await db.SaveChangesAsync();

        var controller = ControllerFor(db, tenantId, adminId);
        var res = await controller.MatchUnmatched(evt.Id, new MatchUnmatchedRequest(invoice.Id), CancellationToken.None);
        Assert.IsType<OkObjectResult>(res);

        var updated = await db.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updated);
        Assert.Equal(400m, updated.PaidAmount);
        Assert.Equal(600m, updated.BalanceDue);
        Assert.Equal(InvoiceStatus.PartiallyPaid, updated.Status);

        var payment = await db.Payments.SingleAsync(p => p.InvoiceId == invoice.Id);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(PaymentVerificationStatus.Confirmed, payment.VerificationStatus);

        var ledger = await db.FinanceLedgerEntries
            .Where(e => e.EntryType == FinanceEntryType.PaymentConfirmed)
            .ToListAsync();
        Assert.Equal(2, ledger.Count);
        Assert.Contains(ledger, e => e.AccountCategory == "Bank" && e.DebitAmount == 400m);
        Assert.Contains(ledger, e => e.AccountCategory == "AccountsReceivable" && e.CreditAmount == 400m);
    }

    [Fact]
    public async Task MatchedWebhookPayment_OnFullyPaidInvoice_IsRejected()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceNumber = 12,
            Series = "INV",
            Status = InvoiceStatus.Paid,
            Total = 500m,
            PaidAmount = 500m,
            BalanceDue = 0m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Invoices.Add(invoice);
        var evt = new ProcessedWebhookEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Provider = "paytm",
            EventId = "evt-2",
            Status = ProcessedWebhookEvent.Unmatched,
            Amount = 500m,
            Utr = "UTR-MATCH-2"
        };
        db.ProcessedWebhookEvents.Add(evt);
        await db.SaveChangesAsync();

        var res = await ControllerFor(db, tenantId, Guid.NewGuid())
            .MatchUnmatched(evt.Id, new MatchUnmatchedRequest(invoice.Id), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(res);
        Assert.Empty(await db.Payments.ToListAsync());
    }
}
