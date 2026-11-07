using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/payee-accounts")]
public class PayeeAccountsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly IEmailSender _email;
    private readonly ILogger<PayeeAccountsController> _logger;

    public PayeeAccountsController(
        HospitalCrmDbContext db,
        ISecretProtector secretProtector,
        IEmailSender email,
        ILogger<PayeeAccountsController> logger)
    {
        _db = db;
        _secretProtector = secretProtector;
        _email = email;
        _logger = logger;
    }

    private Guid TenantId => User.GetTenantId() ?? Guid.Empty;

    // Money is routed to the caller's own clinic account, never to a sibling clinic's.
    private Guid? ClinicId => User.GetClinicId();

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin", "Owner", "Receptionist", "Doctor")]
    public async Task<IActionResult> ListAccounts([FromQuery] bool? includeInactive, CancellationToken ct)
    {
        if (!await HasSelectedMembershipAsync(admin: false, ct)) return Forbid();
        var tenantId = TenantId;
        var clinicId = ClinicId;
        var query = _db.PayeeAccounts.Where(p => p.TenantId == tenantId && p.ClinicId == clinicId);

        if (includeInactive != true)
            query = query.Where(p => p.IsActive);

        var now = DateTimeOffset.UtcNow;
        var accounts = await query
            .OrderBy(p => p.PriorityOrder)
            .ThenBy(p => p.CreatedAt)
            .Select(p => new
            {
                id = p.Id,
                accountHolderName = p.AccountHolderName,
                vpa = p.Vpa,
                bankName = p.BankName,
                accountNumberMasked = p.AccountNumberMasked,
                ifscCode = p.IfscCode,
                priorityOrder = p.PriorityOrder,
                dailyLimit = p.DailyLimit,
                dailyCollected = p.DailyCollected,
                isActive = p.IsActive,
                coolingOffUntil = p.CoolingOffUntil,
                isCoolingOff = p.CoolingOffUntil.HasValue && p.CoolingOffUntil.Value > now
            })
            .ToListAsync(ct);

        return Ok(accounts);
    }

    [HttpPost]
    [AuthorizeRoles("ClinicAdmin", "Owner")]
    public async Task<IActionResult> CreateAccount([FromBody] CreatePayeeAccountRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        if (tenantId == Guid.Empty)
            return Unauthorized(new { error = "invalid_tenant" });

        var stepUp = await RequireStepUp(req.CurrentPassword, req.MfaCode, ct);
        if (stepUp is not null) return stepUp;

        // Basic VPA format validation (e.g. username@bank)
        if (!Regex.IsMatch(req.Vpa.Trim(), @"^[\w\.\-]+@[\w\-]+$"))
            return BadRequest(new { error = "invalid_vpa_format", message = "UPI ID / VPA must follow username@bank format" });

        if (!Regex.IsMatch(req.IfscCode.Trim().ToUpperInvariant(), @"^[A-Z]{4}0[A-Z0-9]{6}$"))
            return BadRequest(new { error = "invalid_ifsc_format", message = "IFSC must be 11 characters: 4 letters, 0, then 6 alphanumerics." });

        if (!Regex.IsMatch(req.AccountNumber.Trim(), @"^\d{9,18}$"))
            return BadRequest(new { error = "invalid_account_number", message = "Account number must be 9 to 18 digits." });

        var priorityOrder = req.PriorityOrder > 0 ? req.PriorityOrder : 1;
        var clinicId = ClinicId;
        if (await _db.PayeeAccounts.AnyAsync(p => p.TenantId == tenantId
                && p.ClinicId == clinicId && p.PriorityOrder == priorityOrder && p.IsActive, ct))
            return BadRequest(new { error = "priority_in_use", message = $"An active payee account already uses priority {priorityOrder}." });

        var now = DateTimeOffset.UtcNow;
        // 24-hour security cooling-off delay on new accounts
        var coolingOff = now.AddHours(24);

        var account = new PayeeAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            AccountHolderName = req.AccountHolderName.Trim(),
            Vpa = req.Vpa.Trim().ToLowerInvariant(),
            BankName = req.BankName.Trim(),
            AccountNumberMasked = MaskAccountNumber(req.AccountNumber.Trim()),
            IfscCode = req.IfscCode.Trim().ToUpperInvariant(),
            PriorityOrder = priorityOrder,
            DailyLimit = req.DailyLimit > 0 ? req.DailyLimit : 100000.0m,
            DailyCollected = 0.0m,
            IsActive = true,
            CoolingOffUntil = coolingOff,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.PayeeAccounts.Add(account);
        await _db.SaveChangesAsync(ct);

        await NotifyAdminsAsync(account.TenantId, account.ClinicId!.Value,
            "Payee account added",
            $"A payee account {account.Vpa} ({account.AccountNumberMasked}) was added by {User.Identity?.Name ?? "a clinic admin"}. It cannot receive QR traffic for 24 hours.",
            ct);

        return StatusCode(201, new
        {
            id = account.Id,
            accountHolderName = account.AccountHolderName,
            vpa = account.Vpa,
            bankName = account.BankName,
            accountNumberMasked = account.AccountNumberMasked,
            ifscCode = account.IfscCode,
            priorityOrder = account.PriorityOrder,
            dailyLimit = account.DailyLimit,
            isActive = account.IsActive,
            coolingOffUntil = account.CoolingOffUntil,
            isCoolingOff = true,
            warning = "Payee account is in 24-hour cooling-off protection. Live QR will activate once cooling-off period completes."
        });
    }

    [HttpPatch("{id:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Owner")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromBody] UpdatePayeeAccountRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var clinicId = ClinicId;
        var stepUp = await RequireStepUp(req.CurrentPassword, req.MfaCode, ct);
        if (stepUp is not null) return stepUp;

        var account = await _db.PayeeAccounts
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId
                && p.ClinicId == clinicId, ct);

        if (account == null)
            return NotFound(new { error = "payee_account_not_found" });

        if (req.PriorityOrder.HasValue && req.PriorityOrder.Value > 0)
        {
            var newPriority = req.PriorityOrder.Value;
            if (await _db.PayeeAccounts.AnyAsync(p => p.TenantId == tenantId && p.Id != id
                    && p.ClinicId == account.ClinicId && p.PriorityOrder == newPriority && p.IsActive, ct))
                return BadRequest(new { error = "priority_in_use", message = $"An active payee account already uses priority {newPriority}." });
            account.PriorityOrder = newPriority;
        }

        if (req.DailyLimit.HasValue && req.DailyLimit.Value > 0)
            account.DailyLimit = req.DailyLimit.Value;

        if (req.IsActive.HasValue)
        {
            // Re-activating an account restarts the cooling-off window.
            if (req.IsActive.Value && !account.IsActive)
                account.CoolingOffUntil = DateTimeOffset.UtcNow.AddHours(24);
            account.IsActive = req.IsActive.Value;
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await NotifyAdminsAsync(account.TenantId, account.ClinicId!.Value,
            "Payee account changed",
            $"Payee account {account.Vpa} ({account.AccountNumberMasked}) was updated by {User.Identity?.Name ?? "a clinic admin"}. Active: {account.IsActive}. Priority: {account.PriorityOrder}.",
            ct);

        return Ok(new
        {
            id = account.Id,
            accountHolderName = account.AccountHolderName,
            vpa = account.Vpa,
            priorityOrder = account.PriorityOrder,
            dailyLimit = account.DailyLimit,
            isActive = account.IsActive,
            coolingOffUntil = account.CoolingOffUntil
        });
    }

    /// <summary>
    /// Re-authentication for a money-routing change: password always, plus the
    /// enrolled second factor when MFA is on. A stolen session alone cannot redirect settlement.
    /// </summary>
    private async Task<IActionResult?> RequireStepUp(string? password, string? mfaCode, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        if (!await HasSelectedMembershipAsync(admin: true, ct)) return Forbid();

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
        if (user is null) return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(password) || !password.VerifyPassword(user.PasswordHash))
            return Unauthorized(new { error = "step_up_required", message = "Re-enter your password to change payee accounts." });

        if (user.MfaEnabled)
        {
            var secret = _secretProtector.Unprotect(user.MfaSecret);
            var method = (user.MfaMethod ?? "totp").Trim().ToLowerInvariant();
            var valid = secret is not null && (method == "email"
                ? TotpService.Verify(secret, mfaCode, DateTimeOffset.UtcNow, skewSteps: 1, periodSeconds: TotpService.EmailPeriodSeconds)
                : TotpService.Verify(secret, mfaCode, DateTimeOffset.UtcNow));

            if (!valid)
                return Unauthorized(new { error = "mfa_code_invalid", message = "A current authenticator/email code is required to change payee accounts." });
        }

        return null;
    }

    private Task<bool> HasSelectedMembershipAsync(bool admin, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = TenantId;
        var clinicId = ClinicId;
        if (userId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty)
            return Task.FromResult(false);
        return _db.TenantMemberships.AsNoTracking().AnyAsync(m =>
            m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active"
            && (m.Role == UserRole.ClinicAdmin || m.Role == UserRole.Owner
                || !admin && (m.Role == UserRole.Receptionist || m.Role == UserRole.Doctor))
            && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
    }

    private async Task NotifyAdminsAsync(Guid tenantId, Guid clinicId, string subject, string body, CancellationToken ct)
    {
        try
        {
            var admins = _db.TenantMemberships.AsNoTracking().Where(m =>
                m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active"
                && m.Role == UserRole.ClinicAdmin && m.Clinic != null && m.Clinic.TenantId == tenantId);
            var ids = await admins.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(ct);
            foreach (var id in ids)
            {
                try
                {
                    // A previous send may outlive a role change; resolve eligibility and address again.
                    var to = await admins.Where(m => m.Id == id).Select(m => m.User.Email).SingleOrDefaultAsync(ct);
                    if (to is null) continue;
                    if (!await _email.SendAsync(to, subject, body, ct))
                        _logger.LogWarning("Payee account alert handoff failed for membership {MembershipId}", id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Payee account alert failed for membership {MembershipId}; the account change was committed", id);
                }
            }
        }
        catch (Exception ex)
        {
            // Recipient lookup and cancellation are also post-commit failures.
            _logger.LogWarning(ex, "Payee account alert lookup failed; the account change was committed");
        }
    }

    private static string MaskAccountNumber(string accountNum)
    {
        if (string.IsNullOrWhiteSpace(accountNum)) return "****";
        if (accountNum.Length <= 4) return accountNum;
        return new string('*', accountNum.Length - 4) + accountNum[^4..];
    }
}

public record CreatePayeeAccountRequest(
    [Required, MinLength(2), MaxLength(255)] string AccountHolderName,
    [Required, MinLength(5), MaxLength(100)] string Vpa,
    [Required, MinLength(2), MaxLength(100)] string BankName,
    [Required, MinLength(4), MaxLength(50)] string AccountNumber,
    [Required, MinLength(4), MaxLength(20)] string IfscCode,
    int PriorityOrder = 1,
    decimal DailyLimit = 100000.0m,
    string? CurrentPassword = null,
    string? MfaCode = null);

public record UpdatePayeeAccountRequest(
    int? PriorityOrder,
    decimal? DailyLimit,
    bool? IsActive,
    string? CurrentPassword = null,
    string? MfaCode = null);
