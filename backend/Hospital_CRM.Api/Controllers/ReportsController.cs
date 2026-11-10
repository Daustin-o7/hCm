using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/reports")]
[AuthorizeRoles("ClinicAdmin")]
public class ReportsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ITimezoneService _timezone;
    private const string AccountingTimezone = "Asia/Kolkata";

    public ReportsController(HospitalCrmDbContext db, ITimezoneService? timezone = null)
    {
        _db = db;
        _timezone = timezone ?? new TimezoneService();
    }

    // ----- FR-11: Financial Analytics & Tax Audit Reports -----

    [HttpGet("financial")]
    public async Task<IActionResult> FinancialSummary([FromQuery] string? month, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var endDate = startDate.AddMonths(1);
        var startDt = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var endDt = _timezone.GetStartOfDayUtc(endDate, AccountingTimezone);

        var clinicId = await GetUserClinicId(userId.Value, ct);
        if (!clinicId.HasValue) return StatusCode(403, new { error = "invalid_selected_clinic" });
        var tenantId = User.GetTenantId()!.Value;

        // Income from completed payments; refunds are reported additively and netted for profit.
        var payments = await QueryPayments(startDt, endDt, clinicId).ToListAsync(ct);
        var refunds = await QueryRefunds(clinicId)
            .Where(p => p.PaidAt >= startDt && p.PaidAt < endDt)
            .SumAsync(p => -p.Amount, ct);

        var grossIncome = payments.Sum(p => p.Amount);
        var netCollections = grossIncome - refunds;

        // GST is per-invoice and recognized once, pro-rata to collections. Cumulative allocation
        // stops a later period from reporting tax already recognized; approved credit notes
        // reverse it in the period the note is issued.
        var invoiceIds = payments.Select(p => p.InvoiceId).Distinct().ToList();
        var creditedInvoiceIds = await QueryApprovedCreditNotes(clinicId)
            .Where(c => c.IssuedAt >= startDt && c.IssuedAt < endDt)
            .Select(c => c.InvoiceId).Distinct().ToListAsync(ct);
        var scopeIds = invoiceIds.Union(creditedInvoiceIds).ToList();
        var receiptsToDate = await QueryCompletedPayments(clinicId)
            .Where(p => scopeIds.Contains(p.InvoiceId) && p.PaidAt < endDt)
            .ToListAsync(ct);
        var creditNotesToDate = await QueryApprovedCreditNotes(clinicId)
            .Where(c => scopeIds.Contains(c.InvoiceId) && c.IssuedAt < endDt)
            .ToListAsync(ct);
        var gstLiability = InvoiceGstRecognition.RecognizedInWindow(
            receiptsToDate, creditNotesToDate, startDt, endDt);

        // Expenses
        var expensesQuery = _db.LedgerExpenses
            .Where(e => e.TenantId == tenantId && e.ExpenseDate >= startDate && e.ExpenseDate < endDate);

        var expenses = await expensesQuery.ToListAsync(ct);
        var totalExpenses = expenses.Sum(e => e.Amount);
        var expenseByCategory = expenses
            .GroupBy(e => e.Category)
            .Select(g => new { category = g.Key.ToString().ToLower(), total = g.Sum(e => e.Amount) })
            .ToList();

        var netProfit = netCollections - totalExpenses - gstLiability;

        // Section 44ADA: 50% deemed profit for medical professionals
        var deemedIncome44ADA = Math.Round(grossIncome * 0.5m, 2);

        // Prior month for MoM comparison
        var prevStartDt = _timezone.GetStartOfDayUtc(startDate.AddMonths(-1), AccountingTimezone);
        var prevGross = await QueryPayments(prevStartDt, startDt, clinicId).SumAsync(p => p.Amount, ct);
        var grossCollectionsMoMChange = prevGross == 0 ? 0m : Math.Round((grossIncome - prevGross) / prevGross * 100m, 1);
        var expensesPercentage = grossIncome == 0 ? 0m : Math.Round(totalExpenses / grossIncome * 100m, 1);
        var netProfitMargin = netCollections <= 0 ? 0m : Math.Round(netProfit / netCollections * 100m, 1);

        return Ok(new
        {
            month = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            grossIncome,
            refunds,
            netCollections,
            expenses = totalExpenses,
            expensesPercentage,
            gstLiability,
            netProfit,
            netProfitMargin,
            grossCollectionsMoMChange,
            deemedIncome44ADA,
            expenseByCategory,
            invoiceCount = payments.Select(p => p.InvoiceId).Distinct().Count(),
            paymentCount = payments.Count
        });
    }

    [HttpGet("tax/itr4")]
    public async Task<IActionResult> ITR4([FromQuery] string? month, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var endDate = startDate.AddMonths(1);
        var startDt = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var endDt = _timezone.GetStartOfDayUtc(endDate, AccountingTimezone);

        var clinicId = await GetUserClinicId(userId.Value, ct);
        if (!clinicId.HasValue) return StatusCode(403, new { error = "invalid_selected_clinic" });

        var payments = await QueryPayments(startDt, endDt, clinicId).ToListAsync(ct);

        var grossReceipts = payments.Sum(p => p.Amount);
        var deemedProfit = Math.Round(payments.Sum(p => p.Amount) * 0.5m, 2);
        var taxableIncome = deemedProfit;

        return Ok(new
        {
            month = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            grossReceipts,
            deemedProfitRate = "50%",
            deemedProfit,
            taxableIncome,
            taxRate = "As per slab",
            note = "Section 44ADA: 50% of gross receipts deemed as profit for medical professionals with gross receipts up to ₹50 Lakhs"
        });
    }

    [HttpGet("tax/gstr1")]
    public async Task<IActionResult> GSTR1([FromQuery] string? month, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var startDt = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var endDt = _timezone.GetStartOfDayUtc(startDate.AddMonths(1), AccountingTimezone);

        var clinicId = await GetUserClinicId(userId.Value, ct);
        if (!clinicId.HasValue) return StatusCode(403, new { error = "invalid_selected_clinic" });

        return Ok(await BuildGstr1Async(startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture), startDt, endDt, clinicId, ct));
    }

    [HttpGet("payment-distribution")]
    public async Task<IActionResult> PaymentDistribution([FromQuery] string? month, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var startDt = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var endDt = _timezone.GetStartOfDayUtc(startDate.AddMonths(1), AccountingTimezone);
        var clinicId = await GetUserClinicId(userId.Value, ct);
        if (!clinicId.HasValue) return StatusCode(403, new { error = "invalid_selected_clinic" });

        var payments = await QueryPayments(startDt, endDt, clinicId).ToListAsync(ct);
        var refunds = await QueryRefunds(clinicId)
            .Where(p => p.PaidAt >= startDt && p.PaidAt < endDt)
            .ToListAsync(ct);

        // Net each tender bucket by refunds issued in the same month, so the split matches cash flow.
        decimal Net(params PaymentMethod[] methods)
        {
            var collected = payments.Where(p => methods.Contains(p.Method)).Sum(p => p.Amount);
            var refunded = refunds.Where(p => methods.Contains(p.Method)).Sum(p => -p.Amount);
            return collected - refunded;
        }

        var upi = Net(PaymentMethod.UPI, PaymentMethod.Razorpay);
        var card = Net(PaymentMethod.Card, PaymentMethod.NetBanking, PaymentMethod.Wallet);
        var cash = Net(PaymentMethod.Cash);
        var other = Net(PaymentMethod.AdvanceDeposit);
        var total = upi + card + cash + other;
        static decimal Pct(decimal amount, decimal total) => total == 0 ? 0m : Math.Round(amount / total * 100m, 1);

        return Ok(new
        {
            month = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            upiAmount = upi,
            upiPercentage = Pct(upi, total),
            cardAmount = card,
            cardPercentage = Pct(card, total),
            cashAmount = cash,
            cashPercentage = Pct(cash, total),
            otherAmount = other,
            otherPercentage = Pct(other, total),
            totalCollected = total,
            digitalPaymentPercentage = Pct(upi + card, total)
        });
    }

    [HttpGet("platform-health")]
    public async Task<IActionResult> PlatformHealth([FromQuery] string? month, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var startDt = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var endDt = _timezone.GetStartOfDayUtc(startDate.AddMonths(1), AccountingTimezone);
        var clinicId = await GetUserClinicId(userId.Value, ct);
        if (!clinicId.HasValue) return StatusCode(403, new { error = "invalid_selected_clinic" });
        var tenantId = User.GetTenantId()!.Value;

        var totalUsers = await _db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active")
            .Select(m => m.UserId).Distinct().CountAsync(ct);
        var monthlyConsultations = await _db.Consultations
            .CountAsync(c => c.TenantId == tenantId && c.Appointment.TenantId == tenantId
                && c.Appointment.ClinicId == clinicId && c.CreatedAt >= startDt && c.CreatedAt < endDt, ct);

        return Ok(new
        {
            month = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            activeTenants = 1,
            totalUsers,
            monthlyConsultations,
            overallStatus = totalUsers > 0 ? "healthy" : "degraded"
        });
    }

    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] ExportRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (!TryParseMonth(request.Month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var month = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var startDt = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var endDt = _timezone.GetStartOfDayUtc(startDate.AddMonths(1), AccountingTimezone);
        var clinicId = await GetUserClinicId(userId.Value, ct);
        if (!clinicId.HasValue) return StatusCode(403, new { error = "invalid_selected_clinic" });

        switch (request.Format)
        {
            case "ITR-4 CSV":
                {
                    var payments = await QueryPayments(startDt, endDt, clinicId).ToListAsync(ct);
                    var gross = payments.Sum(p => p.Amount);
                    var deemedProfit = Math.Round(gross * 0.5m, 2);
                    var sb = new StringBuilder();
                    sb.AppendLine("Section 44ADA Computation,Value (INR)");
                    sb.AppendLine(FormattableString.Invariant($"Gross Professional Receipts,{gross:0.00}"));
                    sb.AppendLine(FormattableString.Invariant($"Deemed Profit (50%),{deemedProfit:0.00}"));
                    sb.AppendLine(FormattableString.Invariant($"Taxable Income,{deemedProfit:0.00}"));
                    sb.AppendLine();
                    sb.AppendLine("Paid At (Asia/Kolkata),Invoice,Method,Amount");
                    foreach (var p in payments.OrderBy(p => p.PaidAt))
                        sb.AppendLine(FormattableString.Invariant($"{TimeZoneInfo.ConvertTime(p.PaidAt!.Value, _timezone.ResolveTimeZone(null, AccountingTimezone)):yyyy-MM-dd HH:mm},{p.Invoice?.InvoiceNumber ?? 0},{p.Method},{p.Amount:0.00}"));
                    return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"ITR4-{month}.csv");
                }
            case "GSTR-1 JSON":
                {
                    var payload = await BuildGstr1Async(month, startDt, endDt, clinicId, ct);
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    return File(Encoding.UTF8.GetBytes(json), "application/json", $"GSTR1-{month}.json");
                }
            default:
                return BadRequest(new { error = "unsupported_format" });
        }
    }

    private IQueryable<Payment> QueryOwnedPayments(Guid? clinicId)
    {
        var tenantId = User.GetTenantId()!.Value;
        var query = _db.Payments
            .Include(p => p.Invoice).ThenInclude(inv => inv.Appointment)
            .Where(p => p.TenantId == tenantId && p.Invoice.TenantId == tenantId);

        // Walk-ins belong to the tenant's unique clinic; appointment joins must agree too.
        return query.Where(p => p.Invoice.AppointmentId == null
                              || p.Invoice.Appointment != null && p.Invoice.Appointment.TenantId == tenantId
                                  && p.Invoice.Appointment.ClinicId == clinicId);
    }

    private IQueryable<Payment> QueryCompletedPayments(Guid? clinicId)
        => QueryOwnedPayments(clinicId).Where(p => p.Status == PaymentStatus.Completed);

    private IQueryable<Payment> QueryRefunds(Guid? clinicId)
        => QueryOwnedPayments(clinicId).Where(p => p.Status == PaymentStatus.Refunded
            && p.VerificationStatus == PaymentVerificationStatus.Confirmed && p.Amount < 0);

    private IQueryable<Payment> QueryPayments(DateTime startDt, DateTime endDt, Guid? clinicId)
        => QueryCompletedPayments(clinicId).Where(p => p.PaidAt >= startDt && p.PaidAt < endDt);

    private IQueryable<CreditNote> QueryApprovedCreditNotes(Guid? clinicId)
    {
        var tenantId = User.GetTenantId()!.Value;
        return _db.CreditNotes.AsNoTracking()
            .Include(c => c.Invoice)
            .Where(c => c.TenantId == tenantId
                     && c.Invoice.TenantId == tenantId
                     && (c.Invoice.AppointmentId == null || c.Invoice.Appointment != null
                         && c.Invoice.Appointment.TenantId == tenantId && c.Invoice.Appointment.ClinicId == clinicId)
                     && c.Status == CreditNoteStatus.Approved);
    }

    private IQueryable<Invoice> QueryInvoices(DateTime startDt, DateTime endDt, Guid? clinicId)
    {
        var tenantId = User.GetTenantId()!.Value;
        var query = _db.Invoices
            .Include(i => i.Appointment).ThenInclude(a => a!.Patient)
            // A draft has no legal number yet; cancelled documents are reported separately, never as supply.
            .Where(i => i.TenantId == tenantId && i.CreatedAt >= startDt && i.CreatedAt < endDt
                     && i.InvoiceNumber > 0
                     && i.Status != InvoiceStatus.Draft
                     && i.Status != InvoiceStatus.Cancelled);

        query = query.Where(i => i.AppointmentId == null
                              || i.Appointment != null && i.Appointment.TenantId == tenantId
                                  && i.Appointment.ClinicId == clinicId);

        return query;
    }

    private async Task<object> BuildGstr1Async(string month, DateTime startDt, DateTime endDt, Guid? clinicId, CancellationToken ct)
    {
        var tenantId = User.GetTenantId()!.Value;

        var taxProfile = await _db.TenantTaxProfiles.AsNoTracking()
            .Where(p => p.TenantId == tenantId && (p.ClinicId == null || p.ClinicId == clinicId))
            .OrderByDescending(p => p.ClinicId == clinicId)
            .FirstOrDefaultAsync(ct);

        var invoices = await QueryInvoices(startDt, endDt, clinicId)
            .Include(i => i.LineItems)
            .ToListAsync(ct);

        // Credit notes reduce the month's liability and must reach GSTR-1 (Table 9B), not only the ledger.
        var creditNotes = await _db.CreditNotes.AsNoTracking()
            .Include(c => c.Invoice)
            .Where(c => c.TenantId == tenantId
                     && c.Invoice.TenantId == tenantId
                     && (c.Invoice.AppointmentId == null || c.Invoice.Appointment != null
                         && c.Invoice.Appointment.TenantId == tenantId && c.Invoice.Appointment.ClinicId == clinicId)
                     && c.Status == CreditNoteStatus.Approved
                     && c.IssuedAt >= startDt && c.IssuedAt < endDt)
            .ToListAsync(ct);

        return BuildGstr1Payload(month, invoices, creditNotes, taxProfile);
    }

    private object BuildGstr1Payload(
        string month,
        List<Invoice> invoices,
        List<CreditNote> creditNotes,
        TenantTaxProfile? taxProfile)
    {
        var supplierState = taxProfile?.StateCode;
        // OPD supply happens at the clinic, so the place of supply is the supplier's own state.
        var placeOfSupply = supplierState ?? "";
        var isRegistered = taxProfile?.IsGstRegistered == true;

        // One pass over every line, resolving both stored amount shapes up front.
        var lines = invoices
            .SelectMany(i => i.LineItems.Select(li => new
            {
                li.HsnSacCode,
                li.GstRate,
                li.Quantity,
                li.TaxCategory,
                Taxable = li.TaxableAmount(i.InvoiceType),
                Tax = li.ResolvedTaxAmount()
            }))
            .ToList();

        // No GSTIN is captured for patients, so every invoice is B2C. Table 7 (B2CS) is
        // rate-wise + place-of-supply-wise totals, not one row per invoice.
        var b2cs = lines
            .Where(l => l.Tax > 0)
            .GroupBy(l => l.GstRate)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var tax = g.Sum(l => l.Tax);
                var cgst = Math.Round(tax / 2m, 2);
                return new
                {
                    type = "OE",
                    placeOfSupply,
                    rate = g.Key,
                    taxableValue = g.Sum(l => l.Taxable),
                    igstAmount = 0m,
                    cgstAmount = cgst,
                    sgstAmount = tax - cgst,
                    cessAmount = 0m
                };
            })
            .ToList();

        // A line with no resolved tax is a nil/exempt supply, whatever its stored category.
        var exemptLines = lines.Where(l => l.Tax == 0).ToList();

        var nilExempt = new[]
        {
            new
            {
                placeOfSupply,
                nilAmount = exemptLines.Where(l => l.TaxCategory == TaxCategory.Nil).Sum(l => l.Taxable),
                exemptAmount = exemptLines.Where(l => l.TaxCategory != TaxCategory.Nil).Sum(l => l.Taxable)
            }
        }.Where(n => n.nilAmount > 0 || n.exemptAmount > 0).ToList();

        // Table 12: HSN/SAC summary. Mandatory above ₹5 crore turnover; always useful for reconciliation.
        var hsnSummary = lines
            .GroupBy(l => new { l.HsnSacCode, l.GstRate })
            .OrderBy(g => g.Key.HsnSacCode).ThenBy(g => g.Key.GstRate)
            .Select(g =>
            {
                var tax = g.Sum(l => l.Tax);
                var cgst = Math.Round(tax / 2m, 2);
                return new
                {
                    hsnSac = g.Key.HsnSacCode,
                    description = "Healthcare services",
                    uqc = "OTH",
                    quantity = g.Sum(l => l.Quantity),
                    rate = g.Key.GstRate,
                    taxableValue = g.Sum(l => l.Taxable),
                    igstAmount = 0m,
                    cgstAmount = cgst,
                    sgstAmount = tax - cgst,
                    cessAmount = 0m
                };
            })
            .ToList();

        // Table 9B (CDNUR): unregistered credit notes, tax split using the parent invoice's taxable ratio.
        var cdnur = creditNotes
            .Select(cn =>
            {
                var invoiceTotal = cn.Invoice?.Total ?? 0m;
                var taxableFraction = invoiceTotal > 0 ? cn.Invoice!.Subtotal / invoiceTotal : 1m;
                var taxableValue = Math.Round(cn.Amount * taxableFraction, 2);
                var tax = cn.Amount - taxableValue;
                var cgst = Math.Round(tax / 2m, 2);
                return new
                {
                    noteNumber = cn.FormatCreditNoteNumber(),
                    noteDate = TimeZoneInfo.ConvertTime(cn.IssuedAt, _timezone.ResolveTimeZone(null, AccountingTimezone))
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    noteType = "C",
                    noteValue = cn.Amount,
                    taxableValue,
                    rate = taxableValue > 0 ? Math.Round(tax / taxableValue * 100m, 2) : 0m,
                    placeOfSupply,
                    igstAmount = 0m,
                    cgstAmount = cgst,
                    sgstAmount = tax - cgst,
                    cessAmount = 0m
                };
            })
            .ToList();

        var warnings = new List<string>();
        if (!isRegistered) warnings.Add("gst_not_registered");
        if (string.IsNullOrWhiteSpace(supplierState)) warnings.Add("supplier_state_missing");
        if (invoices.Any(i => i.DiscountAmount > 0)) warnings.Add("invoice_level_discount_not_apportioned_to_lines");

        var totalGst = invoices.Sum(i => i.GstAmount);
        var creditNoteTax = cdnur.Sum(c => c.cgstAmount + c.sgstAmount);

        return new
        {
            month,
            supplierGstin = taxProfile?.Gstin,
            supplierStateCode = supplierState,
            placeOfSupply,
            isGstRegistered = isRegistered,
            // A registered-recipient (B2B) section requires a recipient GSTIN, which this app never captures.
            b2bInvoices = Array.Empty<object>(),
            b2cs,
            nilExempt,
            cdnur,
            hsnSummary,
            totalInvoices = invoices.Count,
            totalInvoiceValue = invoices.Sum(i => i.Total),
            totalTaxableValue = b2cs.Sum(b => b.taxableValue),
            totalExemptValue = nilExempt.Sum(n => n.nilAmount + n.exemptAmount),
            totalGst,
            creditNoteValue = creditNotes.Sum(c => c.Amount),
            netTaxPayable = totalGst - creditNoteTax,
            warnings
        };
    }

    private async Task<Guid?> GetUserClinicId(Guid userId, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        if (tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty)
            return null;
        return await _db.Clinics.AnyAsync(c => c.Id == clinicId && c.TenantId == tenantId, ct)
            && await _db.TenantMemberships.AnyAsync(m => m.UserId == userId && m.TenantId == tenantId
                && m.ClinicId == clinicId && m.Status == "active", ct) ? clinicId : null;
    }

    private bool TryParseMonth(string? month, out DateOnly startDate)
    {
        if (month is null)
        {
            var today = _timezone.GetLocalDate(null, AccountingTimezone);
            startDate = new DateOnly(today.Year, today.Month, 1);
            return true;
        }
        return DateOnly.TryParseExact($"{month}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out startDate) && startDate.Year is > 1 and < 9999;
    }
}

public record ExportRequest(
    string Format, // "ITR-4 CSV" | "GSTR-1 JSON" | "Audit PDF"
    string Month // "YYYY-MM"
);
