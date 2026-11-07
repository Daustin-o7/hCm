using System.ComponentModel.DataAnnotations;
using Hangfire;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class PasswordResetController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<PasswordResetController> _logger;
    private readonly IBackgroundJobClient? _jobs;
    private readonly IDataProtectionProvider? _protection;

    public PasswordResetController(HospitalCrmDbContext db, ILogger<PasswordResetController> logger,
        IBackgroundJobClient? jobs = null, IDataProtectionProvider? protection = null)
    {
        _db = db;
        _logger = logger;
        _jobs = jobs;
        _protection = protection;
    }

    [HttpPost("request-password-reset")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<IActionResult> RequestReset([FromBody] RequestResetRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254
            || !new EmailAddressAttribute().IsValid(request.Email))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "valid_email_required" }));

        var requestId = Guid.NewGuid();
        // Identical work for known/unknown addresses; neither lookup nor SMTP runs on this request.
        try
        {
            if (_jobs is null || _protection is null) throw new InvalidOperationException();
            var payload = PasswordResetEmailJob.ProtectRequest(_protection, request.Email.Trim());
            _jobs.Enqueue<PasswordResetEmailJob>(job => job.SendAsync(requestId, payload, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogError("Reset request {RequestId} could not be queued: {FailureType}", requestId, ex.GetType().Name);
        }
        return Task.FromResult<IActionResult>(Ok(new { message = "If the email is registered, you will receive password reset instructions." }));
    }

    [HttpPost("confirm-password-reset")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ConfirmReset([FromBody] ConfirmResetRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ResetToken) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { error = "token_and_password_required" });
        if (request.NewPassword.Length < 8 || request.NewPassword.Length > 72 || request.ResetToken.Length > 512)
            return BadRequest(new { error = "invalid_token_or_password_length" });

        // Digest lookup: one indexed read instead of hashing every live token.
        var tokenHash = request.ResetToken.HashToken();
        var tokenRecord = await _db.PasswordResetTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash
                                      && t.UsedAt == null
                                      && t.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (tokenRecord is null)
            return BadRequest(new { error = "invalid_or_expired_token" });

        try
        {
            return await SessionMutation.RunAsync(_db, tokenRecord.UserId, () => ConfirmResetLockedAsync(tokenRecord, request, ct), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "password_changed_concurrently" });
        }
    }

    private async Task<IActionResult> ConfirmResetLockedAsync(PasswordResetToken tokenRecord, ConfirmResetRequest request, CancellationToken ct)
    {
        if (_db.Database.IsRelational()) await _db.Entry(tokenRecord).ReloadAsync(ct);
        if (tokenRecord.UsedAt is not null || tokenRecord.ExpiresAt <= DateTimeOffset.UtcNow)
            return BadRequest(new { error = "invalid_or_expired_token" });
        var user = await _db.Users.FindAsync([tokenRecord.UserId], ct);
        if (user is null)
            return BadRequest(new { error = "user_not_found" });

        var now = DateTimeOffset.UtcNow;
        if (_db.Database.IsRelational() && await _db.PasswordResetTokens
            .Where(t => t.Id == tokenRecord.Id && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct) != 1)
            return BadRequest(new { error = "invalid_or_expired_token" });

        user.PasswordHash = request.NewPassword.HashPassword();
        user.MustChangePassword = false;
        user.UpdatedAt = now;
        tokenRecord.UsedAt = now;

        // A reset proves the old password (and any session holding it) is compromised:
        // every live refresh token dies, and older reset links stop working.
        var liveTokens = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var live in liveTokens)
            live.RevokedAt = now;

        var otherTokens = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var other in otherTokens)
            other.UsedAt = now;

        await _db.SaveChangesAsync(ct);

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.PasswordChanged, "success",
            user.Id, user.TenantId, user.Email, detail: $"via reset token; sessions revoked={liveTokens.Count}", ct: ct);

        return Ok(new { message = "password_reset_successful" });
    }
}

public record RequestResetRequest([Required, EmailAddress, StringLength(254)] string Email);
public record ConfirmResetRequest([Required, StringLength(512)] string ResetToken,
    [Required, MinLength(8), MaxLength(72)] string NewPassword);
