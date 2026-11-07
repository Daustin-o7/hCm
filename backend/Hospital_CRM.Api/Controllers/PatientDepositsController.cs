using System.ComponentModel.DataAnnotations;
using System.Data;
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
[Route("api/v1")]
public class PatientDepositsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;

    public PatientDepositsController(HospitalCrmDbContext db) => _db = db;

    private Guid TenantId => User.GetTenantId() ?? Guid.Empty;

    [HttpPost("patients/{patientId:guid}/deposits")]
    [AuthorizeRoles("Receptionist", "ClinicAdmin")]
    [RequirePermission(Permissions.PaymentRecord)]
    public async Task<IActionResult> CollectDeposit(Guid patientId, [FromBody] CreatePatientDepositRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        if (req.Amount <= 0 || req.Amount > 10000000m || decimal.Round(req.Amount, 2) != req.Amount)
            return BadRequest(new { error = "invalid_amount", message = "Deposit amount must be positive, within the limit and have at most two decimal places." });
        if (req.Method is not (PaymentMethod.Cash or PaymentMethod.UPI or PaymentMethod.Card or PaymentMethod.NetBanking))
            return BadRequest(new { error = "invalid_payment_method" });
        if (req.ReferenceNumber?.Length > 100 || req.Notes?.Length > 500
            || req.Method != PaymentMethod.Cash && string.IsNullOrWhiteSpace(req.ReferenceNumber))
            return BadRequest(new { error = "payment_reference_required", message = "Record the actual bank or card receipt reference before confirming an electronic deposit." });
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 128)
            return BadRequest(new { error = "idempotency_key_required", message = "Use one stable key for this deposit, including retries." });

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var patient = await _db.Patients
                .FirstOrDefaultAsync(p => p.Id == patientId && p.TenantId == tenantId, ct);
            if (patient == null)
                return NotFound(new { error = "patient_not_found" });

            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                var existing = await _db.PatientDeposits
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.IdempotencyKey == req.IdempotencyKey, ct);
                if (existing != null)
                {
                    if (existing.PatientId != patientId || existing.Amount != req.Amount || existing.Method != req.Method
                        || existing.ReferenceNumber != (string.IsNullOrWhiteSpace(req.ReferenceNumber) ? null : req.ReferenceNumber.Trim())
                        || existing.Notes != (string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim()))
                        return Conflict(new { error = "idempotency_key_conflict" });
                    return Ok(DepositResult(existing, true));
                }
            }

            var now = DateTimeOffset.UtcNow;
            var financialYear = IndianFinancialYearUtils.GetFinancialYear(now);

            // Gapless sequential numbering per tenant + FY
            var maxSeq = await _db.PatientDeposits
                .Where(d => d.TenantId == tenantId && d.FinancialYear == financialYear)
                .Select(d => (int?)d.SequenceNumber)
                .MaxAsync(ct) ?? 0;

            var nextSeq = maxSeq + 1;
            var receiptNumber = $"DEP-{financialYear}-{nextSeq:D6}";

            var deposit = new PatientDeposit
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClinicId = User.GetClinicId(),
                PatientId = patientId,
                ReceiptNumber = receiptNumber,
                FinancialYear = financialYear,
                SequenceNumber = nextSeq,
                Amount = req.Amount,
                AllocatedAmount = 0.0m,
                RefundedAmount = 0.0m,
                Balance = req.Amount,
                Method = req.Method,
                ReferenceNumber = string.IsNullOrWhiteSpace(req.ReferenceNumber) ? null : req.ReferenceNumber.Trim(),
                Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
                Status = PatientDepositStatus.Active,
                CollectedByUserId = currentUserId,
                IdempotencyKey = req.IdempotencyKey,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.PatientDeposits.Add(deposit);

            // Double-entry accounting:
            // Debit: Cash / Bank (Asset increases)
            // Credit: PatientDeposits (Liability increases — unearned revenue / advance held for patient)
            var debitAccount = req.Method == PaymentMethod.Cash ? "Cash" : "Bank";

            _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntryType = FinanceEntryType.DepositCollected,
                ReferenceType = "PatientDeposit",
                ReferenceId = deposit.Id,
                ReferenceNumber = receiptNumber,
                AccountCategory = debitAccount,
                DebitAmount = req.Amount,
                CreditAmount = 0.0m,
                Narration = $"Patient advance deposit received from {patient.Name} via {req.Method}. Receipt: {receiptNumber}",
                PostedByUserId = currentUserId,
                CreatedAt = now
            });

            _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntryType = FinanceEntryType.DepositCollected,
                ReferenceType = "PatientDeposit",
                ReferenceId = deposit.Id,
                ReferenceNumber = receiptNumber,
                AccountCategory = "PatientDeposits",
                DebitAmount = 0.0m,
                CreditAmount = req.Amount,
                Narration = $"Advance liability recorded for {patient.Name}. Receipt: {receiptNumber}",
                PostedByUserId = currentUserId,
                CreatedAt = now
            });

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return StatusCode(201, DepositResult(deposit, false));
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && (InvoiceAccounting.IsIdempotencyConflict(update) || InvoiceAccounting.IsNumberingConflict(update)))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "deposit_changed_concurrently", message = "Retry with the same deposit key." });
        }
    }

    [HttpGet("patients/{patientId:guid}/deposits")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> GetPatientDeposits(Guid patientId, CancellationToken ct)
    {
        var tenantId = TenantId;
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Id == patientId && p.TenantId == tenantId, ct);
        if (patient == null)
            return NotFound(new { error = "patient_not_found" });

        var deposits = await _db.PatientDeposits
            .Include(d => d.Allocations)
            .Where(d => d.PatientId == patientId && d.TenantId == tenantId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        var totalDeposited = deposits.Sum(d => d.Amount);
        var totalAllocated = deposits.Sum(d => d.AllocatedAmount);
        var totalRefunded = deposits.Sum(d => d.RefundedAmount);
        var availableBalance = deposits.Where(d => d.Status == PatientDepositStatus.Active).Sum(d => d.Balance);

        return Ok(new
        {
            patientId,
            patientName = patient.Name,
            totalDeposited,
            totalAllocated,
            totalRefunded,
            availableBalance,
            deposits = deposits.Select(d => new
            {
                depositId = d.Id,
                receiptNumber = d.ReceiptNumber,
                amount = d.Amount,
                allocatedAmount = d.AllocatedAmount,
                refundedAmount = d.RefundedAmount,
                balance = d.Balance,
                method = d.Method.ToString(),
                referenceNumber = d.ReferenceNumber,
                status = d.Status.ToString().ToLower(),
                createdAt = d.CreatedAt,
                allocationsCount = d.Allocations.Count
            })
        });
    }

    [HttpPost("invoices/{id:guid}/apply-advance")]
    [AuthorizeRoles("Receptionist", "ClinicAdmin")]
    [RequirePermission(Permissions.PaymentRecord)]
    public async Task<IActionResult> ApplyAdvanceToInvoice(Guid id, [FromBody] ApplyAdvanceRequest? req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        if (req?.Amount is { } amount && (amount <= 0 || amount > 10000000m || decimal.Round(amount, 2) != amount))
            return BadRequest(new { error = "invalid_advance_amount" });
        if (req is null || string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 128)
            return BadRequest(new { error = "idempotency_key_required", message = "Use one stable key for this advance application, including retries." });
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var invoice = await _db.Invoices
                .Include(i => i.Payments)
                .Include(i => i.Appointment)
                .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

            if (invoice == null)
                return NotFound(new { error = "invoice_not_found" });

            var existing = await _db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == req.IdempotencyKey, ct);
            if (existing != null)
            {
                var allocations = await _db.PatientDepositAllocations.Include(a => a.PatientDeposit)
                    .Where(a => a.TenantId == tenantId && a.PaymentId == existing.Id).ToListAsync(ct);
                if (existing.InvoiceId != id || existing.Method != PaymentMethod.AdvanceDeposit || existing.Status != PaymentStatus.Completed
                    || existing.VerificationStatus != PaymentVerificationStatus.Confirmed || allocations.Count == 0
                    || req.Amount.HasValue && req.Amount != existing.Amount
                    || req.DepositId.HasValue && allocations.Any(a => a.PatientDepositId != req.DepositId))
                    return Conflict(new { error = "idempotency_key_conflict" });
                return Ok(AdvanceResult(invoice, existing, allocations, true));
            }

            if (invoice.Status is not (InvoiceStatus.Issued or InvoiceStatus.Finalized or InvoiceStatus.PartiallyPaid))
                return BadRequest(new { error = "invoice_not_finalized", message = "Advance can only be applied to finalized invoices with an outstanding balance." });

            if (invoice.BalanceDue <= 0m)
                return BadRequest(new { error = "invoice_already_paid", message = "Invoice has no balance due." });

            var patientId = invoice.PatientId ?? invoice.Appointment?.PatientId;
            if (!patientId.HasValue)
                return BadRequest(new { error = "patient_not_linked", message = "Invoice must be linked to an appointment with a patient to apply advances." });

            // Fetch active deposits for this patient
            var depositsQuery = _db.PatientDeposits
                .Where(d => d.TenantId == tenantId && d.PatientId == patientId.Value && d.Status == PatientDepositStatus.Active && d.Balance > 0);

            if (req.DepositId.HasValue)
                depositsQuery = depositsQuery.Where(d => d.Id == req.DepositId.Value);

            var activeDeposits = await depositsQuery
                .OrderBy(d => d.CreatedAt)
                .ThenBy(d => d.Id)
                .ToListAsync(ct);

            if (activeDeposits.Any(d => d.Balance != d.Amount - d.AllocatedAmount - d.RefundedAmount || d.AllocatedAmount < 0 || d.RefundedAmount < 0))
                return Conflict(new { error = "deposit_reconciliation_required", message = "The recorded deposit balances must be reconciled before applying an advance." });

            var availableAdvance = activeDeposits.Sum(d => d.Balance);
            if (availableAdvance <= 0m)
                return BadRequest(new { error = "no_advance_available", message = "Patient has no available advance deposit balance." });

            var maxApplicable = Math.Min(invoice.BalanceDue, availableAdvance);
            var requestedAmount = req.Amount ?? maxApplicable;

            if (requestedAmount <= 0m)
                return BadRequest(new { error = "invalid_advance_amount", message = "Requested advance amount must be greater than zero." });

            if (requestedAmount > invoice.BalanceDue)
                return BadRequest(new { error = "amount_exceeds_balance", message = $"Requested advance ₹{requestedAmount:F2} exceeds balance due ₹{invoice.BalanceDue:F2}." });

            if (requestedAmount > availableAdvance)
                return BadRequest(new { error = "amount_exceeds_available_advance", message = $"Requested advance ₹{requestedAmount:F2} exceeds available advance of ₹{availableAdvance:F2}." });

            var remainingToAllocate = requestedAmount;
            var now = DateTimeOffset.UtcNow;
            var invoiceRef = invoice.FormatInvoiceNumber();
            var allocationsSummary = new List<PatientDepositAllocation>();
            var paymentId = Guid.NewGuid();

            foreach (var deposit in activeDeposits)
            {
                if (remainingToAllocate <= 0m) break;

                var allocAmount = Math.Min(deposit.Balance, remainingToAllocate);
                deposit.AllocatedAmount += allocAmount;
                deposit.Balance -= allocAmount;
                if (deposit.Balance <= 0m)
                    deposit.Status = PatientDepositStatus.FullyAllocated;
                deposit.UpdatedAt = now;

                var allocation = new PatientDepositAllocation
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PatientDepositId = deposit.Id,
                    InvoiceId = invoice.Id,
                    PaymentId = paymentId,
                    AllocatedAmount = allocAmount,
                    AllocatedByUserId = currentUserId,
                    AllocatedAt = now,
                    Notes = $"Applied to {invoiceRef}"
                };
                _db.PatientDepositAllocations.Add(allocation);
                allocation.PatientDeposit = deposit;
                allocationsSummary.Add(allocation);

                remainingToAllocate -= allocAmount;
            }

            // Record Payment row on Invoice
            var payment = new Payment
            {
                Id = paymentId,
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                Method = PaymentMethod.AdvanceDeposit,
                Amount = requestedAmount,
                Utr = $"ADV-{invoiceRef}",
                Status = PaymentStatus.Completed,
                VerificationStatus = PaymentVerificationStatus.Confirmed,
                ConfirmedByUserId = currentUserId,
                PaidAt = now,
                ConfirmedAt = now,
                CreatedAt = now
            };
            payment.IdempotencyKey = req.IdempotencyKey;
            _db.Payments.Add(payment);

            // Update Invoice balance
            invoice.PaidAmount += requestedAmount;
            invoice.RecalculateBalance();
            invoice.Status = invoice.BalanceDue <= 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
            invoice.UpdatedAt = now;

            // Double-entry accounting for advance application:
            // Debit: PatientDeposits (Liability reduced)
            // Credit: AccountsReceivable (Asset/Receivable reduced)
            _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntryType = FinanceEntryType.DepositApplied,
                ReferenceType = "PatientDepositAllocation",
                ReferenceId = payment.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = "PatientDeposits",
                DebitAmount = requestedAmount,
                CreditAmount = 0.0m,
                Narration = $"Patient advance of ₹{requestedAmount:F2} applied to {invoiceRef}",
                PostedByUserId = currentUserId,
                CreatedAt = now
            });

            _db.FinanceLedgerEntries.Add(new FinanceLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntryType = FinanceEntryType.DepositApplied,
                ReferenceType = "Invoice",
                ReferenceId = invoice.Id,
                ReferenceNumber = invoiceRef,
                AccountCategory = "AccountsReceivable",
                DebitAmount = 0.0m,
                CreditAmount = requestedAmount,
                Narration = $"Receivable offset for {invoiceRef} using advance deposit",
                PostedByUserId = currentUserId,
                CreatedAt = now
            });

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return Ok(AdvanceResult(invoice, payment, allocationsSummary, false));
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && InvoiceAccounting.IsIdempotencyConflict(update))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "advance_changed_concurrently", message = "Reload and retry with the same advance key." });
        }
    }

    private static object DepositResult(PatientDeposit deposit, bool replay) => new
    {
        depositId = deposit.Id,
        receiptNumber = deposit.ReceiptNumber,
        financialYear = deposit.FinancialYear,
        sequenceNumber = deposit.SequenceNumber,
        patientId = deposit.PatientId,
        amount = deposit.Amount,
        balance = deposit.Balance,
        method = deposit.Method.ToString(),
        referenceNumber = deposit.ReferenceNumber,
        notes = deposit.Notes,
        status = deposit.Status.ToString().ToLowerInvariant(),
        createdAt = deposit.CreatedAt,
        idempotencyKey = deposit.IdempotencyKey,
        idempotentDuplicate = replay,
        idempotentReplay = replay
    };

    private static object AdvanceResult(Invoice invoice, Payment payment, IEnumerable<PatientDepositAllocation> allocations, bool replay) => new
    {
        invoiceId = invoice.Id,
        invoiceNumber = invoice.FormatInvoiceNumber(),
        paymentId = payment.Id,
        amount = payment.Amount,
        method = payment.Method.ToString(),
        paymentStatus = payment.Status.ToString().ToLowerInvariant(),
        verificationStatus = payment.VerificationStatus.ToString().ToLowerInvariant(),
        idempotencyKey = payment.IdempotencyKey,
        confirmedAt = payment.ConfirmedAt,
        allocatedAdvanceAmount = payment.Amount,
        paidAmount = invoice.PaidAmount,
        balanceDue = invoice.BalanceDue,
        status = invoice.Status.ToString().ToLowerInvariant(),
        idempotentReplay = replay,
        allocations = allocations.Select(a => new
        {
            allocationId = a.Id,
            depositId = a.PatientDepositId,
            receiptNumber = a.PatientDeposit.ReceiptNumber,
            amount = a.AllocatedAmount,
            depositRemainingBalance = a.PatientDeposit.Balance
        })
    };
}

public record CreatePatientDepositRequest(
    [Range(0.01, 10000000.00, ErrorMessage = "Amount must be between ₹0.01 and ₹1,00,00,000")]
    decimal Amount,

    PaymentMethod Method,

    [MaxLength(100)]
    string? ReferenceNumber = null,

    [MaxLength(500)]
    string? Notes = null,

    [Required, MaxLength(128)] string? IdempotencyKey = null);

public record ApplyAdvanceRequest(
    [Range(0.01, 10000000.00)]
    decimal? Amount = null,

    Guid? DepositId = null,

    [Required, MaxLength(128)] string? IdempotencyKey = null);
