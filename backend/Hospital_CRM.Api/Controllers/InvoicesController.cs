using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class InvoicesController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IConfiguration _config;
    private readonly RazorpayOrderClient _razorpay;
    private readonly ITenantContext? _tenantContext;
    private readonly ITimezoneService _timezone;
    private const string AccountingTimezone = AccountingTimezones.Kolkata;

    public InvoicesController(HospitalCrmDbContext db, IConfiguration config, RazorpayOrderClient razorpay,
        ITenantContext? tenantContext = null, ITimezoneService? timezone = null)
    {
        _db = db;
        _config = config;
        _razorpay = razorpay;
        _tenantContext = tenantContext;
        _timezone = timezone ?? new TimezoneService();
    }

    private Guid TenantId => User.GetTenantId() ?? Guid.Empty;

    private Task<TenantMembership?> SelectedBillingMembershipAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        if (userId is null || tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty
            || !Enum.TryParse<UserRole>(User.GetUserRole(), true, out var role)
            || role is not (UserRole.Receptionist or UserRole.Doctor or UserRole.ClinicAdmin or UserRole.Pharmacist))
            return Task.FromResult<TenantMembership?>(null);
        return _db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(m =>
            m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active" && m.Role == role
            && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
    }

    private async Task<int> NextPrintCopyAsync(Invoice invoice, CancellationToken ct) =>
        Math.Max(invoice.PrintCount, await _db.InvoicePrintLogs.Where(l => l.InvoiceId == invoice.Id && l.TenantId == invoice.TenantId)
            .MaxAsync(l => (int?)l.CopyNumber, ct) ?? 0) + 1;

    private void RecordPrint(Invoice invoice, int copyNumber)
    {
        invoice.PrintCount = copyNumber;
        invoice.LastPrintedAt = DateTimeOffset.UtcNow;
        invoice.UpdatedAt = invoice.LastPrintedAt > invoice.UpdatedAt ? invoice.LastPrintedAt.Value : invoice.UpdatedAt.AddTicks(10);
        _db.InvoicePrintLogs.Add(new InvoicePrintLog
        {
            Id = Guid.NewGuid(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            PrintedByUserId = User.GetUserId()!.Value,
            PrintedAt = invoice.LastPrintedAt.Value,
            CopyNumber = copyNumber,
            Watermark = copyNumber > 1 ? "DUPLICATE COPY" : "ORIGINAL",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
    }

    [HttpPost("invoices")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    [RequirePermission(Permissions.InvoiceWrite)]
    public Task<IActionResult> GenerateInvoice([FromBody] GenerateInvoiceRequest request, CancellationToken ct) =>
        GenerateInvoiceAsync(request, ct);

    internal async Task<IActionResult> GenerateInvoiceAsync(GenerateInvoiceRequest request, CancellationToken ct, Guid? clientId = null)
    {
        var tenantId = TenantId;

        if (request?.LineItems is null || request.LineItems.Count == 0)
            return BadRequest(new { error = "line_items_required" });
        if (request.IdempotencyKey?.Length > 128)
            return BadRequest(new { error = "invalid_idempotency_key" });
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var existing = await _db.Invoices
                    .Include(i => i.LineItems)
                    .FirstOrDefaultAsync(i => i.IdempotencyKey == request.IdempotencyKey && i.TenantId == tenantId, ct);
                if (existing is not null)
                {
                    if (!InvoiceAccounting.InvoiceRequestAgrees(existing, request))
                        return Conflict(new { error = "idempotency_key_conflict" });
                    return Ok(new
                    {
                        invoiceId = existing.Id,
                        invoiceNumber = existing.FormatInvoiceNumber(),
                        subtotal = existing.Subtotal,
                        gstAmount = existing.GstAmount,
                        total = existing.Total,
                        paidAmount = existing.PaidAmount,
                        balanceDue = existing.BalanceDue,
                        status = existing.Status.ToString().ToLowerInvariant(),
                        idempotentReplay = true
                    });
                }
            }
            if (clientId.HasValue && await _db.Invoices.AnyAsync(i => i.Id == clientId, ct))
                return Conflict(new { error = "invoice_id_conflict" });

            var appointment = await _db.Appointments
                .Include(a => a.Patient)
                .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == tenantId, ct);

            if (appointment is null)
                return NotFound(new { error = "appointment_not_found" });

            // Retrieve tenant tax profile (or fallback to Notification 12/2017 healthcare exemption defaults)
            var taxProfile = await _db.TenantTaxProfiles
                .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

            var docType = taxProfile?.DocumentType ?? InvoiceDocumentType.BillOfSupply;
            var (lineItemsToCreate, lineError) = InvoiceAccounting.CalculateLines(request.LineItems, taxProfile);
            if (lineError != null) return BadRequest(lineError);
            var calculatedSubtotal = lineItemsToCreate.Sum(i => i.Amount - i.TaxAmount);
            var calculatedGst = lineItemsToCreate.Sum(i => i.TaxAmount);

            var gross = calculatedSubtotal + calculatedGst;
            if (request.DiscountAmount < 0 || request.DiscountAmount > gross || decimal.Round(request.DiscountAmount, 2) != request.DiscountAmount)
                return BadRequest(new { error = "invalid_discount_amount", message = $"Discount must be between 0 and ₹{gross:F2}." });

            if (request.DiscountAmount > 0 && string.IsNullOrWhiteSpace(request.DiscountReason))
                return BadRequest(new { error = "discount_reason_required" });
            if (request.DiscountReason?.Length > 255 || request.IdempotencyKey?.Length > 128)
                return BadRequest(new { error = "invalid_invoice_metadata" });

            var total = gross - request.DiscountAmount;

            // A bill containing any taxable supply is a Tax Invoice, never a Bill of Supply.
            if (calculatedGst > 0)
                docType = InvoiceDocumentType.TaxInvoice;

            var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);

            var linkedPharmacyId = request.LinkedPharmacyInvoiceId;
            if (linkedPharmacyId.HasValue && !await _db.Invoices.AnyAsync(i => i.Id == linkedPharmacyId && i.TenantId == tenantId
                && i.InvoiceType == InvoiceType.Pharmacy && i.Status != InvoiceStatus.Cancelled
                && (i.PatientId == appointment.PatientId || i.AppointmentId == appointment.Id), ct))
                return BadRequest(new { error = "invalid_linked_pharmacy_invoice" });
            if (!linkedPharmacyId.HasValue && request.AppointmentId != Guid.Empty)
            {
                linkedPharmacyId = await _db.Invoices
                    .Where(i => i.TenantId == appointment.TenantId
                             && i.AppointmentId == request.AppointmentId
                             && i.Series == "PHARM"
                             && i.Status != InvoiceStatus.Cancelled)
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => (Guid?)i.Id)
                    .FirstOrDefaultAsync(ct);
            }

            var invoice = new Invoice
            {
                Id = clientId ?? Guid.NewGuid(),
                TenantId = appointment.TenantId,
                AppointmentId = request.AppointmentId,
                PatientId = appointment.PatientId,
                FinancialYear = currentFy,
                Series = "INV",
                DocumentType = docType,
                LinkedPharmacyInvoiceId = linkedPharmacyId,
                Subtotal = calculatedSubtotal,
                GstAmount = calculatedGst,
                DiscountAmount = request.DiscountAmount,
                DiscountReason = request.DiscountReason,
                Total = total,
                PaidAmount = 0.0m,
                BalanceDue = total,
                Status = request.AutoFinalize ? InvoiceStatus.Finalized : InvoiceStatus.Draft,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            // If auto-finalizing, assign sequential gapless invoice number immediately
            if (request.AutoFinalize)
            {
                var maxNumber = await _db.Invoices
                    .Where(i => i.TenantId == tenantId && i.FinancialYear == currentFy && i.Series == invoice.Series)
                    .MaxAsync(i => (int?)i.InvoiceNumber, ct) ?? 0;
                invoice.InvoiceNumber = maxNumber + 1;
                invoice.FinalizedAt = DateTimeOffset.UtcNow;
                invoice.FinalizedByUserId = User.GetUserId();
                if (invoice.Total == 0) invoice.Status = InvoiceStatus.Paid;

                // Post append-only ledger entries
                InvoiceAccounting.PostFinalization(_db, invoice, User.GetUserId());
            }

            foreach (var lineItem in lineItemsToCreate)
            {
                lineItem.InvoiceId = invoice.Id;
                _db.InvoiceLineItems.Add(lineItem);
            }

            _db.Invoices.Add(invoice);
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            var invoiceNumStr = invoice.InvoiceNumber > 0
                ? invoice.FormatInvoiceNumber()
                : $"DRAFT-{invoice.Id:N}"[..14];

            return StatusCode(201, new
            {
                invoiceId = invoice.Id,
                invoiceNumber = invoiceNumStr,
                documentType = invoice.DocumentType.ToString(),
                subtotal = invoice.Subtotal,
                gstAmount = invoice.GstAmount,
                discountAmount = invoice.DiscountAmount,
                total = invoice.Total,
                paidAmount = invoice.PaidAmount,
                balanceDue = invoice.BalanceDue,
                status = invoice.Status.ToString().ToLower()
            });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && (InvoiceAccounting.IsIdempotencyConflict(update) || InvoiceAccounting.IsNumberingConflict(update)))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "invoice_changed_concurrently", message = "Retry with the same invoice key." });
        }
    }

    [HttpPost("invoices/{id:guid}/finalize")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    [RequirePermission(Permissions.InvoiceWrite)]
    public async Task<IActionResult> FinalizeInvoice(Guid id, CancellationToken ct)
    {
        var tenantId = TenantId;
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var invoice = await _db.Invoices
                .Include(i => i.LineItems)
                .Include(i => i.Appointment).ThenInclude(a => a!.Patient)
                .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

            if (invoice == null)
                return NotFound(new { error = "invoice_not_found" });

            if (invoice.Status != InvoiceStatus.Draft)
                return BadRequest(new { error = "invoice_already_finalized" });

            var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);
            var maxNumber = await _db.Invoices
                .Where(i => i.TenantId == tenantId && i.FinancialYear == currentFy && i.Series == invoice.Series)
                .MaxAsync(i => (int?)i.InvoiceNumber, ct) ?? 0;

            invoice.InvoiceNumber = maxNumber + 1;
            invoice.FinancialYear = currentFy;
            invoice.Status = InvoiceStatus.Finalized;
            invoice.FinalizedAt = DateTimeOffset.UtcNow;
            invoice.FinalizedByUserId = User.GetUserId();
            invoice.RecalculateBalance();
            if (invoice.BalanceDue == 0) invoice.Status = InvoiceStatus.Paid;
            invoice.UpdatedAt = DateTimeOffset.UtcNow;

            var invoiceRef = invoice.FormatInvoiceNumber();

            // Append-only financial ledger entries
            InvoiceAccounting.PostFinalization(_db, invoice, User.GetUserId());

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return Ok(new
            {
                invoiceId = invoice.Id,
                invoiceNumber = invoiceRef,
                status = invoice.Status.ToString().ToLowerInvariant(),
                total = invoice.Total,
                balanceDue = invoice.BalanceDue,
                finalizedAt = invoice.FinalizedAt
            });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && InvoiceAccounting.IsNumberingConflict(update))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "invoice_changed_concurrently", message = "Reload the invoice before retrying finalization." });
        }
    }

    [HttpGet("invoices/{id:guid}/print")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> PrintInvoice(Guid id, CancellationToken ct)
    {
        var selected = await SelectedBillingMembershipAsync(ct);
        if (selected is null) return Forbid();
        var tenantId = selected.TenantId;
        var invoice = await _db.Invoices
            .Include(i => i.LineItems)
            .Include(i => i.Payments)
            .Include(i => i.Appointment).ThenInclude(a => a!.Patient)
            .Include(i => i.Appointment).ThenInclude(a => a!.Doctor)
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId
                && (i.AppointmentId == null || i.Appointment != null && i.Appointment.TenantId == tenantId && i.Appointment.ClinicId == selected.ClinicId), ct);

        if (invoice == null)
            return NotFound(new { error = "invoice_not_found" });

        if (selected.Role == UserRole.Pharmacist && invoice.InvoiceType != InvoiceType.Pharmacy) return Forbid();

        if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled)
            return BadRequest(new { error = "invoice_not_finalized", message = "Finalize the invoice before printing a receipt." });

        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == selected.ClinicId && c.TenantId == tenantId, ct);
        RecordPrint(invoice, await NextPrintCopyAsync(invoice, ct));
        var watermark = invoice.PrintCount > 1 ? "DUPLICATE COPY" : "ORIGINAL";

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "invoice_changed_concurrently", message = "Retry printing after reloading the invoice." });
        }

        var invoiceRef = invoice.FormatInvoiceNumber();

        return Ok(new
        {
            invoiceId = invoice.Id,
            invoiceNumber = invoiceRef,
            documentType = invoice.DocumentType.ToString(),
            watermark,
            printCount = invoice.PrintCount,
            clinic = new
            {
                name = clinic?.Name,
                legalName = clinic?.LegalName,
                address = clinic?.Address,
                phone = clinic?.Phone,
                email = clinic?.Email
            },
            patient = new
            {
                name = invoice.Appointment?.Patient?.Name ?? invoice.WalkInCustomerName ?? "Walk-in Patient",
                uhid = invoice.Appointment?.Patient?.Uhid,
                phone = invoice.Appointment?.Patient?.Phone ?? invoice.WalkInCustomerPhone
            },
            doctor = new
            {
                name = invoice.Appointment?.Doctor?.Name
            },
            lineItems = invoice.LineItems.Select(li => new
            {
                description = li.Description,
                quantity = li.Quantity,
                unitPrice = li.UnitPrice,
                taxCategory = li.TaxCategory.ToString(),
                hsnSacCode = li.HsnSacCode,
                gstRate = li.GstRate,
                taxAmount = li.ResolvedTaxAmount(),
                discountAmount = li.DiscountAmount,
                taxableAmount = li.TaxableAmount(invoice.InvoiceType),
                totalAmount = li.GrossAmount(invoice.InvoiceType)
            }),
            subtotal = invoice.Subtotal,
            gstAmount = invoice.GstAmount,
            discountAmount = invoice.DiscountAmount,
            total = invoice.Total,
            paidAmount = invoice.PaidAmount,
            balanceDue = invoice.BalanceDue,
            status = invoice.Status.ToString().ToLower(),
            createdAt = invoice.CreatedAt,
            finalizedAt = invoice.FinalizedAt
        });
    }

    [HttpPost("invoices/{id:guid}/credit-notes")]
    [AuthorizeRoles("ClinicAdmin", "Owner")]
    public async Task<IActionResult> CreateCreditNote(Guid id, [FromBody] CreateCreditNoteRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

        if (invoice == null)
            return NotFound(new { error = "invoice_not_found" });

        if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled or InvoiceStatus.Refunded)
            return BadRequest(new { error = "invoice_not_finalized", message = "Credit notes can only be issued against a finalized invoice." });

        var taxProfile = await _db.TenantTaxProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        var creditNoteThreshold = taxProfile?.ThresholdCreditNoteApproval ?? 5000.0m;

        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);
        var series = "CN";
        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var alreadyCredited = await _db.CreditNotes
                .Where(c => c.InvoiceId == id && c.Status == CreditNoteStatus.Approved)
                .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
            var creditable = invoice.Total - alreadyCredited;

            if (req.Amount <= 0 || req.Amount > creditable)
                return BadRequest(new
                {
                    error = "invalid_credit_note_amount",
                    message = $"Credit note amount must be between ₹0.01 and ₹{creditable:F2} (already credited: ₹{alreadyCredited:F2})."
                });

            var maxSeq = await _db.CreditNotes
                .Where(c => c.TenantId == tenantId && c.FinancialYear == currentFy && c.Series == series)
                .MaxAsync(c => (int?)c.SequenceNumber, ct) ?? 0;

            if (maxSeq == 0)
            {
                var prefix = $"{series}-{currentFy}-";
                var lastNumber = await _db.CreditNotes
                    .Where(c => c.TenantId == tenantId && c.CreditNoteNumber.StartsWith(prefix))
                    .OrderByDescending(c => c.CreditNoteNumber)
                    .Select(c => c.CreditNoteNumber)
                    .FirstOrDefaultAsync(ct);
                if (lastNumber is not null && int.TryParse(lastNumber[prefix.Length..], out var lastParsedSeq))
                {
                    maxSeq = lastParsedSeq;
                }
            }

            var nextSequence = maxSeq + 1;
            var creditNoteNum = $"{series}-{currentFy}-{nextSequence:D6}";
            var currentUserId = User.GetUserId() ?? Guid.Empty;
            var now = DateTimeOffset.UtcNow;

            // Dual-control: amounts above threshold require second approval
            var requiresSecondApproval = req.Amount >= creditNoteThreshold;
            var initialStatus = requiresSecondApproval ? CreditNoteStatus.PendingApproval : CreditNoteStatus.Approved;

            var creditNote = new CreditNote
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                InvoiceId = id,
                CreditNoteNumber = creditNoteNum,
                FinancialYear = currentFy,
                Series = series,
                SequenceNumber = nextSequence,
                Amount = req.Amount,
                Reason = req.Reason.Trim(),
                ApprovedByUserId = currentUserId,
                SecondApprovedByUserId = requiresSecondApproval ? null : currentUserId,
                Status = initialStatus,
                IssuedAt = now,
                CreatedAt = now
            };

            _db.CreditNotes.Add(creditNote);

            // Reversal entry in append-only financial ledger (only if approved immediately)
            if (!requiresSecondApproval)
            {
                var invoiceRef = invoice.FormatInvoiceNumber();
                _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EntryType = FinanceEntryType.CreditNoteIssued,
                    ReferenceType = "CreditNote",
                    ReferenceId = creditNote.Id,
                    ReferenceNumber = creditNoteNum,
                    AccountCategory = "SalesReturnsAndAllowances",
                    DebitAmount = req.Amount,
                    CreditAmount = 0.0m,
                    Narration = $"Credit note {creditNoteNum} issued against {invoiceRef}. Reason: {req.Reason}",
                    PostedByUserId = currentUserId,
                    CreatedAt = now
                });

                _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EntryType = FinanceEntryType.CreditNoteIssued,
                    ReferenceType = "Invoice",
                    ReferenceId = invoice.Id,
                    ReferenceNumber = invoiceRef,
                    AccountCategory = "AccountsReceivable",
                    DebitAmount = 0.0m,
                    CreditAmount = req.Amount,
                    Narration = $"Receivable reduced by {creditNoteNum}",
                    PostedByUserId = currentUserId,
                    CreatedAt = now
                });

                // Adjust invoice balance
                invoice.CreditedAmount += req.Amount;
                invoice.RecalculateBalance();
                invoice.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return StatusCode(201, new
            {
                creditNoteId = creditNote.Id,
                creditNoteNumber = creditNote.CreditNoteNumber,
                invoiceId = invoice.Id,
                amount = creditNote.Amount,
                reason = creditNote.Reason,
                status = creditNote.Status.ToString().ToLower(),
                requiresSecondApproval,
                newBalanceDue = invoice.BalanceDue,
                issuedAt = creditNote.IssuedAt
            });
        });
    }

    [HttpPost("credit-notes/{creditNoteId:guid}/approve")]
    [AuthorizeRoles("ClinicAdmin", "Owner")]
    public async Task<IActionResult> ApproveCreditNote(Guid creditNoteId, [FromBody] ApproveCreditNoteRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var creditNote = await _db.CreditNotes
            .Include(c => c.Invoice)
            .FirstOrDefaultAsync(c => c.Id == creditNoteId && c.TenantId == tenantId, ct);

        if (creditNote == null)
            return NotFound(new { error = "credit_note_not_found" });

        if (creditNote.Status != CreditNoteStatus.PendingApproval)
            return BadRequest(new { error = "credit_note_not_pending", message = "This credit note is not pending approval." });

        var currentUserId = User.GetUserId() ?? Guid.Empty;
        if (currentUserId == creditNote.ApprovedByUserId)
            return BadRequest(new { error = "cannot_self_approve", message = "The original approver cannot provide the second approval." });

        var now = DateTimeOffset.UtcNow;

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // Re-check the credit cap at approval time: an earlier note may have been approved in between.
        var alreadyCredited = await _db.CreditNotes
            .Where(c => c.InvoiceId == creditNote.InvoiceId && c.Id != creditNote.Id && c.Status == CreditNoteStatus.Approved)
            .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        if (alreadyCredited + creditNote.Amount > creditNote.Invoice.Total)
            return BadRequest(new
            {
                error = "invalid_credit_note_amount",
                message = $"Approving this note would exceed the invoice total (already credited: ₹{alreadyCredited:F2})."
            });

        creditNote.Status = CreditNoteStatus.Approved;
        creditNote.SecondApprovedByUserId = currentUserId;
        creditNote.IssuedAt = now;

        // Post reversal entries to ledger now that it's approved
        var invoiceRef = creditNote.Invoice.FormatInvoiceNumber();
        _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntryType = FinanceEntryType.CreditNoteIssued,
            ReferenceType = "CreditNote",
            ReferenceId = creditNote.Id,
            ReferenceNumber = creditNote.CreditNoteNumber,
            AccountCategory = "SalesReturnsAndAllowances",
            DebitAmount = creditNote.Amount,
            CreditAmount = 0.0m,
            Narration = $"Credit note {creditNote.CreditNoteNumber} approved against {invoiceRef}. Reason: {creditNote.Reason}",
            PostedByUserId = currentUserId,
            CreatedAt = now
        });

        _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntryType = FinanceEntryType.CreditNoteIssued,
            ReferenceType = "Invoice",
            ReferenceId = creditNote.InvoiceId,
            ReferenceNumber = invoiceRef,
            AccountCategory = "AccountsReceivable",
            DebitAmount = 0.0m,
            CreditAmount = creditNote.Amount,
            Narration = $"Receivable reduced by {creditNote.CreditNoteNumber}",
            PostedByUserId = currentUserId,
            CreatedAt = now
        });

        // Adjust invoice balance
        creditNote.Invoice.CreditedAmount += creditNote.Amount;
        creditNote.Invoice.RecalculateBalance();
        creditNote.Invoice.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(new
        {
            creditNoteId = creditNote.Id,
            creditNoteNumber = creditNote.CreditNoteNumber,
            invoiceId = creditNote.InvoiceId,
            amount = creditNote.Amount,
            status = creditNote.Status.ToString().ToLower(),
            secondApprovedByUserId = currentUserId,
            approvedAt = now,
            newBalanceDue = creditNote.Invoice.BalanceDue
        });
    }

    [HttpPost("credit-notes/{creditNoteId:guid}/reject")]
    [AuthorizeRoles("ClinicAdmin", "Owner")]
    public async Task<IActionResult> RejectCreditNote(Guid creditNoteId, [FromBody] RejectCreditNoteRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var creditNote = await _db.CreditNotes
            .FirstOrDefaultAsync(c => c.Id == creditNoteId && c.TenantId == tenantId, ct);

        if (creditNote == null)
            return NotFound(new { error = "credit_note_not_found" });

        if (creditNote.Status != CreditNoteStatus.PendingApproval)
            return BadRequest(new { error = "credit_note_not_pending", message = "This credit note is not pending approval." });

        var currentUserId = User.GetUserId() ?? Guid.Empty;
        if (currentUserId == creditNote.ApprovedByUserId)
            return BadRequest(new { error = "cannot_self_reject", message = "The original approver cannot reject their own credit note." });

        creditNote.Status = CreditNoteStatus.Rejected;
        creditNote.SecondApprovedByUserId = currentUserId;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            creditNoteId = creditNote.Id,
            creditNoteNumber = creditNote.CreditNoteNumber,
            status = creditNote.Status.ToString().ToLower(),
            rejectedByUserId = currentUserId,
            rejectedAt = DateTimeOffset.UtcNow
        });
    }

    [HttpPost("invoices/{id:guid}/refund")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    [RequirePermission(Permissions.PaymentRecord)]
    public Task<IActionResult> ProcessRefund(Guid id, [FromBody] CreateRefundRequest req, CancellationToken ct) =>
        ProcessRefundAsync(id, req, false, ct);

    internal Task<IActionResult> ProcessPharmacyReturnAsync(Guid id, ReturnRequest req, CancellationToken ct) =>
        ProcessRefundAsync(id, new(0m, PaymentMethod.Cash, req.RefundReference, req.Reason ?? "", req.IdempotencyKey, req.SourcePaymentId, req.RefundCreditNoteId, req.RefundNonConsiderationReason), true, ct);

    private async Task<IActionResult> ProcessRefundAsync(Guid id, CreateRefundRequest req, bool pharmacyReturn, CancellationToken ct)
    {
        var tenantId = TenantId;
        if (tenantId == Guid.Empty || User.GetUserId() is not { } userId || userId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });
        if (req == null || string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 128)
            return BadRequest(new { error = "refund_key_required", message = "Use one stable key of at most 128 characters for this refund and all its retries." });
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length > 500)
            return BadRequest(new { error = "refund_reason_required" });
        if (req.RefundReference?.Length > 100)
            return BadRequest(new { error = "invalid_refund_reference" });
        req = req with { Reason = req.Reason.Trim(), RefundReference = string.IsNullOrWhiteSpace(req.RefundReference) ? null : req.RefundReference.Trim() };

        bool hasCn = req.RefundCreditNoteId.HasValue && req.RefundCreditNoteId.Value != Guid.Empty;
        bool hasReason = !string.IsNullOrWhiteSpace(req.RefundNonConsiderationReason);

        // Tax resolution requirement for new refunds is enforced inside when existing == null

        if (hasCn && hasReason)
            return BadRequest(new { error = "conflicting_refund_tax_resolution", message = "Specify either a credit note or a non-consideration reason, not both." });

        if (hasReason)
        {
            var trimmedReason = req.RefundNonConsiderationReason!.Trim();
            if (trimmedReason.Length > 500)
                return BadRequest(new { error = "invalid_non_consideration_reason", message = "Non-consideration reason cannot exceed 500 characters." });
            if (string.Equals(trimmedReason, req.Reason.Trim(), StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "invalid_non_consideration_reason", message = "Non-consideration reason must be an explicit explanation of why tax does not adjust, not a restatement of the operational refund reason." });
            req = req with { RefundNonConsiderationReason = trimmedReason };
        }

        Guid refundId;
        var created = false;
        await using (var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null)
        {
            try
            {
                var invoice = await _db.Invoices.Include(i => i.Payments).Include(i => i.LineItems)
                    .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
                if (invoice == null) return NotFound(new { error = "invoice_not_found" });
                await _db.Entry(invoice).ReloadAsync(ct);
                var payments = await _db.Payments.AsNoTracking().Where(p => p.InvoiceId == id && p.TenantId == tenantId).ToListAsync(ct);
                var existing = await _db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == req.IdempotencyKey, ct);
                if (pharmacyReturn)
                {
                    if (invoice.InvoiceType != InvoiceType.Pharmacy) return BadRequest(new { error = "not_a_pharmacy_invoice" });
                    var methods = payments.Where(IsCollectedPayment).Select(p => p.Method).Distinct().ToList();
                    var source = payments.FirstOrDefault(p => p.Id == req.SourcePaymentId && IsCollectedPayment(p));
                    if (existing?.IsPharmacyReturn == true) req = req with { Amount = -existing.Amount, Method = existing.Method };
                    else if (req.SourcePaymentId.HasValue && source == null) return BadRequest(new { error = "invalid_refund_source" });
                    else if (source == null && methods.Count != 1) return BadRequest(new { error = "refund_source_required" });
                    else req = req with { Amount = invoice.Total, Method = source?.Method ?? methods[0] };
                }
                if (req.Amount is <= 0 or > 1000000m || decimal.Round(req.Amount, 2) != req.Amount)
                    return BadRequest(new { error = "invalid_refund_amount" });
                if (req.Method is not (PaymentMethod.Cash or PaymentMethod.UPI or PaymentMethod.Card or PaymentMethod.NetBanking or PaymentMethod.Razorpay))
                    return BadRequest(new { error = "invalid_refund_method" });
                if (req.Method == PaymentMethod.Razorpay && req.RefundReference != null)
                    return BadRequest(new { error = "provider_refund_reference_is_server_assigned" });
                if (req.Method != PaymentMethod.Razorpay && req.RefundReference == null)
                    return BadRequest(new { error = "refund_reference_required", message = "Record the actual payout UTR or cash voucher, not a generated payment identifier." });
                if (existing != null)
                {
                    if (existing.InvoiceId != id || existing.Amount != -req.Amount || existing.Method != req.Method
                        || existing.IsPharmacyReturn != pharmacyReturn || existing.RefundReason != req.Reason
                        || req.Method != PaymentMethod.Razorpay && existing.RefundReference != req.RefundReference
                        || req.SourcePaymentId.HasValue && existing.RefundSourcePaymentId != req.SourcePaymentId
                        || (hasCn && existing.RefundCreditNoteId != req.RefundCreditNoteId)
                        || (hasReason && existing.RefundNonConsiderationReason != req.RefundNonConsiderationReason))
                        return Conflict(new { error = "refund_key_conflict" });
                    refundId = existing.Id;
                }
                else
                {
                    if (!hasCn && !hasReason)
                        return BadRequest(new { error = "refund_tax_resolution_required", message = "Select an approved credit note or record why this refund is not a consideration adjustment." });
                    if (hasCn)
                    {
                        var cn = await _db.CreditNotes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.RefundCreditNoteId && c.TenantId == tenantId && c.InvoiceId == id, ct);
                        if (cn == null)
                            return BadRequest(new { error = "invalid_refund_credit_note", message = "The selected credit note was not found for this invoice." });
                        if (cn.Status == CreditNoteStatus.Rejected)
                            return BadRequest(new { error = "credit_note_rejected", message = "A rejected credit note cannot be linked to a refund." });
                    }
                    if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled or InvoiceStatus.Refunded)
                        return BadRequest(new { error = "invoice_not_refundable" });
                    if (pharmacyReturn && (invoice.Status != InvoiceStatus.Paid || invoice.PaidAmount != invoice.Total))
                        return BadRequest(new { error = "invoice_not_paid", message = "A full pharmacy return requires a fully collected invoice." });
                    if (req.Amount > RefundableBalance(invoice, payments, null, true))
                        return BadRequest(new { error = "refund_exceeds_paid_balance" });
                    Payment? source = null;
                    if (req.SourcePaymentId.HasValue)
                        source = payments.FirstOrDefault(p => p.Id == req.SourcePaymentId && IsCollectedPayment(p));
                    else if (req.Method == PaymentMethod.Razorpay)
                    {
                        var candidates = payments.Where(p => IsCollectedPayment(p) && p.Method == PaymentMethod.Razorpay
                            && !string.IsNullOrWhiteSpace(p.RazorpayPaymentId) && SourceRefundableBalance(payments, p, null, true) >= req.Amount).ToList();
                        if (candidates.Count != 1) return BadRequest(new { error = "captured_refund_source_required" });
                        source = candidates[0];
                    }
                    if (req.SourcePaymentId.HasValue && source == null || source != null && SourceRefundableBalance(payments, source, null, true) < req.Amount)
                        return BadRequest(new { error = "invalid_or_exhausted_refund_source" });
                    if (req.Method == PaymentMethod.Razorpay && (source?.Method != PaymentMethod.Razorpay || string.IsNullOrWhiteSpace(source.RazorpayPaymentId)))
                        return BadRequest(new { error = "captured_refund_source_required" });
                    if (req.Method == PaymentMethod.Razorpay && !_razorpay.IsConfigured)
                        return StatusCode(503, new { error = "razorpay_not_configured" });
                    if (pharmacyReturn && await ValidateReturnStockAsync(invoice, ct) is { } stockError)
                        return BadRequest(new { error = stockError.Error, message = stockError.Message });
                    var threshold = await _db.TenantTaxProfiles.Where(p => p.TenantId == tenantId)
                        .Select(p => (decimal?)p.ThresholdRefundApproval).FirstOrDefaultAsync(ct) ?? 1000m;
                    var refund = new Payment
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        InvoiceId = id,
                        Amount = -req.Amount,
                        Method = req.Method,
                        Status = PaymentStatus.Pending,
                        VerificationStatus = req.Amount > threshold ? PaymentVerificationStatus.Pending : PaymentVerificationStatus.Confirmed,
                        ConfirmedByUserId = userId,
                        RefundReason = req.Reason,
                        RefundReference = req.RefundReference,
                        RefundSourcePaymentId = source?.Id,
                        RefundCreditNoteId = req.RefundCreditNoteId,
                        RefundNonConsiderationReason = req.RefundNonConsiderationReason,
                        RazorpayPaymentId = req.Method == PaymentMethod.Razorpay ? source?.RazorpayPaymentId : null,
                        IsPharmacyReturn = pharmacyReturn,
                        IdempotencyKey = req.IdempotencyKey,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    _db.Payments.Add(refund);
                    invoice.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    refundId = refund.Id;
                    created = true;
                }
                if (transaction != null) await transaction.CommitAsync(ct);
            }
            catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex) || ex is DbUpdateException update && InvoiceAccounting.IsIdempotencyConflict(update))
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return Conflict(new { error = "refund_changed_concurrently", message = "Reload and retry with the same refund key." });
            }
        }

        return await CompleteRefundAsync(id, refundId, created, ct);
    }

    [HttpPost("invoices/{id:guid}/refunds/{refundId:guid}/approve")]
    [AuthorizeRoles("ClinicAdmin")]
    [RequirePermission(Permissions.PaymentRecord)]
    public async Task<IActionResult> ApproveRefund(Guid id, Guid refundId, [FromBody] ApproveRefundRequest? req, CancellationToken ct)
    {
        if (TenantId == Guid.Empty || User.GetUserId() is not { } userId || userId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });
        await using (var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null)
        {
            try
            {
                var invoice = await _db.Invoices.Include(i => i.Payments)
                    .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == TenantId, ct);
                if (invoice == null) return NotFound(new { error = "invoice_not_found" });
                await _db.Entry(invoice).ReloadAsync(ct);
                var refund = invoice.Payments.FirstOrDefault(p => p.Id == refundId && p.TenantId == TenantId);
                if (refund == null) return NotFound(new { error = "refund_not_found" });
                await _db.Entry(refund).ReloadAsync(ct);
                if (refund.Amount >= 0 || string.IsNullOrWhiteSpace(refund.RefundReason))
                    return BadRequest(new { error = "not_a_refund" });
                if (refund.ConfirmedByUserId == userId)
                    return RefundResult(refund, invoice, httpStatus: 400, error: "dual_approval_self_approval_blocked");
                if (refund.SecondApprovedByUserId.HasValue && refund.SecondApprovedByUserId != userId)
                    return RefundResult(refund, invoice, httpStatus: 409, error: "refund_approval_conflict");
                if (!refund.SecondApprovedByUserId.HasValue)
                {
                    if (refund.Status != PaymentStatus.Pending || refund.VerificationStatus != PaymentVerificationStatus.Pending)
                        return RefundResult(refund, invoice, httpStatus: 400, error: "refund_not_pending_approval");
                    var payments = await _db.Payments.AsNoTracking().Where(p => p.InvoiceId == id && p.TenantId == TenantId).ToListAsync(ct);
                    if (-refund.Amount > RefundableBalance(invoice, payments, refund.Id, false))
                        return RefundResult(refund, invoice, httpStatus: 400, error: "refund_exceeds_paid_balance");
                    refund.SecondApprovedByUserId = userId;
                    refund.VerificationStatus = PaymentVerificationStatus.Confirmed;
                    invoice.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                if (transaction != null) await transaction.CommitAsync(ct);
            }
            catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(ct);
                    await transaction.DisposeAsync();
                }
                _db.ChangeTracker.Clear();
                return await SavedRefundErrorAsync(id, refundId, 409, "refund_changed_concurrently", "Reload and retry the same approval.", ct);
            }
        }
        return await CompleteRefundAsync(id, refundId, false, ct);
    }

    private static bool IsCollectedPayment(Payment payment) => payment.Amount > 0 && payment.Status == PaymentStatus.Completed
        && payment.VerificationStatus == PaymentVerificationStatus.Confirmed && payment.Method != PaymentMethod.AdvanceDeposit;

    private static decimal RefundableBalance(Invoice invoice, IEnumerable<Payment> payments, Guid? excludeId, bool includePending)
    {
        var available = payments.Where(IsCollectedPayment).Sum(p => p.Amount)
            + payments.Where(p => p.Amount < 0 && p.Id != excludeId
                && (p.Status == PaymentStatus.Refunded || includePending && p.Status == PaymentStatus.Pending)).Sum(p => p.Amount);
        return Math.Max(0m, Math.Min(invoice.PaidAmount, available));
    }

    private static decimal SourceRefundableBalance(IEnumerable<Payment> payments, Payment source, Guid? excludeId, bool includePending) =>
        Math.Max(0m, source.Amount + payments.Where(p => p.Amount < 0 && p.Id != excludeId
            && (p.Status == PaymentStatus.Refunded || includePending && p.Status == PaymentStatus.Pending)
            && (p.RefundSourcePaymentId == source.Id || !p.RefundSourcePaymentId.HasValue)).Sum(p => p.Amount));

    private static string RefundState(Payment refund) =>
        refund.Status == PaymentStatus.Refunded && refund.VerificationStatus == PaymentVerificationStatus.Confirmed ? "refunded"
            : refund.Status == PaymentStatus.Failed ? "failed"
            : refund.VerificationStatus == PaymentVerificationStatus.Pending ? "pending_approval" : "processing";

    private IActionResult RefundResult(Payment refund, Invoice? invoice, bool created = false,
        int? httpStatus = null, string? error = null, string? message = null)
    {
        var status = RefundState(refund);
        var data = new
        {
            refundId = refund.Id,
            invoiceId = refund.InvoiceId,
            invoiceNumber = invoice?.FormatInvoiceNumber(),
            amount = -refund.Amount,
            method = refund.Method.ToString(),
            refundReference = refund.RefundReference,
            status,
            verificationStatus = status == "refunded" ? "confirmed" : status == "failed" ? "rejected" : "pending",
            paidAmount = invoice?.PaidAmount,
            balanceDue = invoice?.BalanceDue,
            secondApprovedByUserId = refund.SecondApprovedByUserId,
            idempotencyKey = refund.IdempotencyKey,
            refundReason = refund.RefundReason,
            refundCreditNoteId = refund.RefundCreditNoteId,
            refundNonConsiderationReason = refund.RefundNonConsiderationReason,
            refundSourcePaymentId = refund.RefundSourcePaymentId,
            confirmedByUserId = refund.ConfirmedByUserId,
            isPharmacyReturn = refund.IsPharmacyReturn,
            refundedAt = refund.PaidAt,
            stockRestored = refund.IsPharmacyReturn && status == "refunded",
            error,
            message
        };
        if (httpStatus == 400) return BadRequest(data);
        if (httpStatus == 409) return Conflict(data);
        if (httpStatus.HasValue) return StatusCode(httpStatus.Value, data);
        return status is "pending_approval" or "processing" ? StatusCode(202, data)
            : status == "failed" ? StatusCode(502, data) : created ? StatusCode(201, data) : Ok(data);
    }

    private async Task<IActionResult> SavedRefundErrorAsync(Guid invoiceId, Guid refundId, int httpStatus,
        string error, string? message, CancellationToken ct)
    {
        var refund = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == refundId && p.InvoiceId == invoiceId && p.TenantId == TenantId, ct);
        if (refund == null) return NotFound(new { error = "refund_not_found" });
        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == TenantId, ct);
        return RefundResult(refund, invoice, httpStatus: httpStatus, error: error, message: message);
    }

    private async Task<IActionResult> CompleteRefundAsync(Guid invoiceId, Guid refundId, bool created, CancellationToken ct)
    {
        var intent = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == refundId && p.InvoiceId == invoiceId && p.TenantId == TenantId, ct);
        if (intent == null) return NotFound(new { error = "refund_not_found" });
        if (intent.Amount is >= 0 or < -1000000m || decimal.Round(intent.Amount, 2) != intent.Amount
            || string.IsNullOrWhiteSpace(intent.RefundReason)
            || intent.Method is not (PaymentMethod.Cash or PaymentMethod.UPI or PaymentMethod.Card or PaymentMethod.NetBanking or PaymentMethod.Razorpay))
            return BadRequest(new { error = "not_a_valid_refund" });
        if (intent.Status != PaymentStatus.Pending || intent.VerificationStatus != PaymentVerificationStatus.Confirmed)
            return RefundResult(intent, await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == TenantId, ct));

        if (!intent.RefundCreditNoteId.HasValue && string.IsNullOrWhiteSpace(intent.RefundNonConsiderationReason))
            return BadRequest(new { error = "refund_tax_resolution_required", message = "Select an approved credit note or record why this refund is not a consideration adjustment." });

        if (intent.RefundCreditNoteId.HasValue && !string.IsNullOrWhiteSpace(intent.RefundNonConsiderationReason))
            return BadRequest(new { error = "conflicting_refund_tax_resolution", message = "Specify either a credit note or a non-consideration reason, not both." });
        if (intent.RefundCreditNoteId.HasValue)
        {
            var cn = await _db.CreditNotes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == intent.RefundCreditNoteId.Value && c.TenantId == TenantId && c.InvoiceId == invoiceId, ct);
            if (cn == null)
                return BadRequest(new { error = "invalid_refund_credit_note", message = "The selected credit note was not found for this invoice." });
            if (cn.Status != CreditNoteStatus.Approved)
                return BadRequest(new { error = "credit_note_not_approved", message = "The selected credit note must be approved before completing this refund." });
        }

        RazorpayRefund? providerRefund = null;
        if (intent.Method == PaymentMethod.Razorpay)
        {
            if (!_razorpay.IsConfigured) return await SavedRefundErrorAsync(invoiceId, refundId, 503, "razorpay_not_configured", null, ct);
            if (intent.RefundSourcePaymentId == null || string.IsNullOrWhiteSpace(intent.RazorpayPaymentId))
                return await SavedRefundErrorAsync(invoiceId, refundId, 400, "captured_refund_source_required", null, ct);
            var collected = await _db.Payments.AsNoTracking().Where(p => p.TenantId == TenantId && p.InvoiceId == invoiceId).ToListAsync(ct);
            var source = collected.FirstOrDefault(p => p.Id == intent.RefundSourcePaymentId && IsCollectedPayment(p));
            var bill = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == TenantId, ct);
            if (source?.Method != PaymentMethod.Razorpay || source.RazorpayPaymentId != intent.RazorpayPaymentId
                || bill == null || bill.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled
                || -intent.Amount > SourceRefundableBalance(collected, source, intent.Id, false)
                || -intent.Amount > RefundableBalance(bill, collected, intent.Id, false))
                return RefundResult(intent, bill, httpStatus: 400, error: "invalid_or_exhausted_refund_source");
            // External HTTP is outside database retries; a lost response reuses this durable intent's exact key/body.
            try
            {
                providerRefund = intent.RefundReference == null
                    ? await _razorpay.CreateRefundAsync(intent.RazorpayPaymentId, -intent.Amount, intent.Id, ct)
                    : await _razorpay.GetRefundAsync(intent.RefundReference, intent.RazorpayPaymentId, -intent.Amount, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
            if (providerRefund == null)
                return RefundResult(intent, await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == TenantId, ct));
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var invoice = await _db.Invoices.Include(i => i.LineItems)
                .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == TenantId, ct);
            var refund = await _db.Payments.FirstOrDefaultAsync(p => p.Id == refundId && p.InvoiceId == invoiceId && p.TenantId == TenantId, ct);
            if (invoice == null || refund == null) return NotFound(new { error = "refund_not_found" });
            await _db.Entry(invoice).ReloadAsync(ct);
            await _db.Entry(refund).ReloadAsync(ct);
            if (refund.Status != PaymentStatus.Pending || refund.VerificationStatus != PaymentVerificationStatus.Confirmed)
                return RefundResult(refund, invoice);
            if (refund.Amount >= 0 || refund.Method != intent.Method || refund.Amount != intent.Amount
                || refund.RefundSourcePaymentId != intent.RefundSourcePaymentId || refund.RazorpayPaymentId != intent.RazorpayPaymentId
                || refund.RefundCreditNoteId != intent.RefundCreditNoteId || refund.RefundNonConsiderationReason != intent.RefundNonConsiderationReason)
                return RefundResult(refund, invoice, httpStatus: 409, error: "refund_intent_changed");
            if (refund.RefundCreditNoteId.HasValue)
            {
                var cn = await _db.CreditNotes.FirstOrDefaultAsync(c => c.Id == refund.RefundCreditNoteId.Value && c.TenantId == TenantId && c.InvoiceId == invoiceId, ct);
                if (cn == null || cn.Status != CreditNoteStatus.Approved)
                    return RefundResult(refund, invoice, httpStatus: 400, error: "credit_note_not_approved", message: "The selected credit note must be approved before completing this refund.");
            }
            if (providerRefund != null)
            {
                if (refund.RefundReference != null && refund.RefundReference != providerRefund.Id)
                    return RefundResult(refund, invoice, httpStatus: 409, error: "provider_refund_identity_conflict");
                refund.RefundReference = providerRefund.Id;
                if (providerRefund.Status == "failed")
                {
                    refund.Status = PaymentStatus.Failed;
                    refund.VerificationStatus = PaymentVerificationStatus.Rejected;
                }
                if (providerRefund.Status != "processed")
                {
                    invoice.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    if (transaction != null) await transaction.CommitAsync(ct);
                    return RefundResult(refund, invoice);
                }
            }
            if (refund.Method != PaymentMethod.Razorpay && string.IsNullOrWhiteSpace(refund.RefundReference))
                return await SavedRefundErrorAsync(invoiceId, refundId, 400, "refund_reference_required", null, ct);
            var payments = await _db.Payments.AsNoTracking().Where(p => p.InvoiceId == invoiceId && p.TenantId == TenantId).ToListAsync(ct);
            if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled || -refund.Amount > RefundableBalance(invoice, payments, refund.Id, false))
                return await SavedRefundErrorAsync(invoiceId, refundId, 400, "refund_exceeds_paid_balance", "Reconcile collected funds before retrying this reserved payout.", ct);
            if (refund.RefundSourcePaymentId.HasValue)
            {
                var source = payments.FirstOrDefault(p => p.Id == refund.RefundSourcePaymentId && IsCollectedPayment(p));
                if (source == null || -refund.Amount > SourceRefundableBalance(payments, source, refund.Id, false)
                    || refund.Method == PaymentMethod.Razorpay && (source.Method != PaymentMethod.Razorpay || source.RazorpayPaymentId != refund.RazorpayPaymentId))
                    return await SavedRefundErrorAsync(invoiceId, refundId, 400, "invalid_or_exhausted_refund_source", null, ct);
            }
            if (refund.IsPharmacyReturn)
            {
                if (invoice.InvoiceType != InvoiceType.Pharmacy || invoice.Total != -refund.Amount || invoice.PaidAmount != invoice.Total)
                    return await SavedRefundErrorAsync(invoiceId, refundId, 400, "return_invoice_changed", null, ct);
                if (await ValidateReturnStockAsync(invoice, ct) is { } stockError)
                    return await SavedRefundErrorAsync(invoiceId, refundId, 400, stockError.Error, stockError.Message, ct);
                await RestoreReturnStockAsync(invoice, refund, ct);
            }
            var now = DateTimeOffset.UtcNow;
            refund.Status = PaymentStatus.Refunded;
            refund.VerificationStatus = PaymentVerificationStatus.Confirmed;
            refund.PaidAt = refund.ConfirmedAt = now;
            invoice.PaidAmount += refund.Amount;
            invoice.RecalculateBalance();
            invoice.Status = invoice.PaidAmount <= 0 ? InvoiceStatus.Refunded : InvoiceStatus.PartiallyPaid;
            invoice.UpdatedAt = now;
            var userId = User.GetUserId();
            var invoiceRef = invoice.FormatInvoiceNumber();
            _db.FinanceLedgerEntries.AddRange(
                new FinanceLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantId,
                    EntryType = FinanceEntryType.RefundProcessed,
                    ReferenceType = "Refund",
                    ReferenceId = refund.Id,
                    ReferenceNumber = refund.RefundReference!,
                    AccountCategory = refund.Method == PaymentMethod.Cash ? "Cash" : "Bank",
                    CreditAmount = -refund.Amount,
                    Narration = $"Refund for {invoiceRef}: {refund.RefundReason}",
                    PostedByUserId = userId,
                    CreatedAt = now
                },
                new FinanceLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantId,
                    EntryType = FinanceEntryType.RefundProcessed,
                    ReferenceType = "Invoice",
                    ReferenceId = invoice.Id,
                    ReferenceNumber = invoiceRef,
                    AccountCategory = "AccountsReceivable",
                    DebitAmount = -refund.Amount,
                    Narration = $"Receivable reinstatement from refund {refund.Id:N}",
                    PostedByUserId = userId,
                    CreatedAt = now
                });
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return RefundResult(refund, invoice, created);
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(ct);
                await transaction.DisposeAsync();
            }
            _db.ChangeTracker.Clear();
            return await SavedRefundErrorAsync(invoiceId, refundId, 409, "refund_changed_concurrently", "Retry the same refund key; do not create another payout.", ct);
        }
    }

    private async Task<(string Error, string Message)?> ValidateReturnStockAsync(Invoice invoice, CancellationToken ct)
    {
        if (invoice.LineItems.Count == 0 || invoice.LineItems.Any(l => l.Quantity <= 0 || !l.DrugBatchId.HasValue))
            return ("return_stock_lineage_required", "Reconcile the original sale's batch and quantity before returning stock.");
        var quantities = invoice.LineItems.GroupBy(l => l.DrugBatchId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => (long)l.Quantity));
        var batches = await _db.DrugBatches.AsNoTracking().Where(b => b.TenantId == invoice.TenantId
            && _db.InvoiceLineItems.Any(l => l.InvoiceId == invoice.Id && l.DrugBatchId == b.Id)).ToListAsync(ct);
        if (batches.Count != quantities.Count || batches.Any(b => b.QuantityRemaining < 0 || b.QuantityRemaining + quantities[b.Id] > b.QuantityReceived))
            return ("return_stock_reconciliation_required", "Returned quantities exceed the traceable batch allowance or a batch is inaccessible.");
        return null;
    }

    private async Task RestoreReturnStockAsync(Invoice invoice, Payment refund, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = User.GetUserId()!.Value;
        foreach (var group in invoice.LineItems.GroupBy(l => l.DrugBatchId!.Value))
        {
            var batch = await _db.DrugBatches.FirstAsync(b => b.Id == group.Key && b.TenantId == invoice.TenantId, ct);
            await _db.Entry(batch).ReloadAsync(ct);
            var quantity = group.Sum(l => l.Quantity);
            if ((long)batch.QuantityRemaining + quantity > batch.QuantityReceived)
                throw new DbUpdateConcurrencyException("The returned batch changed; retry the same refund.");
            batch.QuantityRemaining += quantity;
            batch.UpdatedAt = now;
            _db.DrugStockMovements.Add(new DrugStockMovement
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                DrugId = batch.DrugId,
                DrugBatchId = batch.Id,
                Quantity = quantity,
                Direction = MovementDirection.In,
                ReferenceType = "Return",
                ReferenceId = refund.Id,
                Note = $"Return for {invoice.FormatInvoiceNumber()}: {refund.RefundReason}",
                RecordedBy = userId,
                RecordedAt = now
            });
        }
        var registers = await _db.ControlledSubstanceRegisters.AsNoTracking().Where(r => r.TenantId == invoice.TenantId && r.Quantity > 0
            && (r.InvoiceId == invoice.Id || r.DispenseRecordId.HasValue && _db.DispenseRecords.Any(d =>
                d.TenantId == invoice.TenantId && d.InvoiceId == invoice.Id && d.Id == r.DispenseRecordId))).ToListAsync(ct);
        var name = await _db.Users.Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? userId.ToString();
        foreach (var register in registers)
            _db.ControlledSubstanceRegisters.Add(new ControlledSubstanceRegister
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                DispenseRecordId = register.DispenseRecordId,
                DrugId = register.DrugId,
                ScheduleClass = register.ScheduleClass,
                DrugName = register.DrugName,
                BatchNumber = register.BatchNumber,
                Quantity = -register.Quantity,
                PatientName = register.PatientName,
                PatientAddress = register.PatientAddress,
                PrescriberName = register.PrescriberName,
                PrescriberRegNo = register.PrescriberRegNo,
                DispensedBy = userId,
                DispenserName = name,
                DispensedAt = now
            });
    }

    [HttpGet("invoices/{id:guid}")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetInvoice(Guid id, CancellationToken ct)
    {
        var tenantId = TenantId;
        var invoice = await _db.Invoices
            .Include(i => i.LineItems)
            .Include(i => i.Payments)
            .Include(i => i.CreditNotes)
            .Include(i => i.Appointment).ThenInclude(a => a!.Patient)
            .Include(i => i.Appointment).ThenInclude(a => a!.Doctor)
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

        if (invoice == null)
            return NotFound(new { error = "invoice_not_found" });

        if (User.IsInRole("Pharmacist") && invoice.InvoiceType != InvoiceType.Pharmacy)
            return Forbid();

        var invoiceRef = invoice.FormatInvoiceNumber();
        var refundable = invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled or InvoiceStatus.Refunded
            ? 0m : RefundableBalance(invoice, invoice.Payments, null, true);

        Invoice? linkedPharmacy = null;
        if (invoice.LinkedPharmacyInvoiceId.HasValue)
        {
            linkedPharmacy = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == invoice.LinkedPharmacyInvoiceId.Value && i.TenantId == tenantId, ct);
        }

        return Ok(new
        {
            invoiceId = invoice.Id,
            invoiceNumber = invoiceRef,
            documentType = invoice.DocumentType.ToString(),
            invoiceType = invoice.InvoiceType.ToString(),
            appointmentId = invoice.AppointmentId,
            patient = new
            {
                id = invoice.Appointment?.Patient?.Id,
                name = invoice.Appointment?.Patient?.Name ?? invoice.WalkInCustomerName ?? "Walk-in Patient",
                uhid = invoice.Appointment?.Patient?.Uhid,
                phone = invoice.Appointment?.Patient?.Phone ?? invoice.WalkInCustomerPhone
            },
            doctor = new
            {
                id = invoice.Appointment?.Doctor?.Id,
                name = invoice.Appointment?.Doctor?.Name
            },
            lineItems = invoice.LineItems.Select(li => new
            {
                id = li.Id,
                description = li.Description,
                quantity = li.Quantity,
                unitPrice = li.UnitPrice,
                taxCategory = li.TaxCategory.ToString(),
                hsnSacCode = li.HsnSacCode,
                gstRate = li.GstRate,
                taxAmount = li.ResolvedTaxAmount(),
                discountAmount = li.DiscountAmount,
                taxableAmount = li.TaxableAmount(invoice.InvoiceType),
                totalAmount = li.GrossAmount(invoice.InvoiceType)
            }),
            payments = invoice.Payments.Select(p => new
            {
                id = p.Id,
                method = p.Method.ToString(),
                amount = p.Amount,
                utr = p.Utr,
                status = p.Status.ToString(),
                verificationStatus = p.VerificationStatus.ToString(),
                paidAt = p.PaidAt,
                createdAt = p.CreatedAt,
                reportedAt = p.ReportedAt,
                confirmedAt = p.ConfirmedAt,
                confirmedByUserId = p.ConfirmedByUserId,
                secondApprovedByUserId = p.SecondApprovedByUserId,
                idempotencyKey = p.IdempotencyKey,
                payeeAccountId = p.PayeeAccountId,
                razorpayPaymentId = p.RazorpayPaymentId,
                refundableAmount = IsCollectedPayment(p) ? Math.Min(refundable, SourceRefundableBalance(invoice.Payments, p, null, true)) : 0m,
                refundStatus = p.Amount < 0 ? RefundState(p) : null,
                refundReason = p.RefundReason,
                refundCreditNoteId = p.RefundCreditNoteId,
                refundNonConsiderationReason = p.RefundNonConsiderationReason,
                refundReference = p.RefundReference,
                refundSourcePaymentId = p.RefundSourcePaymentId,
                isPharmacyReturn = p.IsPharmacyReturn
            }),
            creditNotes = invoice.CreditNotes.Select(cn => new
            {
                id = cn.Id,
                creditNoteNumber = cn.CreditNoteNumber,
                amount = cn.Amount,
                reason = cn.Reason,
                status = cn.Status.ToString(),
                issuedAt = cn.IssuedAt
            }),
            linkedPharmacyInvoiceId = invoice.LinkedPharmacyInvoiceId,
            linkedPharmacyInvoice = linkedPharmacy == null ? null : new
            {
                id = linkedPharmacy.Id,
                invoiceNumber = linkedPharmacy.FormatInvoiceNumber(),
                series = linkedPharmacy.Series,
                financialYear = linkedPharmacy.FinancialYear,
                subtotal = linkedPharmacy.Subtotal,
                gstAmount = linkedPharmacy.GstAmount,
                total = linkedPharmacy.Total,
                paidAmount = linkedPharmacy.PaidAmount,
                balanceDue = linkedPharmacy.BalanceDue,
                status = linkedPharmacy.Status.ToString().ToLower(),
                createdAt = linkedPharmacy.CreatedAt
            },
            subtotal = invoice.Subtotal,
            gstAmount = invoice.GstAmount,
            discountAmount = invoice.DiscountAmount,
            total = invoice.Total,
            paidAmount = invoice.PaidAmount,
            balanceDue = invoice.BalanceDue,
            refundableAmount = refundable,
            printCount = invoice.PrintCount,
            status = invoice.Status.ToString().ToLower(),
            createdAt = invoice.CreatedAt,
            finalizedAt = invoice.FinalizedAt
        });
    }

    [HttpPost("invoices/{id:guid}/link-pharmacy")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> LinkPharmacyInvoice(Guid id, [FromBody] LinkPharmacyInvoiceRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (invoice == null)
            return NotFound(new { error = "invoice_not_found" });

        var pharmInvoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == req.PharmacyInvoiceId && i.TenantId == tenantId, ct);
        if (pharmInvoice == null)
            return NotFound(new { error = "pharmacy_invoice_not_found" });

        if (pharmInvoice.Series != "PHARM" && pharmInvoice.InvoiceType != InvoiceType.Pharmacy)
            return BadRequest(new { error = "not_a_pharmacy_invoice", message = "Linked invoice must be a pharmacy invoice (PHARM series)." });

        invoice.LinkedPharmacyInvoiceId = pharmInvoice.Id;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            invoiceId = invoice.Id,
            linkedPharmacyInvoiceId = pharmInvoice.Id,
            linkedPharmacyInvoiceNumber = pharmInvoice.FormatInvoiceNumber(),
            message = "pharmacy_invoice_linked"
        });
    }

    [HttpGet("invoices/{id:guid}/linked-documents")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetLinkedDocuments(Guid id, CancellationToken ct)
    {
        var tenantId = TenantId;
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (invoice == null)
            return NotFound(new { error = "invoice_not_found" });

        Invoice? linkedPharmacy = null;
        if (invoice.LinkedPharmacyInvoiceId.HasValue)
        {
            linkedPharmacy = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == invoice.LinkedPharmacyInvoiceId.Value && i.TenantId == tenantId, ct);
        }

        var linkedConsultationInvoices = await _db.Invoices
            .Where(i => i.TenantId == tenantId && i.LinkedPharmacyInvoiceId == invoice.Id)
            .Select(i => new
            {
                id = i.Id,
                invoiceNumber = i.FormatInvoiceNumber(),
                series = i.Series,
                financialYear = i.FinancialYear,
                subtotal = i.Subtotal,
                gstAmount = i.GstAmount,
                total = i.Total,
                paidAmount = i.PaidAmount,
                balanceDue = i.BalanceDue,
                status = i.Status.ToString().ToLower(),
                createdAt = i.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new
        {
            invoiceId = invoice.Id,
            invoiceNumber = invoice.FormatInvoiceNumber(),
            series = invoice.Series,
            linkedPharmacyInvoice = linkedPharmacy == null ? null : new
            {
                id = linkedPharmacy.Id,
                invoiceNumber = linkedPharmacy.FormatInvoiceNumber(),
                series = linkedPharmacy.Series,
                financialYear = linkedPharmacy.FinancialYear,
                subtotal = linkedPharmacy.Subtotal,
                gstAmount = linkedPharmacy.GstAmount,
                total = linkedPharmacy.Total,
                paidAmount = linkedPharmacy.PaidAmount,
                balanceDue = linkedPharmacy.BalanceDue,
                status = linkedPharmacy.Status.ToString().ToLower(),
                createdAt = linkedPharmacy.CreatedAt
            },
            linkedConsultationInvoices
        });
    }

    [HttpPatch("invoices/{id:guid}")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    [RequirePermission(Permissions.InvoiceWrite)]
    public async Task<IActionResult> UpdateInvoiceDraft(Guid id, [FromBody] UpdateInvoiceDraftRequest request, CancellationToken ct)
    {
        var tenantId = TenantId;
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var invoice = await _db.Invoices
                .Include(i => i.LineItems)
                .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

            if (invoice == null)
                return NotFound(new { error = "invoice_not_found" });

            if (invoice.Status != InvoiceStatus.Draft)
                return BadRequest(new { error = "invoice_not_draft", message = "Only draft invoices can be updated." });

            List<InvoiceLineItem>? newLineItems = null;
            var subtotal = invoice.Subtotal;
            var gst = invoice.GstAmount;
            if (request.LineItems is not null)
            {
                var taxProfile = await _db.TenantTaxProfiles
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
                var (lines, error) = InvoiceAccounting.CalculateLines(request.LineItems.Select(i => new InvoiceLineItemRequest(
                    i.Description, i.Amount ?? 0m, i.Quantity, i.UnitPrice, i.TaxCategory, i.HsnSacCode, i.GstRate, i.DiscountAmount)).ToList(), taxProfile);
                if (error != null) return BadRequest(error);
                newLineItems = lines;
                subtotal = lines.Sum(i => i.Amount - i.TaxAmount);
                gst = lines.Sum(i => i.TaxAmount);
            }

            var discount = request.DiscountAmount ?? invoice.DiscountAmount;
            var discountReason = request.DiscountAmount.HasValue ? request.DiscountReason : invoice.DiscountReason;
            var grossTotal = subtotal + gst;
            if (discount < 0 || discount > grossTotal || decimal.Round(discount, 2) != discount)
                return BadRequest(new { error = "invalid_discount_amount", message = $"Discount must be between ₹0 and ₹{grossTotal:F2}." });
            if (discount > 0 && string.IsNullOrWhiteSpace(discountReason))
                return BadRequest(new { error = "discount_reason_required" });
            if (discountReason?.Length > 255 || request.WalkInCustomerName?.Length > 255 || request.WalkInCustomerPhone?.Length > 20)
                return BadRequest(new { error = "invalid_invoice_metadata" });

            // Validate the whole PATCH before staging any replacement or header change.
            if (newLineItems != null)
            {
                _db.InvoiceLineItems.RemoveRange(invoice.LineItems);
                foreach (var line in newLineItems) line.InvoiceId = invoice.Id;
                invoice.LineItems = newLineItems;
                _db.InvoiceLineItems.AddRange(newLineItems);
                invoice.DocumentType = gst > 0 ? InvoiceDocumentType.TaxInvoice : InvoiceDocumentType.BillOfSupply;
            }
            invoice.Subtotal = subtotal;
            invoice.GstAmount = gst;
            invoice.DiscountAmount = discount;
            invoice.DiscountReason = discountReason;
            invoice.Total = grossTotal - discount;
            invoice.RecalculateBalance();

            // Update walk-in customer info if provided
            if (request.WalkInCustomerName is not null)
                invoice.WalkInCustomerName = request.WalkInCustomerName;
            if (request.WalkInCustomerPhone is not null)
                invoice.WalkInCustomerPhone = request.WalkInCustomerPhone;

            invoice.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            var invoiceRef = invoice.FormatInvoiceNumber();
            return Ok(new
            {
                invoiceId = invoice.Id,
                invoiceNumber = invoiceRef,
                subtotal = invoice.Subtotal,
                gstAmount = invoice.GstAmount,
                discountAmount = invoice.DiscountAmount,
                total = invoice.Total,
                balanceDue = invoice.BalanceDue,
                status = invoice.Status.ToString().ToLower(),
                updatedAt = invoice.UpdatedAt
            });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "invoice_changed_concurrently", message = "Reload the draft before saving again." });
        }
    }

    [HttpPost("invoices/{id:guid}/payment")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    [RequirePermission(Permissions.PaymentRecord)]
    public async Task<IActionResult> CollectPayment(Guid id, [FromBody] CollectPaymentRequest request, CancellationToken ct)
    {
        var tenantId = TenantId;
        if (string.IsNullOrWhiteSpace(request.Method) || request.Utr?.Length > 64 || request.IdempotencyKey?.Length > 128)
            return BadRequest(new { error = "invalid_payment_method_or_reference" });
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var existingPayment = await _db.Payments
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.InvoiceId == id && p.IdempotencyKey == request.IdempotencyKey, ct);
                if (existingPayment is not null)
                {
                    if (!Enum.TryParse<PaymentMethod>(request.Method, true, out var replayMethod)
                        || !InvoiceAccounting.ReceiptAgrees(existingPayment, replayMethod, request.Amount, request.Utr))
                        return Conflict(new { error = "idempotency_key_conflict" });
                    var originalInvoice = await _db.Invoices.FirstAsync(i => i.Id == id && i.TenantId == tenantId, ct);
                    return Ok(new
                    {
                        status = originalInvoice.Status.ToString().ToLowerInvariant(),
                        paymentStatus = existingPayment.Status.ToString().ToLowerInvariant(),
                        paymentId = existingPayment.Id,
                        orderId = existingPayment.RazorpayOrderId,
                        razorpayKeyId = existingPayment.Method == PaymentMethod.Razorpay ? _razorpay.KeyId : null,
                        amount = existingPayment.Amount,
                        currency = "INR",
                        paidAmount = originalInvoice.PaidAmount,
                        balanceDue = originalInvoice.BalanceDue,
                        idempotentReplay = true
                    });
                }
            }

            var invoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
            if (invoice is null)
                return NotFound(new { error = "invoice_not_found" });

            if (invoice.Status is InvoiceStatus.Draft)
                return BadRequest(new { error = "invoice_not_finalized", message = "Finalize the invoice before collecting payment." });

            if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Refunded)
                return BadRequest(new { error = "invoice_already_paid" });

            if (!InvoiceAccounting.CanCollect(invoice, request.Amount))
                return BadRequest(new
                {
                    error = "amount_exceeds_balance",
                    message = $"Payment must be between ₹0.01 and the balance due of ₹{invoice.BalanceDue:F2}."
                });

            if (string.Equals(request.Method, "razorpay", StringComparison.OrdinalIgnoreCase))
            {
                if (!_razorpay.IsConfigured)
                    return StatusCode(503, new { error = "razorpay_not_configured" });

                var orderId = await _razorpay.CreateOrderAsync(request.Amount, invoice.FormatInvoiceNumber(), ct);
                if (string.IsNullOrWhiteSpace(orderId))
                    return StatusCode(502, new { error = "razorpay_order_failed" });

                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    TenantId = invoice.TenantId,
                    InvoiceId = id,
                    Method = PaymentMethod.Razorpay,
                    Amount = request.Amount,
                    RazorpayOrderId = orderId,
                    Status = PaymentStatus.Pending,
                    VerificationStatus = PaymentVerificationStatus.Pending,
                    IdempotencyKey = request.IdempotencyKey,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                _db.Payments.Add(payment);
                await _db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);

                return Ok(new
                {
                    orderId,
                    razorpayKeyId = _razorpay.KeyId,
                    amount = request.Amount,
                    currency = "INR"
                });
            }

            var manualMethod = request.Method.Trim().ToLowerInvariant() switch
            {
                "cash" => PaymentMethod.Cash,
                "upi" => PaymentMethod.UPI,
                "card" => PaymentMethod.Card,
                "netbanking" => PaymentMethod.NetBanking,
                _ => (PaymentMethod?)null
            };

            if (manualMethod is null)
                return BadRequest(new { error = "invalid_payment_method" });

            var taxProfile = await _db.TenantTaxProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
            var utrThreshold = taxProfile?.ThresholdHighValueUtr ?? 2000.0m;
            if (manualMethod != PaymentMethod.Cash
                && request.Amount >= utrThreshold
                && string.IsNullOrWhiteSpace(request.Utr))
            {
                return BadRequest(new
                {
                    error = "utr_mandatory_above_threshold",
                    message = $"Bank UTR / Transaction Reference is mandatory for electronic payments of ₹{utrThreshold:N0} and above."
                });
            }

            var manualPayment = new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = invoice.TenantId,
                InvoiceId = id,
                Method = manualMethod.Value,
                Amount = request.Amount,
                Utr = string.IsNullOrWhiteSpace(request.Utr) ? null : request.Utr.Trim(),
                Status = PaymentStatus.Completed,
                VerificationStatus = PaymentVerificationStatus.Confirmed,
                PaidAt = DateTimeOffset.UtcNow,
                ConfirmedAt = DateTimeOffset.UtcNow,
                ConfirmedByUserId = User.GetUserId(),
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Payments.Add(manualPayment);
            InvoiceAccounting.ConfirmPayment(_db, invoice, manualPayment, User.GetUserId());

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return Ok(new { status = invoice.Status.ToString().ToLower(), paymentId = manualPayment.Id, utr = manualPayment.Utr, paidAmount = invoice.PaidAmount, balanceDue = invoice.BalanceDue });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && InvoiceAccounting.IsIdempotencyConflict(update))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "payment_changed_concurrently", message = "Reload and retry with the same receipt key." });
        }
    }

    [HttpPost("webhooks/razorpay")]
    [AllowAnonymous]
    public async Task<IActionResult> RazorpayWebhook(CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Razorpay-Signature", out var signatureHeader) || string.IsNullOrWhiteSpace(signatureHeader))
            return BadRequest(new { error = "missing_webhook_signature" });

        var secret = _config["Razorpay:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(503, new { error = "webhook_not_configured" });

        using var reader = new StreamReader(Request.Body);
        var bodyText = await reader.ReadToEndAsync(ct);

        if (!PaymentWebhookCodec.Verify("razorpay", secret, bodyText, signatureHeader.ToString()))
            return BadRequest(new { error = "invalid_webhook_signature" });

        var evt = PaymentWebhookCodec.TryParse("razorpay", bodyText);
        if (evt is null) return Ok(new { status = "ignored" });
        if (_tenantContext is null)
            return StatusCode(503, new { error = "tenant_context_unavailable" });
        if (string.IsNullOrWhiteSpace(evt.ProviderOrderId))
            return StatusCode(503, new { error = "payment_order_not_found" });
        if (evt.ProviderOrderId.Length > 255 || evt.Utr?.Length > 64 || evt.Reference?.Length > 255)
            return BadRequest(new { error = "invalid_payment_reference" });

        await using var rlsTransaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        try
        {
            // The verified order only routes; all business reads still require the resolved tenant and exact payment.
            var binding = await _db.Database.SqlQuery<PublicTokenBinding>(
                $"SELECT \"TenantId\", \"ResourceId\" FROM public.resolve_razorpay_order({evt.ProviderOrderId})")
                .SingleOrDefaultAsync(ct);
            if (binding is null || binding.TenantId == Guid.Empty || binding.ResourceId == Guid.Empty)
                return StatusCode(503, new { error = "payment_order_not_found", message = "Retry after the checkout order has been persisted." });
            using var tenantScope = _tenantContext.PushTenant(binding.TenantId);
            var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == binding.ResourceId &&
                p.TenantId == binding.TenantId && p.RazorpayOrderId == evt.ProviderOrderId &&
                p.Method == PaymentMethod.Razorpay && p.Amount > 0, ct);
            if (payment is null) return StatusCode(503, new { error = "payment_order_not_found", message = "Retry after the checkout order has been persisted." });
            var record = await _db.ProcessedWebhookEvents
                .FirstOrDefaultAsync(e => e.TenantId == payment.TenantId && e.Provider == "razorpay" && e.EventId == evt.EventId, ct);
            if (record is { Status: ProcessedWebhookEvent.Processed }) return Ok(new { status = "duplicate" });
            var invoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == payment.InvoiceId && i.TenantId == payment.TenantId, ct);
            if (invoice is null || !InvoiceAccounting.CaptureAgrees(payment, evt) || !InvoiceAccounting.ReferenceAgrees(invoice, evt.Reference))
                return BadRequest(new { error = "capture_does_not_match_order" });
            if (payment.Status == PaymentStatus.Completed && payment.VerificationStatus == PaymentVerificationStatus.Confirmed
                && payment.RazorpayPaymentId == evt.ProviderPaymentId)
            {
                // Already credited through the tenant webhook; only record the common dedupe key.
            }
            else
            {
                if (payment.Status != PaymentStatus.Pending || payment.VerificationStatus != PaymentVerificationStatus.Pending
                    || !InvoiceAccounting.CanCollect(invoice, evt.Amount))
                    return BadRequest(new { error = "invoice_not_payable" });
                payment.RazorpayPaymentId = evt.ProviderPaymentId;
                payment.Utr = evt.Utr ?? payment.Utr;
                InvoiceAccounting.ConfirmPayment(_db, invoice, payment, null);
            }
            if (record is null)
            {
                record = new ProcessedWebhookEvent { Id = Guid.NewGuid(), TenantId = payment.TenantId, Provider = "razorpay", EventId = evt.EventId, Amount = evt.Amount, Utr = evt.Utr, Reference = evt.Reference, RawPayload = bodyText.Length > 4000 ? bodyText[..4000] : bodyText };
                _db.ProcessedWebhookEvents.Add(record);
            }
            record.Status = ProcessedWebhookEvent.Processed;
            record.PaymentId = payment.Id;
            await _db.SaveChangesAsync(ct);
            await rlsTransaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (InvoiceAccounting.IsWebhookDuplicate(ex))
        {
            await rlsTransaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Ok(new { status = "duplicate" });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
        {
            await rlsTransaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return StatusCode(503, new { error = "payment_changed_concurrently" });
        }

        return Ok(new { status = "ok" });
    }

    [HttpGet("invoices")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> ListInvoices(
        [FromQuery] string? status, [FromQuery] Guid? doctorId,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var role = User.GetUserRole();
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();

        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });
        if (!tenantId.HasValue || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty)
            return StatusCode(403, new { error = "invalid_selected_clinic" });

        // Walk-ins carry tenant ownership only; appointment invoices also bind a clinic.
        var query = _db.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId.Value
                && (i.AppointmentId == null || i.Appointment != null
                    && i.Appointment.TenantId == tenantId && i.Appointment.ClinicId == clinicId));

        if (string.Equals(role, "Doctor", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(i => i.Appointment != null && i.Appointment.DoctorId == userId.Value);
        }

        if (DateOnly.TryParse(from, out var fromDate))
        {
            var start = _timezone.GetStartOfDayUtc(fromDate, AccountingTimezone);
            query = query.Where(i => i.CreatedAt >= start);
        }

        if (DateOnly.TryParse(to, out var toDate))
        {
            var end = _timezone.GetStartOfDayUtc(toDate.AddDays(1), AccountingTimezone);
            query = query.Where(i => i.CreatedAt < end);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (string.Equals(status, "unpaid", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Refunded);
            }
            else if (Enum.TryParse<InvoiceStatus>(status, true, out var statusEnum))
            {
                query = query.Where(i => i.Status == statusEnum);
            }
        }

        if (doctorId.HasValue)
            query = query.Where(i => i.Appointment != null && i.Appointment.DoctorId == doctorId.Value);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await query.CountAsync(ct);

        var invoices = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                invoiceId = i.Id,
                invoiceNumber = i.InvoiceNumber > 0 ? $"{(i.Series != null && i.Series != "" ? i.Series : "INV")}-{i.FinancialYear}-{i.InvoiceNumber:D6}" : "DRAFT",
                patientId = i.Appointment != null ? i.Appointment.PatientId : (Guid?)null,
                patientName = i.Appointment != null ? i.Appointment.Patient.Name : (i.WalkInCustomerName ?? "Walk-in Customer"),
                subtotal = i.Subtotal,
                gstAmount = i.GstAmount,
                discountAmount = i.DiscountAmount,
                total = i.Total,
                paidAmount = i.PaidAmount,
                balanceDue = i.BalanceDue,
                status = i.Status.ToString().ToLower(),
                createdAt = i.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new { items = invoices, total, page, pageSize });
    }

    #region Server-Side PDF & Print Verification

    /// <summary>
    /// Generates server-side PDF 1.4 for a finalized invoice (Tax Invoice or Bill of Supply).
    /// Enforces that Draft and Cancelled invoices cannot be printed as statutory invoices.
    /// Every print event is logged to the append-only InvoicePrintLogs table.
    /// </summary>
    [HttpGet("invoices/{id:guid}/pdf")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetInvoicePdf(Guid id, [FromServices] IPdfService pdfService, CancellationToken ct)
    {
        var selected = await SelectedBillingMembershipAsync(ct);
        if (selected is null) return Forbid();
        var invoice = await _db.Invoices
            .Include(i => i.LineItems)
            .Include(i => i.Payments)
            .Include(i => i.Appointment)
                .ThenInclude(a => a!.Patient)
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == selected.TenantId
                && (i.AppointmentId == null || i.Appointment != null && i.Appointment.TenantId == selected.TenantId && i.Appointment.ClinicId == selected.ClinicId), ct);

        if (invoice is null)
            return NotFound(new { error = "invoice_not_found" });

        if (selected.Role == UserRole.Pharmacist && invoice.InvoiceType != InvoiceType.Pharmacy) return Forbid();

        if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled)
        {
            return BadRequest(new
            {
                error = "cannot_print_draft_or_cancelled_invoice",
                message = "Draft or cancelled invoices cannot be printed as statutory tax invoices."
            });
        }

        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == selected.ClinicId && c.TenantId == selected.TenantId, ct);
        var taxProfile = await _db.TenantTaxProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == invoice.TenantId, ct);
        var supplierGstin = invoice.InvoiceType == InvoiceType.Pharmacy
            ? clinic?.PharmacyGstNumber
            : taxProfile?.Gstin;

        var copyNumber = await NextPrintCopyAsync(invoice, ct);
        bool isDuplicate = copyNumber > 1;
        byte[] pdfBytes;
        try { pdfBytes = pdfService.GenerateInvoicePdf(invoice, clinic, isDuplicate, supplierGstin); }
        catch (EncoderFallbackException)
        {
            return UnprocessableEntity(new { error = "invoice_pdf_font_unsupported", message = "The PDF font cannot represent this invoice's text. No print was recorded." });
        }
        RecordPrint(invoice, copyNumber);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "invoice_changed_concurrently", message = "Retry printing after reloading the invoice." });
        }
        var invoiceRef = invoice.FormatInvoiceNumber();
        var filename = $"{invoiceRef}_{(isDuplicate ? "DUPLICATE" : "ORIGINAL")}.pdf";

        return File(pdfBytes, "application/pdf", filename);
    }

    /// <summary>
    /// Generates server-side PDF 1.4 for an Approved credit note.
    /// Pending approval or Rejected credit notes cannot be printed.
    /// </summary>
    [HttpGet("credit-notes/{id:guid}/pdf")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetCreditNotePdf(Guid id, [FromServices] IPdfService pdfService, CancellationToken ct)
    {
        var selected = await SelectedBillingMembershipAsync(ct);
        if (selected is null) return Forbid();
        var creditNote = await _db.CreditNotes
            .Include(cn => cn.Invoice)
                .ThenInclude(i => i.LineItems)
            .Include(cn => cn.Invoice)
                .ThenInclude(i => i.Appointment)
                    .ThenInclude(a => a!.Patient)
            .FirstOrDefaultAsync(cn => cn.Id == id && cn.TenantId == selected.TenantId && cn.Invoice.TenantId == selected.TenantId
                && (cn.Invoice.AppointmentId == null || cn.Invoice.Appointment != null
                    && cn.Invoice.Appointment.TenantId == selected.TenantId && cn.Invoice.Appointment.ClinicId == selected.ClinicId), ct);

        if (creditNote is null)
            return NotFound(new { error = "credit_note_not_found" });

        if (selected.Role == UserRole.Pharmacist && creditNote.Invoice.InvoiceType != InvoiceType.Pharmacy) return Forbid();

        if (creditNote.Status != CreditNoteStatus.Approved)
        {
            return BadRequest(new
            {
                error = "cannot_print_unapproved_credit_note",
                message = "Credit notes must be approved before generating statutory PDF."
            });
        }

        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == selected.ClinicId && c.TenantId == selected.TenantId, ct);
        var taxProfile = await _db.TenantTaxProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == creditNote.TenantId, ct);
        var supplierGstin = creditNote.Invoice.InvoiceType == InvoiceType.Pharmacy
            ? clinic?.PharmacyGstNumber
            : taxProfile?.Gstin;

        byte[] pdfBytes;
        try { pdfBytes = pdfService.GenerateCreditNotePdf(creditNote, creditNote.Invoice, clinic, supplierGstin); }
        catch (EncoderFallbackException)
        {
            return UnprocessableEntity(new { error = "credit_note_pdf_font_unsupported", message = "The PDF font cannot represent this credit note's text." });
        }
        var cnRef = creditNote.FormatCreditNoteNumber();
        var filename = $"{cnRef}.pdf";

        return File(pdfBytes, "application/pdf", filename);
    }

    #endregion
}

