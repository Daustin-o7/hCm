using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Hospital_CRM.Api.Services;

internal static class InvoiceAccounting
{
    internal static (List<InvoiceLineItem> Lines, object? Error) CalculateLines(List<InvoiceLineItemRequest>? items, TenantTaxProfile? profile)
    {
        if (items is not { Count: > 0 and <= 50 })
            return ([], new { error = "line_items_required", message = "Provide between 1 and 50 invoice lines." });
        var lines = new List<InvoiceLineItem>();
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Description) || item.Description.Trim().Length is < 2 or > 150)
                return ([], new { error = "invalid_line_description" });
            if (item.Quantity <= 0 || item.Quantity > 1000000)
                return ([], new { error = "invalid_line_quantity", description = item.Description });
            if (item.UnitPrice is null && (item.Amount <= 0 || decimal.Round(item.Amount, 2) != item.Amount)
                || item.UnitPrice is { } supplied && decimal.Round(supplied, 2) != supplied)
                return ([], new { error = "invalid_line_amount", description = item.Description });
            var price = item.UnitPrice ?? decimal.Round(item.Amount / item.Quantity, 2, MidpointRounding.AwayFromZero);
            if (price <= 0 || price > 1000000m || item.Quantity * price > 10000000m)
                return ([], new { error = "invalid_line_amount", description = item.Description });
            if (item.DiscountAmount < 0 || item.DiscountAmount > item.Quantity * price || decimal.Round(item.DiscountAmount, 2) != item.DiscountAmount)
                return ([], new { error = "invalid_line_discount", description = item.Description });

            var category = profile?.DefaultOpdTaxCategory ?? TaxCategory.Exempt;
            if (!string.IsNullOrWhiteSpace(item.TaxCategory)
                && (!Enum.TryParse(item.TaxCategory, true, out category) || !Enum.IsDefined(category)))
                return ([], new { error = "invalid_tax_category", description = item.Description });
            var hsn = string.IsNullOrWhiteSpace(item.HsnSacCode) ? profile?.DefaultOpdHsnSac ?? "999312" : item.HsnSacCode.Trim();
            if (string.IsNullOrWhiteSpace(hsn) || hsn.Length > 20)
                return ([], new { error = "invalid_hsn_sac", description = item.Description });
            if (category is TaxCategory.Exempt or TaxCategory.Nil && profile?.DefaultOpdTaxCategory == TaxCategory.Taxable
                && !hsn.StartsWith("9993", StringComparison.Ordinal))
                return ([], new { error = "tax_category_not_permitted", description = item.Description, message = "Exempt supplies must use a healthcare SAC (9993xx) for this clinic." });
            var rate = category is TaxCategory.Exempt or TaxCategory.Nil ? 0m : item.GstRate ?? profile?.DefaultOpdGstRate ?? 0m;
            if (rate < 0 || rate > 100 || decimal.Round(rate, 2) != rate || category == TaxCategory.Taxable && rate == 0)
                return ([], new { error = "taxable_line_requires_gst_rate", description = item.Description });
            var taxable = item.Quantity * price - item.DiscountAmount;
            var tax = decimal.Round(taxable * rate / 100m, 2, MidpointRounding.AwayFromZero);
            lines.Add(new InvoiceLineItem
            {
                Description = item.Description.Trim(),
                Quantity = item.Quantity,
                UnitPrice = price,
                DiscountAmount = item.DiscountAmount,
                TaxCategory = category,
                HsnSacCode = hsn,
                GstRate = rate,
                TaxAmount = tax,
                Amount = taxable + tax
            });
        }
        return (lines, null);
    }

    internal static bool CanCollect(Invoice invoice, decimal? amount) =>
        invoice.Status is InvoiceStatus.Issued or InvoiceStatus.Finalized or InvoiceStatus.PartiallyPaid
        && amount is > 0 && amount <= invoice.BalanceDue && decimal.Round(amount.Value, 2) == amount;

    internal static bool InvoiceRequestAgrees(Invoice invoice, GenerateInvoiceRequest request)
    {
        if (invoice.AppointmentId != request.AppointmentId || invoice.InvoiceType != InvoiceType.Consultation
            || invoice.DiscountAmount != request.DiscountAmount || invoice.DiscountReason != request.DiscountReason
            || request.LinkedPharmacyInvoiceId.HasValue && invoice.LinkedPharmacyInvoiceId != request.LinkedPharmacyInvoiceId
            || request.LineItems == null || request.LineItems.Count != invoice.LineItems.Count)
            return false;
        var remaining = invoice.LineItems.ToList();
        foreach (var item in request.LineItems)
        {
            if (item == null || item.Quantity <= 0) return false;
            var price = item.UnitPrice ?? decimal.Round(item.Amount / item.Quantity, 2, MidpointRounding.AwayFromZero);
            var match = remaining.FindIndex(line => line.Description == item.Description?.Trim() && line.Quantity == item.Quantity
                && line.UnitPrice == price && line.DiscountAmount == item.DiscountAmount
                && (string.IsNullOrWhiteSpace(item.TaxCategory) || string.Equals(line.TaxCategory.ToString(), item.TaxCategory, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(item.HsnSacCode) || line.HsnSacCode == item.HsnSacCode.Trim())
                && (!item.GstRate.HasValue || line.TaxCategory is TaxCategory.Exempt or TaxCategory.Nil || line.GstRate == item.GstRate));
            if (match < 0) return false;
            remaining.RemoveAt(match);
        }
        return true;
    }

    internal static List<string> InvoiceReferences(string reference) =>
        Regex.Matches(reference, @"(?<![\w-])[A-Z][A-Z0-9]*-\d{4}-\d{2}-\d{6}(?![\w-])", RegexOptions.IgnoreCase)
            .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static bool ReferenceAgrees(Invoice invoice, string? reference) =>
        string.IsNullOrWhiteSpace(reference) || InvoiceReferences(reference) is { Count: 1 } references
            && string.Equals(references[0], invoice.FormatInvoiceNumber(), StringComparison.OrdinalIgnoreCase);

    internal static Task<List<Payment>> FindRazorpayPaymentsAsync(HospitalCrmDbContext db, Guid? tenantId, PaymentEvent evt, CancellationToken ct) =>
        db.Payments.IgnoreQueryFilters()
            .Where(p => (!tenantId.HasValue || p.TenantId == tenantId.Value) && p.Method == PaymentMethod.Razorpay && p.Amount > 0
                && (p.RazorpayPaymentId == evt.ProviderPaymentId && evt.ProviderPaymentId != null
                    || p.RazorpayOrderId == evt.ProviderOrderId && evt.ProviderOrderId != null))
            .Take(2).ToListAsync(ct);

    internal static bool CaptureAgrees(Payment payment, PaymentEvent evt) => payment.Amount == evt.Amount
        && (payment.RazorpayPaymentId == null || payment.RazorpayPaymentId == evt.ProviderPaymentId)
        && (evt.ProviderOrderId == null || payment.RazorpayOrderId == evt.ProviderOrderId);

    internal static bool ReceiptAgrees(Payment payment, PaymentMethod method, decimal amount, string? utr, Guid? payeeId = null) =>
        payment.Amount > 0 && payment.Method == method && (amount == 0 || payment.Amount == amount)
        && (string.IsNullOrWhiteSpace(utr) || string.Equals(payment.Utr, utr.Trim(), StringComparison.Ordinal)
            || payment.Utr == null && payment.Status == PaymentStatus.Pending && payment.VerificationStatus == PaymentVerificationStatus.Reported)
        && (!payeeId.HasValue || payment.PayeeAccountId == payeeId
            || payment.PayeeAccountId == null && payment.Status == PaymentStatus.Pending && payment.VerificationStatus == PaymentVerificationStatus.Reported);

    internal static bool IsWebhookDuplicate(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505", ConstraintName: "IX_ProcessedWebhookEvents_TenantId_Provider_EventId" };

    internal static bool IsReceiptConflict(Exception ex)
    {
        // Npgsql's execution strategy can wrap transaction conflicts more than once.
        for (Exception? cause = ex; cause != null; cause = cause.InnerException)
            if (cause is DbUpdateConcurrencyException
                or Npgsql.PostgresException { SqlState: "40001" or "40P01" }
                or Npgsql.PostgresException { SqlState: "23505", ConstraintName: "IX_Payments_TenantId_Method_RazorpayPaymentId" })
                return true;
        return false;
    }

    internal static bool IsIdempotencyConflict(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505", ConstraintName: "IX_Payments_TenantId_IdempotencyKey" or "IX_Invoices_TenantId_IdempotencyKey" or "IX_PatientDeposits_TenantId_IdempotencyKey" };

    internal static bool IsNumberingConflict(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505", ConstraintName: "IX_Invoices_TenantId_FinancialYear_Series_InvoiceNumber"
            or "IX_PatientDeposits_TenantId_FinancialYear_SequenceNumber" or "IX_PatientDeposits_TenantId_ReceiptNumber"
        };

    internal static void PostFinalization(HospitalCrmDbContext db, Invoice invoice, Guid? userId)
    {
        var invoiceRef = invoice.FormatInvoiceNumber();
        var now = DateTimeOffset.UtcNow;
        db.FinanceLedgerEntries.AddRange(
            new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                EntryType = FinanceEntryType.InvoiceFinalized,
                ReferenceType = "Invoice",
                ReferenceId = invoice.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = "AccountsReceivable",
                DebitAmount = invoice.Total,
                Narration = $"Finalized invoice {invoiceRef}",
                PostedByUserId = userId,
                CreatedAt = now
            },
            new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                EntryType = FinanceEntryType.InvoiceFinalized,
                ReferenceType = "Invoice",
                ReferenceId = invoice.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = invoice.InvoiceType == InvoiceType.Pharmacy ? "PharmacySales" : "ConsultationRevenue",
                CreditAmount = invoice.Subtotal,
                Narration = $"Revenue recognized from invoice {invoiceRef}",
                PostedByUserId = userId,
                CreatedAt = now
            });
        if (invoice.GstAmount > 0)
            db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                EntryType = FinanceEntryType.InvoiceFinalized,
                ReferenceType = "Invoice",
                ReferenceId = invoice.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = "GstPayable",
                CreditAmount = invoice.GstAmount,
                Narration = $"GST payable on invoice {invoiceRef}",
                PostedByUserId = userId,
                CreatedAt = now
            });
        if (invoice.DiscountAmount > 0)
            db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                EntryType = FinanceEntryType.InvoiceFinalized,
                ReferenceType = "Invoice",
                ReferenceId = invoice.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = "DiscountAllowed",
                DebitAmount = invoice.DiscountAmount,
                Narration = $"Discount on invoice {invoiceRef}: {invoice.DiscountReason}",
                PostedByUserId = userId,
                CreatedAt = now
            });
    }

    internal static void ConfirmPayment(HospitalCrmDbContext db, Invoice invoice, Payment payment, Guid? userId)
    {
        if (payment.TenantId != invoice.TenantId || payment.InvoiceId != invoice.Id || !CanCollect(invoice, payment.Amount))
            throw new InvalidOperationException("A confirmed payment must belong to a payable invoice and fit its outstanding balance.");

        var now = DateTimeOffset.UtcNow;
        payment.Status = PaymentStatus.Completed;
        payment.VerificationStatus = PaymentVerificationStatus.Confirmed;
        payment.PaidAt = now;
        payment.ConfirmedAt = now;
        payment.ConfirmedByUserId = userId;
        invoice.PaidAmount += payment.Amount;
        invoice.RecalculateBalance();
        invoice.Status = invoice.BalanceDue <= 0 ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        invoice.UpdatedAt = now;

        // A payment must never build on an invoice whose own figures disagree — the legacy rows this
        // release repairs were marked Paid with PaidAmount = 0, and a drift like that hides real money.
        if (invoice.PaidAmount < 0 || invoice.PaidAmount + invoice.CreditedAmount > invoice.Total
            || invoice.BalanceDue != Math.Max(0m, invoice.Total - invoice.PaidAmount - invoice.CreditedAmount))
            throw new InvalidOperationException("Invoice payment state is inconsistent with its recorded amount.");

        var invoiceRef = invoice.FormatInvoiceNumber();
        db.FinanceLedgerEntries.AddRange(
            new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                EntryType = FinanceEntryType.PaymentConfirmed,
                ReferenceType = "Payment",
                ReferenceId = payment.Id,
                ReferenceNumber = payment.RazorpayPaymentId ?? payment.Utr ?? invoiceRef,
                AccountCategory = payment.Method == PaymentMethod.Cash ? "Cash" : "Bank",
                DebitAmount = payment.Amount,
                Narration = $"Payment confirmed for {invoiceRef} via {payment.Method}",
                PostedByUserId = userId,
                CreatedAt = now
            },
            new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                EntryType = FinanceEntryType.PaymentConfirmed,
                ReferenceType = "Invoice",
                ReferenceId = invoice.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = "AccountsReceivable",
                CreditAmount = payment.Amount,
                Narration = $"Receivable offset for {invoiceRef} from payment {payment.Id:N}",
                PostedByUserId = userId,
                CreatedAt = now
            });
    }
}
