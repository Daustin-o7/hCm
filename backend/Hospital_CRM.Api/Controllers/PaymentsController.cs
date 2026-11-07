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
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class PaymentsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<PaymentsController>? _logger;
    private readonly IEmailSender? _email;

    public PaymentsController(
        HospitalCrmDbContext db,
        ILogger<PaymentsController>? logger = null,
        IEmailSender? email = null)
    {
        _db = db;
        _logger = logger;
        _email = email;
    }

    private Guid TenantId => User.GetTenantId() ?? Guid.Empty;

    [HttpPost("invoices/{id:guid}/payments/dynamic-qr")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GenerateDynamicQr(Guid id, [FromBody] DynamicQrRequest? req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var invoice = await _db.Invoices
            .Include(i => i.LineItems)
            .Include(i => i.Appointment)
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

        if (invoice == null)
            return NotFound(new { error = "invoice_not_found" });

        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Refunded)
            return BadRequest(new { error = "invoice_already_paid" });

        var amountPayable = invoice.BalanceDue > 0 ? invoice.BalanceDue : invoice.Total;
        if (amountPayable <= 0)
            return BadRequest(new { error = "nothing_to_pay" });

        var clinic = await InvoiceClinicAsync(invoice, ct);
        if (clinic is null)
            return StatusCode(503, new { error = "payee_account_unavailable", message = "The invoice clinic could not be determined." });
        var clinicName = clinic.LegalName ?? clinic.Name;

        var now = DateTimeOffset.UtcNow;
        var accountsQuery = _db.PayeeAccounts
            .Where(p => p.TenantId == tenantId && p.IsActive
                     && p.ClinicId == clinic.Id);

        // Filter out accounts still in 24h cooling off
        var eligibleAccounts = await accountsQuery
            .Where(p => !p.CoolingOffUntil.HasValue || p.CoolingOffUntil.Value <= now)
            .OrderBy(p => p.PriorityOrder)
            .ToListAsync(ct);

        if (eligibleAccounts.Count == 0)
        {
            // If cooling off accounts exist, return informative cooling-off response
            var inCooling = await accountsQuery.AnyAsync(ct);
            return StatusCode(503, new
            {
                error = "payee_account_unavailable",
                message = inCooling
                    ? "Configured payee account is currently in 24-hour security cooling-off period."
                    : "No active clinic payee bank account or UPI ID configured."
            });
        }

        // Calculate today's collections per account to enforce DailyLimit
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var accountIds = eligibleAccounts.Select(a => a.Id).ToList();

        var todayCollections = await _db.Payments
            .Where(p => p.TenantId == tenantId
                     && p.PayeeAccountId.HasValue
                     && accountIds.Contains(p.PayeeAccountId.Value)
                     && p.Status == PaymentStatus.Completed
                     && p.ConfirmedAt >= todayStart)
            .GroupBy(p => p.PayeeAccountId!.Value)
            .Select(g => new { AccountId = g.Key, Total = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Total, ct);

        // An account has headroom if DailyLimit <= 0 or (todayCollected + amountPayable) <= DailyLimit
        bool HasLimitHeadroom(PayeeAccount acc)
        {
            if (acc.DailyLimit <= 0) return true;
            var collected = todayCollections.TryGetValue(acc.Id, out var sum) ? sum : acc.DailyCollected;
            return (collected + amountPayable) <= acc.DailyLimit;
        }

        var accountsWithLimit = eligibleAccounts.Where(HasLimitHeadroom).ToList();

        if (accountsWithLimit.Count == 0)
        {
            return StatusCode(503, new
            {
                error = "payee_daily_limit_exceeded",
                message = "All eligible clinic payee bank accounts or UPI VPAs have reached their daily collection limit."
            });
        }

        // Selection: Primary or Backup account toggle with daily limit enforcement
        PayeeAccount selectedAccount;
        var useBackup = req?.UseBackupAccount == true;
        if (useBackup)
        {
            selectedAccount = accountsWithLimit.FirstOrDefault(p => p.PriorityOrder > 1) ?? accountsWithLimit.First();
        }
        else
        {
            // Primary account preferred if it has headroom; otherwise automatically roll over to backup
            selectedAccount = accountsWithLimit.FirstOrDefault(p => p.PriorityOrder == 1) ?? accountsWithLimit.First();
        }

        var invoiceRef = invoice.FormatInvoiceNumber();
        var encodedClinic = Uri.EscapeDataString(clinicName);
        var encodedNote = Uri.EscapeDataString($"Bill {invoiceRef}");

        // Standard NPCI UPI Intent / Deep Link specification
        var upiUri = $"upi://pay?pa={selectedAccount.Vpa}&pn={encodedClinic}&am={amountPayable:F2}&tr={invoice.Id:N}&cu=INR&tn={encodedNote}";

        // Scannable QR: the patient scans this with any UPI app. No QR image means the
        // receptionist has to read out a VPA, so render it here rather than in the browser.
        using var qrData = QRCodeGenerator.GenerateQrCode(upiUri, QRCodeGenerator.ECCLevel.M);
        var qrSvg = new SvgQRCode(qrData).GetGraphic(10);

        var todayCollectedSelected = todayCollections.TryGetValue(selectedAccount.Id, out var coll) ? coll : selectedAccount.DailyCollected;

        return Ok(new
        {
            invoiceId = invoice.Id,
            invoiceNumber = invoiceRef,
            amount = amountPayable,
            currency = "INR",
            upiUri,
            qrSvg,
            account = new
            {
                id = selectedAccount.Id,
                vpa = selectedAccount.Vpa,
                accountHolderName = selectedAccount.AccountHolderName,
                bankName = selectedAccount.BankName,
                accountNumberMasked = selectedAccount.AccountNumberMasked,
                priorityOrder = selectedAccount.PriorityOrder,
                isBackup = selectedAccount.PriorityOrder > 1,
                dailyLimit = selectedAccount.DailyLimit,
                dailyCollectedToday = todayCollectedSelected,
                limitHeadroom = selectedAccount.DailyLimit > 0 ? Math.Max(0m, selectedAccount.DailyLimit - todayCollectedSelected) : (decimal?)null
            },
            backupAvailable = accountsWithLimit.Any(p => p.PriorityOrder > 1)
        });
    }

    [HttpPost("invoices/{id:guid}/payments/report")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> ReportPayment(Guid id, [FromBody] ReportPaymentRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var method = ParseMethod(req.Method);
        if (method is null)
            return BadRequest(new { error = "invalid_payment_method" });
        if (req.Amount < 0 || decimal.Round(req.Amount, 2) != req.Amount || req.Utr?.Length > 64)
            return BadRequest(new { error = "invalid_payment_amount_or_reference" });
        if (req.IdempotencyKey is { } reportKey && (string.IsNullOrWhiteSpace(reportKey) || reportKey.Length > 128))
            return BadRequest(new { error = "invalid_idempotency_key" });
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var invoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

            if (invoice == null)
                return NotFound(new { error = "invoice_not_found" });

            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                var existing = await _db.Payments.FirstOrDefaultAsync(
                    p => p.TenantId == tenantId && p.IdempotencyKey == req.IdempotencyKey, ct);
                if (existing is not null)
                {
                    if (existing.InvoiceId != id || !InvoiceAccounting.ReceiptAgrees(existing, method.Value, req.Amount, req.Utr, req.PayeeAccountId))
                        return Conflict(new { error = "idempotency_key_conflict" });
                    return Ok(ReportResult(existing, true));
                }
            }

            if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Refunded || invoice.BalanceDue <= 0)
                return BadRequest(new { error = "invoice_already_paid" });
            if (invoice.Status is InvoiceStatus.Draft)
                return BadRequest(new { error = "invoice_not_finalized", message = "Finalize the invoice before reporting a payment." });

            var reportedAmount = req.Amount == 0 ? invoice.BalanceDue : req.Amount;
            if (!InvoiceAccounting.CanCollect(invoice, reportedAmount))
                return BadRequest(new
                {
                    error = "amount_exceeds_balance",
                    message = $"Reported amount must be between ₹0.01 and the balance due of ₹{invoice.BalanceDue:F2}."
                });

            if (req.PayeeAccountId.HasValue)
            {
                var clinic = await InvoiceClinicAsync(invoice, ct);
                if (clinic is null || !await _db.PayeeAccounts.AnyAsync(p => p.Id == req.PayeeAccountId
                    && p.TenantId == invoice.TenantId && p.ClinicId == clinic.Id && p.IsActive, ct))
                    return BadRequest(new { error = "payee_account_not_found" });
            }

            // Clinic cap on total pending unconfirmed reported payments
            var profile = await _db.TenantTaxProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
            var maxCount = profile?.MaxUnconfirmedReportedPaymentsCount ?? 50;
            var maxAmount = profile?.MaxUnconfirmedReportedPaymentsAmount ?? 100000.0m;

            var unconfirmedQuery = _db.Payments
                .Where(p => p.TenantId == tenantId && p.Status == PaymentStatus.Pending && p.VerificationStatus == PaymentVerificationStatus.Reported);

            var currentUnconfirmedCount = await unconfirmedQuery.CountAsync(ct);
            var currentUnconfirmedAmount = await unconfirmedQuery.SumAsync(p => p.Amount, ct);

            if (currentUnconfirmedCount >= maxCount || (currentUnconfirmedAmount + reportedAmount) > maxAmount)
            {
                return BadRequest(new
                {
                    error = "reported_payments_cap_exceeded",
                    message = $"Clinic cap on pending unconfirmed reported payments exceeded ({currentUnconfirmedCount}/{maxCount} payments, ₹{currentUnconfirmedAmount:F2}/₹{maxAmount:F2} pending). Staff must verify pending payments before new reports can be accepted."
                });
            }

            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                InvoiceId = id,
                Method = method.Value,
                Amount = reportedAmount,
                Utr = string.IsNullOrWhiteSpace(req.Utr) ? null : req.Utr.Trim(),
                PayeeAccountId = req.PayeeAccountId,
                Status = PaymentStatus.Pending,
                VerificationStatus = PaymentVerificationStatus.Reported,
                ReportedAt = DateTimeOffset.UtcNow,
                IdempotencyKey = req.IdempotencyKey,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Payments.Add(payment);
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return Ok(ReportResult(payment, false));
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && InvoiceAccounting.IsIdempotencyConflict(update))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "payment_changed_concurrently", message = "Retry with the same report key." });
        }
    }

    [HttpPost("invoices/{id:guid}/payments/confirm")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Pharmacist")]
    [RequirePermission(Permissions.PaymentRecord)]
    public async Task<IActionResult> ConfirmPayment(Guid id, [FromBody] ConfirmPaymentRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var method = ParseMethod(req.Method);
        if (method is null)
            return BadRequest(new { error = "invalid_payment_method" });
        if (req.Amount < 0 || decimal.Round(req.Amount, 2) != req.Amount || req.Utr?.Length > 64)
            return BadRequest(new { error = "invalid_payment_amount_or_reference" });
        if (req.IdempotencyKey is { } receiptKey && (string.IsNullOrWhiteSpace(receiptKey) || receiptKey.Length > 128))
            return BadRequest(new { error = "invalid_idempotency_key" });

        object result;
        (Invoice Invoice, Guid? StaffId, int Count)? spikeAlert = null;
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var invoice = await _db.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);

            if (invoice == null)
                return NotFound(new { error = "invoice_not_found" });

            Payment? payment = null;
            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                payment = await _db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == req.IdempotencyKey, ct);
                if (payment != null && (payment.InvoiceId != id || req.PaymentId.HasValue && req.PaymentId != payment.Id
                    || !InvoiceAccounting.ReceiptAgrees(payment, method.Value, req.Amount, req.Utr, req.PayeeAccountId)))
                    return Conflict(new { error = "idempotency_key_conflict" });
            }
            if (req.PaymentId.HasValue)
            {
                payment ??= invoice.Payments.FirstOrDefault(p => p.Id == req.PaymentId.Value && p.TenantId == tenantId);
                if (payment is null) return NotFound(new { error = "payment_not_found" });
            }
            if (payment?.IdempotencyKey is { } originalKey && !string.IsNullOrWhiteSpace(req.IdempotencyKey)
                && originalKey != req.IdempotencyKey)
                return Conflict(new { error = "idempotency_key_conflict", message = "Keep the original reported payment key when confirming or retrying it." });
            if (payment != null && !InvoiceAccounting.ReceiptAgrees(payment, method.Value, req.Amount, req.Utr, req.PayeeAccountId))
                return Conflict(new { error = "reported_payment_conflict", message = "Confirm the reported amount, method and reference without replacing them." });
            if (payment is { Status: PaymentStatus.Completed, VerificationStatus: PaymentVerificationStatus.Confirmed })
                return Ok(ConfirmResult(invoice, payment, alreadyConfirmed: true));
            if (payment != null && (payment.Status != PaymentStatus.Pending || payment.VerificationStatus != PaymentVerificationStatus.Reported))
                return BadRequest(new { error = "payment_not_confirmable", message = "Only a reported, unconfirmed payment can be confirmed." });

            if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled or InvoiceStatus.Refunded || invoice.BalanceDue <= 0)
                return BadRequest(new { error = "invoice_already_paid" });

            if (invoice.Status is InvoiceStatus.Draft)
                return BadRequest(new { error = "invoice_not_finalized", message = "Finalize the invoice before confirming a payment." });

            var amount = payment?.Amount ?? (req.Amount == 0 ? invoice.BalanceDue : req.Amount);
            if (!InvoiceAccounting.CanCollect(invoice, amount))
                return BadRequest(new
                {
                    error = "amount_exceeds_balance",
                    message = $"Payment must be between ₹0.01 and the balance due of ₹{invoice.BalanceDue:F2}."
                });

            var profile = await _db.TenantTaxProfiles.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
            var thresholdUtr = profile?.ThresholdHighValueUtr ?? 2000.0m;
            var utr = string.IsNullOrWhiteSpace(req.Utr) ? payment?.Utr : req.Utr.Trim();
            var payeeId = req.PayeeAccountId ?? payment?.PayeeAccountId;
            if (payeeId.HasValue)
            {
                var clinic = await InvoiceClinicAsync(invoice, ct);
                if (clinic is null || !await _db.PayeeAccounts.AnyAsync(p => p.Id == payeeId
                    && p.TenantId == invoice.TenantId && p.ClinicId == clinic.Id && p.IsActive, ct))
                    return BadRequest(new { error = "payee_account_not_found" });
            }

            // Mandatory UTR above clinic threshold for UPI/Bank transfers
            if (method != PaymentMethod.Cash && amount >= thresholdUtr && string.IsNullOrWhiteSpace(utr))
            {
                return BadRequest(new
                {
                    error = "utr_mandatory_above_threshold",
                    message = $"Bank UTR / Transaction Reference is mandatory for electronic payments of ₹{thresholdUtr:N0} and above."
                });
            }

            var currentUserId = User.GetUserId();
            var now = DateTimeOffset.UtcNow;

            // Anomaly / Spike Detection: check how many payments this user confirmed in the last hour
            var spikeThreshold = profile?.StaffConfirmationSpikeThreshold ?? 20;
            var oneHourAgo = now.AddHours(-1);
            var recentUserConfirmations = await _db.Payments
                .CountAsync(p => p.TenantId == tenantId
                              && p.ConfirmedByUserId == currentUserId
                              && p.ConfirmedAt >= oneHourAgo, ct);

            bool isSpikeDetected = (recentUserConfirmations + 1) >= spikeThreshold;
            if (isSpikeDetected)
            {
                _logger?.LogWarning(
                    "SECURITY ALERT: Staff confirmation spike detected for user {UserId} in tenant {TenantId}: {Count} confirmations in past hour (Threshold: {Threshold})",
                    currentUserId, tenantId, recentUserConfirmations + 1, spikeThreshold);

                _db.AuthAuditLogs.Add(new AuthAuditLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = currentUserId,
                    EventType = "payment_confirmation_spike_detected",
                    Outcome = "warning",
                    Detail = $"Staff member confirmed {recentUserConfirmations + 1} payments within 1 hour (Threshold: {spikeThreshold}).",
                    CreatedAt = now
                });

            }

            if (payment == null)
            {
                payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    InvoiceId = id,
                    Method = method.Value,
                    Amount = amount,
                    Utr = utr,
                    PayeeAccountId = payeeId,
                    Status = PaymentStatus.Pending,
                    IdempotencyKey = req.IdempotencyKey,
                    CreatedAt = now
                };
                _db.Payments.Add(payment);
            }
            else
            {
                payment.Utr = utr;
                payment.PayeeAccountId = payeeId;
                if (payment.IdempotencyKey == null && !string.IsNullOrWhiteSpace(req.IdempotencyKey))
                    payment.IdempotencyKey = req.IdempotencyKey;
            }

            InvoiceAccounting.ConfirmPayment(_db, invoice, payment, currentUserId);

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            if (isSpikeDetected && _email != null)
                spikeAlert = (invoice, currentUserId, recentUserConfirmations + 1);
            result = ConfirmResult(invoice, payment, alreadyConfirmed: false, anomalySpikeAlert: isSpikeDetected);
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && InvoiceAccounting.IsIdempotencyConflict(update))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "payment_changed_concurrently", message = "Reload and retry with the same receipt key." });
        }
        // Alert failures cannot enter the receipt rollback/conflict handler after commit.
        if (spikeAlert is { } alert)
            await NotifySpikeAsync(alert.Invoice, alert.StaffId, alert.Count, ct);
        return Ok(result);
    }

    private async Task<Clinic?> InvoiceClinicAsync(Invoice invoice, CancellationToken ct)
    {
        var clinics = _db.Clinics.AsNoTracking().Where(c => c.TenantId == invoice.TenantId);
        if (invoice.AppointmentId.HasValue)
            clinics = clinics.Where(c => _db.Appointments.Any(a => a.Id == invoice.AppointmentId
                && a.TenantId == invoice.TenantId && a.ClinicId == c.Id));
        // Walk-in invoices have no clinic column; only a unique tenant clinic is authoritative.
        var matches = await clinics.Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task NotifySpikeAsync(Invoice invoice, Guid? staffId, int count, CancellationToken ct)
    {
        try
        {
            var clinic = await InvoiceClinicAsync(invoice, ct);
            if (clinic is null)
            {
                _logger?.LogWarning("Payment spike alert skipped: clinic unresolved for invoice {InvoiceId}; payment and audit were committed", invoice.Id);
                return;
            }
            var admins = _db.TenantMemberships.AsNoTracking().Where(m =>
                m.TenantId == invoice.TenantId && m.ClinicId == clinic.Id && m.Status == "active"
                && m.Role == UserRole.ClinicAdmin && m.Clinic != null && m.Clinic.TenantId == invoice.TenantId);
            var ids = await admins.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(ct);
            var staffName = await _db.Users.Where(u => u.Id == staffId).Select(u => u.Name).FirstOrDefaultAsync(ct)
                ?? staffId.ToString();
            foreach (var id in ids)
            {
                try
                {
                    var to = await admins.Where(m => m.Id == id).Select(m => m.User.Email).SingleOrDefaultAsync(ct);
                    if (to is null) continue;
                    if (!await _email!.SendAsync(to, "ALERT: Staff Payment Confirmation Spike Detected",
                        $"Notice: Staff member '{staffName}' has confirmed {count} payments in the last hour. Please audit recent invoice payments.", ct))
                        _logger?.LogWarning("Payment spike alert handoff failed for membership {MembershipId}", id);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Payment spike alert failed for membership {MembershipId}; payment and audit were committed", id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Payment spike alert lookup failed; payment and audit were committed");
        }
    }

    private static PaymentMethod? ParseMethod(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "cash" => PaymentMethod.Cash,
        "upi" => PaymentMethod.UPI,
        "card" => PaymentMethod.Card,
        "netbanking" => PaymentMethod.NetBanking,
        _ => null
    };

    private static object ConfirmResult(Invoice invoice, Payment payment, bool alreadyConfirmed, bool anomalySpikeAlert = false) => new
    {
        paymentId = payment.Id,
        invoiceId = invoice.Id,
        invoiceNumber = invoice.FormatInvoiceNumber(),
        status = invoice.Status.ToString().ToLower(),
        paidAmount = invoice.PaidAmount,
        balanceDue = invoice.BalanceDue,
        amount = payment.Amount,
        method = payment.Method.ToString(),
        paymentStatus = payment.Status.ToString().ToLowerInvariant(),
        verificationStatus = payment.VerificationStatus.ToString().ToLowerInvariant(),
        idempotencyKey = payment.IdempotencyKey,
        payeeAccountId = payment.PayeeAccountId,
        utr = payment.Utr,
        confirmedAt = payment.ConfirmedAt,
        alreadyConfirmed,
        anomalySpikeAlert
    };

    private static object ReportResult(Payment payment, bool replay) => new
    {
        paymentId = payment.Id,
        invoiceId = payment.InvoiceId,
        amount = payment.Amount,
        method = payment.Method.ToString(),
        utr = payment.Utr,
        payeeAccountId = payment.PayeeAccountId,
        idempotencyKey = payment.IdempotencyKey,
        status = payment.VerificationStatus.ToString().ToLowerInvariant(),
        paymentStatus = payment.Status.ToString().ToLowerInvariant(),
        verificationStatus = payment.VerificationStatus.ToString().ToLowerInvariant(),
        reportedAt = payment.ReportedAt,
        confirmedAt = payment.ConfirmedAt,
        idempotentReplay = replay
    };
}

public record DynamicQrRequest(bool UseBackupAccount = false);

public record ReportPaymentRequest(
    [Required] string Method,
    decimal Amount = 0.0m,
    string? Utr = null,
    [MaxLength(128)] string? IdempotencyKey = null,
    Guid? PayeeAccountId = null);

public record ConfirmPaymentRequest(
    [Required] string Method,
    decimal Amount = 0.0m,
    string? Utr = null,
    Guid? PaymentId = null,
    [MaxLength(128)] string? IdempotencyKey = null,
    Guid? PayeeAccountId = null);