public record GenerateInvoiceRequest(
    [Required(ErrorMessage = "Appointment ID is required")]
    Guid AppointmentId,

    [Required(ErrorMessage = "LineItems are required"), MinLength(1, ErrorMessage = "At least one line item is required"), MaxLength(50, ErrorMessage = "Maximum 50 line items per invoice")]
    List<InvoiceLineItemRequest> LineItems,

    [RegularExpression(@"^IDEMP-INV-[a-zA-Z0-9\-]+$", ErrorMessage = "IdempotencyKey must follow format IDEMP-INV-{UUID}")]
    string? IdempotencyKey = null,

    decimal DiscountAmount = 0.0m,
    string? DiscountReason = null,
    Guid? LinkedPharmacyInvoiceId = null,
    bool AutoFinalize = false);

public record InvoiceLineItemRequest(
    [Required(ErrorMessage = "Description is required"), StringLength(150, MinimumLength = 2, ErrorMessage = "Description must be between 2 and 150 characters")]
    string Description,

    [Range(0.01, 1000000.00, ErrorMessage = "Amount must be between ₹0.01 and ₹10,00,000")]
    decimal Amount,

    int Quantity = 1,
    decimal? UnitPrice = null,
    string? TaxCategory = null,
    string? HsnSacCode = null,
    decimal? GstRate = null,
    decimal DiscountAmount = 0.0m);

