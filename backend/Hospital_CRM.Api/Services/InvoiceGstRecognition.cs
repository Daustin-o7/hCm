using Hospital_CRM.Domain.Entities;

namespace Hospital_CRM.Api.Services;

/// <summary>
/// Invoice GST is recognized once, pro-rata to what was actually collected, and cumulatively:
/// a later period only recognizes the change since the previous one. Approved credit notes
/// reduce the recognized consideration, so tax reverses in the period the note is issued.
/// </summary>
public static class InvoiceGstRecognition
{
    /// <summary>
    /// GST recognized in [start, end). Receipts must be completed payments only: a refund
    /// without an approved credit note reverses no GST. Credit notes must be approved.
    /// Both need their invoice loaded.
    /// </summary>
    public static decimal RecognizedInWindow(
        IEnumerable<Payment> receiptsToDate,
        IEnumerable<CreditNote> approvedCreditNotesToDate,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        // A credit note can reverse tax in a period with no receipt, so scope is both sets.
        var invoices = receiptsToDate.Select(p => p.Invoice)
            .Concat(approvedCreditNotesToDate.Select(c => c.Invoice))
            .Where(i => i is { Total: > 0 })
            .DistinctBy(i => i.Id)
            .ToList();
        var receipts = receiptsToDate.ToLookup(p => p.InvoiceId);
        var credits = approvedCreditNotesToDate.ToLookup(c => c.InvoiceId);

        var recognized = 0m;
        foreach (var invoice in invoices)
            recognized += Cumulative(invoice, receipts[invoice.Id], credits[invoice.Id], end)
                        - Cumulative(invoice, receipts[invoice.Id], credits[invoice.Id], start);
        return recognized;
    }

    private static decimal Cumulative(
        Invoice invoice, IEnumerable<Payment> receipts, IEnumerable<CreditNote> credits, DateTimeOffset cutoff)
    {
        var collected = receipts.Where(p => p.PaidAt < cutoff).Sum(p => p.Amount);
        var credited = Math.Min(invoice.Total, credits.Where(c => c.IssuedAt < cutoff).Sum(c => c.Amount));
        var recognizedBase = Math.Clamp(Math.Min(collected, invoice.Total - credited), 0m, invoice.Total);
        return Allocate(invoice.GstAmount, invoice.Total, recognizedBase);
    }

    // Remainder lands on the period that completes collection; every period total stays exact.
    private static decimal Allocate(decimal gst, decimal invoiceTotal, decimal amount)
        => Math.Round(gst * amount / invoiceTotal, 2, MidpointRounding.AwayFromZero);
}
