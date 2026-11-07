using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/ledger")]
public class LedgerController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ITimezoneService _timezone;
    private const string AccountingTimezone = "Asia/Kolkata";

    public LedgerController(HospitalCrmDbContext db, ITimezoneService? timezone = null)
    {
        _db = db;
        _timezone = timezone ?? new TimezoneService();
    }

    private Guid TenantId => User.GetTenantId() ?? Guid.Empty;

    // ----- Section 5 / FR-11-01: Income read directly from invoices/payments -----

    [HttpGet("income")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist", "Doctor")]
    public async Task<IActionResult> Income([FromQuery] string? month, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var start = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var end = _timezone.GetStartOfDayUtc(startDate.AddMonths(1), AccountingTimezone);

        var payments = await QueryPayments()
            .Where(p => p.Status == PaymentStatus.Completed && p.PaidAt >= start && p.PaidAt < end)
            .ToListAsync(ct);

        var totalIncome = payments.Sum(p => p.Amount);
        // GST is recognized once per invoice, pro-rata to what was actually collected this month.
        var gstCollected = payments
            .GroupBy(p => p.Invoice)
            .Where(g => g.Key is { Total: > 0 })
            .Sum(g => g.Key!.GstAmount * Math.Min(1m, g.Sum(p => p.Amount) / g.Key!.Total));
        var invoiceCount = payments.Select(p => p.InvoiceId).Distinct().Count();

        return Ok(new
        {
            month = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            totalIncome,
            gstCollected,
            invoiceCount,
            paymentCount = payments.Count
        });
    }

    // ----- Section 5: Daily Collection by Payment Tender (Cash, UPI, Card, etc.) -----

    [HttpGet("daily-collection")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    public async Task<IActionResult> DailyCollection([FromQuery] string? date, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        if (!TryParseDay(date, out var targetDate))
            return BadRequest(new { error = "invalid_date", message = "Use YYYY-MM-DD." });
        var start = _timezone.GetStartOfDayUtc(targetDate, AccountingTimezone);
        var end = _timezone.GetStartOfDayUtc(targetDate.AddDays(1), AccountingTimezone);

        var payments = await QueryCollections(start, end).ToListAsync(ct);

        var confirmedPayments = payments.Where(p => p.Status == PaymentStatus.Completed).ToList();
        var reportedPayments = payments.Where(p => p.VerificationStatus == PaymentVerificationStatus.Reported && p.Status != PaymentStatus.Completed).ToList();

        var confirmedByMethod = confirmedPayments
            .GroupBy(p => p.Method)
            .Select(g => new
            {
                method = g.Key.ToString().ToLower(),
                amount = g.Sum(p => p.Amount),
                count = g.Count()
            })
            .ToList();

        return Ok(new
        {
            date = targetDate.ToString("yyyy-MM-dd"),
            totalConfirmed = confirmedPayments.Sum(p => p.Amount),
            totalReportedPending = reportedPayments.Sum(p => p.Amount),
            confirmedCount = confirmedPayments.Count,
            reportedCount = reportedPayments.Count,
            byMethod = confirmedByMethod
        });
    }

    // ----- Section 5: Outstanding Patient Dues & Aging Analysis -----

    [HttpGet("outstanding-dues")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    public async Task<IActionResult> OutstandingDues(CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        var tenantId = TenantId;
        var now = DateTimeOffset.UtcNow;

        var unpaidInvoices = await QueryInvoices()
            .Include(i => i.Appointment).ThenInclude(a => a!.Patient)
            .Where(i => (i.AppointmentId == null || i.Appointment!.Patient == null || i.Appointment.Patient.TenantId == tenantId)
                     && i.Status != InvoiceStatus.Paid
                     && i.Status != InvoiceStatus.Cancelled
                     && i.Status != InvoiceStatus.Refunded
                     && i.BalanceDue > 0)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        var totalOutstanding = unpaidInvoices.Sum(i => i.BalanceDue);

        // Aging buckets: 0-30, 31-60, 61-90, 90+ days
        var bucket0To30 = unpaidInvoices.Where(i => (now - i.CreatedAt).TotalDays <= 30).Sum(i => i.BalanceDue);
        var bucket31To60 = unpaidInvoices.Where(i => (now - i.CreatedAt).TotalDays > 30 && (now - i.CreatedAt).TotalDays <= 60).Sum(i => i.BalanceDue);
        var bucket61To90 = unpaidInvoices.Where(i => (now - i.CreatedAt).TotalDays > 60 && (now - i.CreatedAt).TotalDays <= 90).Sum(i => i.BalanceDue);
        var bucket90Plus = unpaidInvoices.Where(i => (now - i.CreatedAt).TotalDays > 90).Sum(i => i.BalanceDue);

        var list = unpaidInvoices.Select(i => new
        {
            invoiceId = i.Id,
            invoiceNumber = i.FormatInvoiceNumber(),
            patientName = i.Appointment?.Patient?.Name ?? i.WalkInCustomerName ?? "Walk-in Patient",
            patientPhone = i.Appointment?.Patient?.Phone ?? i.WalkInCustomerPhone,
            total = i.Total,
            paidAmount = i.PaidAmount,
            balanceDue = i.BalanceDue,
            status = i.Status.ToString().ToLower(),
            daysPast = (int)(now - i.CreatedAt).TotalDays,
            createdAt = i.CreatedAt
        }).ToList();

        return Ok(new
        {
            totalOutstanding,
            aging = new
            {
                current0To30Days = bucket0To30,
                past31To60Days = bucket31To60,
                past61To90Days = bucket61To90,
                over90Days = bucket90Plus
            },
            invoices = list
        });
    }

    // ----- Section 5: Append-Only Financial Ledger Audit Journal -----

    [HttpGet("journal")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Journal([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        var tenantId = TenantId;
        var entries = await _db.FinanceLedgerEntries
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(e => new
            {
                id = e.Id,
                entryType = e.EntryType.ToString(),
                referenceType = e.ReferenceType,
                referenceId = e.ReferenceId,
                referenceNumber = e.ReferenceNumber,
                accountCategory = e.AccountCategory,
                debitAmount = e.DebitAmount,
                creditAmount = e.CreditAmount,
                narration = e.Narration,
                postedByUserId = e.PostedByUserId,
                createdAt = e.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(entries);
    }

    // ----- FR-11-02: Manual Expense Entry (gated by Finance module) -----

    [HttpPost("expenses")]
    [RequireModule(ModuleRegistry.Finance)]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> CreateExpense([FromBody] CreateExpenseRequest request, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        if (!Enum.TryParse<ExpenseCategory>(request.Category, true, out var category))
            return BadRequest(new { error = "invalid_category" });

        if (request.Amount <= 0)
            return BadRequest(new { error = "amount_must_be_positive" });

        if (!DateOnly.TryParse(request.ExpenseDate, out var date))
            return BadRequest(new { error = "invalid_date" });

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var tenantId = TenantId;
        var expense = new LedgerExpense
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Category = category,
            CategoryOther = category == ExpenseCategory.Other
                ? (request.CategoryOther ?? request.Category)
                : category.ToString(),
            Amount = request.Amount,
            ExpenseDate = date,
            Note = request.Note,
            RecordedBy = userId.Value,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.LedgerExpenses.Add(expense);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = expense.Id,
            category = expense.Category.ToString().ToLower(),
            amount = expense.Amount,
            expenseDate = expense.ExpenseDate.ToString("yyyy-MM-dd")
        });
    }

    [HttpPatch("expenses/{id:guid}")]
    [RequireModule(ModuleRegistry.Finance)]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> UpdateExpense(Guid id, [FromBody] UpdateExpenseRequest request, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        var tenantId = TenantId;
        var expense = await _db.LedgerExpenses.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (expense is null)
            return NotFound(new { error = "expense_not_found" });

        if (request.Amount.HasValue && request.Amount.Value > 0)
            expense.Amount = request.Amount.Value;

        if (!string.IsNullOrWhiteSpace(request.Category) &&
            Enum.TryParse<ExpenseCategory>(request.Category, true, out var category))
        {
            expense.Category = category;
            expense.CategoryOther = category == ExpenseCategory.Other
                ? (request.CategoryOther ?? request.Category)
                : category.ToString();
        }

        if (!string.IsNullOrWhiteSpace(request.Note))
            expense.Note = request.Note;

        if (DateOnly.TryParse(request.ExpenseDate, out var date))
            expense.ExpenseDate = date;

        expense.EditedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = expense.Id,
            category = expense.Category.ToString().ToLower(),
            amount = expense.Amount,
            editedAt = expense.EditedAt
        });
    }

    [HttpGet("expenses")]
    [RequireModule(ModuleRegistry.Finance)]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListExpenses([FromQuery] string? month, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var tenantId = TenantId;
        var endDate = startDate.AddMonths(1);

        var expenses = await _db.LedgerExpenses
            .Where(e => e.TenantId == tenantId && e.ExpenseDate >= startDate && e.ExpenseDate < endDate)
            .OrderByDescending(e => e.ExpenseDate)
            .Select(e => new
            {
                id = e.Id,
                category = e.Category.ToString().ToLower(),
                amount = e.Amount,
                expenseDate = e.ExpenseDate.ToString("yyyy-MM-dd"),
                note = e.Note,
                editedAt = e.EditedAt
            })
            .ToListAsync(ct);

        return Ok(expenses);
    }

    // ----- FR-11-03 / Section 5: Monthly EOD & Reconciliation Summary -----

    [HttpGet("summary")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Summary([FromQuery] string? month, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        if (!TryParseMonth(month, out var startDate))
            return BadRequest(new { error = "invalid_month", message = "Use YYYY-MM." });
        var monthLabel = startDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var tenantId = TenantId;
        var endDate = startDate.AddMonths(1);
        var start = _timezone.GetStartOfDayUtc(startDate, AccountingTimezone);
        var end = _timezone.GetStartOfDayUtc(endDate, AccountingTimezone);

        var payments = await QueryCollections(start, end).ToListAsync(ct);

        var confirmedIncome = payments.Where(p => p.Status == PaymentStatus.Completed).Sum(p => p.Amount);
        var reportedPending = payments.Where(p => p.VerificationStatus == PaymentVerificationStatus.Reported && p.Status != PaymentStatus.Completed).Sum(p => p.Amount);

        var ownedInvoiceIds = QueryInvoices().Select(i => i.Id);
        // Pending/rejected notes have no realized effect; use the approved note's issue period.
        var creditNotes = await _db.CreditNotes
            .Where(c => c.TenantId == tenantId
                     && ownedInvoiceIds.Contains(c.InvoiceId)
                     && c.Status == CreditNoteStatus.Approved
                     && c.IssuedAt >= start && c.IssuedAt < end)
            .SumAsync(c => c.Amount, ct);

        // Expenses (if any)
        var expenseByCategory = await _db.LedgerExpenses
            .Where(e => e.TenantId == tenantId && e.ExpenseDate >= startDate && e.ExpenseDate < endDate)
            .GroupBy(e => e.Category)
            .Select(g => new
            {
                category = g.Key.ToString().ToLower(),
                total = g.Sum(e => e.Amount)
            })
            .ToListAsync(ct);

        var totalExpenses = expenseByCategory.Sum(x => x.total);

        return Ok(new
        {
            month = monthLabel,
            income = confirmedIncome,
            reportedPendingIncome = reportedPending,
            creditNotesIssued = creditNotes,
            expenses = new
            {
                total = totalExpenses,
                byCategory = expenseByCategory
            },
            net = confirmedIncome - creditNotes - totalExpenses,
            exportFormat = "json"
        });
    }

    // ----- FR-11-01: Daily Collections (accessible to Receptionist/Doctor for shift reconciliation) -----

    [HttpGet("daily")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    public async Task<IActionResult> Daily([FromQuery] string? date, CancellationToken ct)
    {
        if (await ValidateScope(ct) is { } denied) return denied;
        if (!TryParseDay(date, out var targetDate))
            return BadRequest(new { error = "invalid_date", message = "Use YYYY-MM-DD." });
        var start = _timezone.GetStartOfDayUtc(targetDate, AccountingTimezone);
        var end = _timezone.GetStartOfDayUtc(targetDate.AddDays(1), AccountingTimezone);
        var tenantId = TenantId;

        var payments = await QueryPayments()
            .Where(p => p.Status == PaymentStatus.Completed
                     && p.PaidAt >= start
                     && p.PaidAt < end)
            .ToListAsync(ct);

        var totalIncome = payments.Sum(p => p.Amount);
        var gstCollected = payments
            .GroupBy(p => p.Invoice)
            .Where(g => g.Key is { Total: > 0 })
            .Sum(g => g.Key!.GstAmount * Math.Min(1m, g.Sum(p => p.Amount) / g.Key!.Total));
        var paymentCount = payments.Count;

        var expenses = await _db.LedgerExpenses
            .Where(e => e.TenantId == tenantId && e.ExpenseDate == targetDate)
            .ToListAsync(ct);

        var totalExpenses = expenses.Sum(e => e.Amount);
        var expenseByCategory = expenses
            .GroupBy(e => e.Category)
            .Select(g => new { category = g.Key.ToString().ToLower(), total = g.Sum(e => e.Amount) })
            .ToList();

        return Ok(new
        {
            date = targetDate.ToString("yyyy-MM-dd"),
            income = new
            {
                total = totalIncome,
                gstCollected,
                paymentCount
            },
            expenses = new
            {
                total = totalExpenses,
                byCategory = expenseByCategory
            },
            net = totalIncome - totalExpenses
        });
    }

    private async Task<IActionResult?> ValidateScope(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null || userId == Guid.Empty) return Unauthorized(new { error = "invalid_token" });
        var tenantId = TenantId;
        var clinicId = User.GetClinicId();
        if (tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty
            || !await _db.Clinics.AnyAsync(c => c.Id == clinicId && c.TenantId == tenantId, ct)
            || !await _db.TenantMemberships.AnyAsync(m => m.UserId == userId && m.TenantId == tenantId
                && m.ClinicId == clinicId && m.Status == "active", ct))
            return StatusCode(403, new { error = "invalid_selected_clinic" });
        return null;
    }

    private IQueryable<Invoice> QueryInvoices()
    {
        var tenantId = TenantId;
        var clinicId = User.GetClinicId();
        // Walk-ins, expenses and journal rows have tenant ownership; appointments also bind a clinic.
        return _db.Invoices.AsNoTracking().Where(i => i.TenantId == tenantId
            && (i.AppointmentId == null || i.Appointment != null
                && i.Appointment.TenantId == tenantId && i.Appointment.ClinicId == clinicId));
    }

    private IQueryable<Payment> QueryPayments()
    {
        var tenantId = TenantId;
        var ownedInvoiceIds = QueryInvoices().Select(i => i.Id);
        return _db.Payments.AsNoTrackingWithIdentityResolution().Include(p => p.Invoice)
            .Where(p => p.TenantId == tenantId && ownedInvoiceIds.Contains(p.InvoiceId));
    }

    private IQueryable<Payment> QueryCollections(DateTime start, DateTime end) => QueryPayments()
        .Where(p => p.Status == PaymentStatus.Completed && p.PaidAt >= start && p.PaidAt < end
            || p.Status != PaymentStatus.Completed && p.VerificationStatus == PaymentVerificationStatus.Reported
                && p.ReportedAt >= start && p.ReportedAt < end);

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

    private bool TryParseDay(string? date, out DateOnly targetDate)
    {
        if (date is null)
        {
            targetDate = _timezone.GetLocalDate(null, AccountingTimezone);
            return true;
        }
        return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out targetDate) && targetDate.Year is > 1 and < 9999;
    }
}

public record CreateExpenseRequest(
    [Required(ErrorMessage = "Expense Category is required")]
    string Category,

    [StringLength(100, ErrorMessage = "CategoryOther cannot exceed 100 characters")]
    string? CategoryOther,

    [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between ₹0.01 and ₹1,00,00,000")]
    decimal Amount,

    [Required(ErrorMessage = "ExpenseDate is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "ExpenseDate must be YYYY-MM-DD")]
    string ExpenseDate,

    [StringLength(500, ErrorMessage = "Note cannot exceed 500 characters")]
    string? Note);

public record UpdateExpenseRequest(
    [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between ₹0.01 and ₹1,00,00,000")]
    decimal? Amount,

    string? Category,

    [StringLength(100, ErrorMessage = "CategoryOther cannot exceed 100 characters")]
    string? CategoryOther,

    [StringLength(500, ErrorMessage = "Note cannot exceed 500 characters")]
    string? Note,

    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "ExpenseDate must be YYYY-MM-DD")]
    string? ExpenseDate);