public record CreateCreditNoteRequest(
    [Range(0.01, 1000000.00, ErrorMessage = "Credit note amount must be positive")]
    decimal Amount,

    [Required, MinLength(3), MaxLength(500)]
    string Reason);

public record CollectPaymentRequest(
    [Required(ErrorMessage = "Payment Method is required"), RegularExpression("(?i)^(cash|upi|card|netbanking|razorpay)$", ErrorMessage = "Method must be cash, upi, card, netbanking or razorpay")]
    string Method,

    [Range(0.01, 1000000.00, ErrorMessage = "Amount must be between ₹0.01 and ₹10,00,000")]
    decimal Amount,

    [RegularExpression(@"^IDEMP-PAY-[a-zA-Z0-9\-]+$", ErrorMessage = "IdempotencyKey must follow format IDEMP-PAY-{UUID}")]
    string? IdempotencyKey = null,

    [MaxLength(64)]
    string? Utr = null);

public record CreateRefundRequest(
    [Range(0.01, 1000000.00, ErrorMessage = "Refund amount must be between ₹0.01 and ₹10,00,000")]
    decimal Amount,

    PaymentMethod Method,

    [MaxLength(100)]
    string? RefundReference,

    [Required(ErrorMessage = "Reason is required"), MinLength(3), MaxLength(500)]
    string Reason,

    [Required, MaxLength(128), RegularExpression(@"^IDEMP-REF-[a-zA-Z0-9\-]+$", ErrorMessage = "IdempotencyKey must follow format IDEMP-REF-{UUID}")]
    string? IdempotencyKey = null,
    Guid? SourcePaymentId = null,
    Guid? RefundCreditNoteId = null,
    [MaxLength(500)]
    string? RefundNonConsiderationReason = null);

public record ApproveRefundRequest(
    [MaxLength(500)]
    string? Note = null);

public record UpdateInvoiceLineItemRequest(
    [Required, MinLength(1), MaxLength(255)]
    string Description,
    [Range(1, int.MaxValue)]
    int Quantity,
    [Range(0.01, double.MaxValue)]
    decimal? UnitPrice = null,
    [Range(0.01, double.MaxValue)]
    decimal? Amount = null,
    [Range(0, double.MaxValue)]
    decimal DiscountAmount = 0.0m,
    [MaxLength(20)]
    string? HsnSacCode = null,
    [MaxLength(20)]
    string? TaxCategory = null,
    [Range(0, 100)]
    decimal? GstRate = null);

public record UpdateInvoiceDraftRequest(
    List<UpdateInvoiceLineItemRequest>? LineItems = null,
    [Range(0, double.MaxValue)]
    decimal? DiscountAmount = null,
    [MaxLength(255)]
    string? DiscountReason = null,
    [MaxLength(255)]
    string? WalkInCustomerName = null,
    [MaxLength(20)]
    string? WalkInCustomerPhone = null);

public record ApproveCreditNoteRequest(
    [MaxLength(500)]
    string? Note = null);

public record RejectCreditNoteRequest(
    [MaxLength(500)]
    string? Reason = null);

public record LinkPharmacyInvoiceRequest(
    Guid PharmacyInvoiceId);



