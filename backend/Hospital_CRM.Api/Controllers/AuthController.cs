using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AuthController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IConfiguration _config;
    private readonly IPersistentKeyService _keyService;
    private readonly IWebHostEnvironment _env;
    private readonly ISecretProtector _secretProtector;
    private readonly IEmailSender _email;
    private readonly ITenantContext? _tenantContext;

    public AuthController(
        HospitalCrmDbContext db,
        IConfiguration config,
        IPersistentKeyService keyService,
        IWebHostEnvironment env,
        ISecretProtector secretProtector,
        IEmailSender email,
        ITenantContext? tenantContext = null)
    {
        _db = db;
        _config = config;
        _keyService = keyService;
        _env = env;
        _secretProtector = secretProtector;
        _email = email;
        _tenantContext = tenantContext;
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "invalid_credentials" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);

        if (user == null)
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.LoginFailed, "failure",
                email: request.Email, detail: "unknown_email", ct: ct);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        return await SessionMutation.RunAsync(_db, user.Id, () => LoginLockedAsync(user, request, ct), ct);
    }

    private async Task<IActionResult> LoginLockedAsync(User user, LoginRequest request, CancellationToken ct)
    {
        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTimeOffset.UtcNow)
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.LoginLocked, "denied",
                user.Id, user.TenantId, user.Email, "account_locked", ct);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        if (!request.Password.VerifyPassword(user.PasswordHash))
        {
            // Brute-force lockout: count failures and lock the account past the threshold.
            var maxAttempts = _config.GetValue<int>("Auth:MaxFailedAttempts", 5);
            var lockMinutes = _config.GetValue<int>("Auth:LockoutMinutes", 15);

            user.FailedLoginCount++;
            if (user.FailedLoginCount >= maxAttempts)
                user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(lockMinutes);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.LoginFailed, "failure",
                user.Id, user.TenantId, user.Email,
                user.LockedUntil.HasValue ? "bad_password_locked" : "bad_password", ct);

            return Unauthorized(new { error = "invalid_credentials" });
        }

        // With a second factor enrolled, the password alone yields a short-lived challenge only.
        if (user.MfaEnabled)
        {
            var method = NormalizeMethod(user.MfaMethod);

            if (method == "email" && !await SendEmailCodeAsync(user, ct))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = "email_delivery_failed",
                    message = "Could not send the sign-in code. Try again, or switch to an authenticator app in Settings."
                });
            }

            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.MfaRequired, "success",
                user.Id, user.TenantId, user.Email, method, ct);

            return Ok(new { mfaRequired = true, challengeToken = CreateMfaChallenge(user), method });
        }

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.LoginSucceeded, "success",
            user.Id, user.TenantId, user.Email, detail: "password", ct: ct);

        return await ResolveTenantAndIssueSessionAsync(user, request.TenantId, ct);
    }

    private async Task<IActionResult> ResolveTenantAndIssueSessionAsync(User user, Guid? requestedTenantId, CancellationToken ct)
    {
        if (user.Role == UserRole.PlatformAdmin)
        {
            return await IssueSessionAsync(user, ct, null, null, UserRole.PlatformAdmin);
        }

        var memberships = await _db.TenantMemberships
            .AsNoTracking()
            .Where(m => m.UserId == user.Id && m.Status == "active")
            .ToListAsync(ct);

        if (requestedTenantId.HasValue && requestedTenantId.Value != Guid.Empty)
        {
            var chosen = memberships.FirstOrDefault(m => m.TenantId == requestedTenantId.Value);
            if (chosen == null)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "unauthorized_tenant" });

            return await CheckSubscriptionAndIssueSessionAsync(user, chosen.TenantId, chosen.ClinicId, chosen.Role, ct);
        }

        // Multiple memberships -> return clinic selection prompt
        if (memberships.Count > 1)
        {
            var options = await ActiveMembershipsAsync(user.Id, ct);
            return Ok(new
            {
                requiresTenantSelection = true,
                selectionToken = CreateTenantSelectionChallenge(user),
                memberships = options
            });
        }

        // Single membership
        if (memberships.Count == 1)
        {
            return await CheckSubscriptionAndIssueSessionAsync(user, memberships[0].TenantId, memberships[0].ClinicId, memberships[0].Role, ct);
        }

        return StatusCode(StatusCodes.Status403Forbidden, new { error = "unauthorized_tenant" });
    }

    private async Task<IActionResult> CheckSubscriptionAndIssueSessionAsync(
        User user,
        Guid? tenantId,
        Guid? clinicId,
        UserRole role,
        CancellationToken ct)
    {
        using var selectedTenant = tenantId is { } selected && selected != Guid.Empty
            ? _tenantContext?.PushTenant(selected) : null;
        if (!Enum.IsDefined(role) || role == UserRole.PlatformAdmin && user.Role != UserRole.PlatformAdmin)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "unauthorized_tenant" });
        if (role != UserRole.PlatformAdmin && (tenantId is null || tenantId == Guid.Empty
            || clinicId is null || clinicId == Guid.Empty
            || !await _db.Clinics.IgnoreQueryFilters().AnyAsync(c => c.Id == clinicId && c.TenantId == tenantId, ct)))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "unauthorized_tenant" });
        if (role != UserRole.PlatformAdmin && tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            var tenant = await _db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId.Value)
                .Select(t => new { t.SubscriptionStatus, t.SubscriptionEndsAt })
                .FirstOrDefaultAsync(ct);

            if (tenant is null)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "tenant_not_found" });

            var access = SubscriptionPolicy.EffectiveAccess(tenant.SubscriptionStatus, tenant.SubscriptionEndsAt, DateTimeOffset.UtcNow);
            if (access == EffectiveAccessLevel.Blocked)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "tenant_suspended",
                    message = "Tenant subscription is suspended or expired."
                });
            }
        }

        return await IssueSessionAsync(user, ct, tenantId, clinicId, role);
    }

    /// <summary>Issues access and refresh tokens scoped to the selected tenant and clinic.</summary>
    private async Task<IActionResult> IssueSessionAsync(
        User user,
        CancellationToken ct,
        Guid? tenantId,
        Guid? clinicId,
        UserRole role)
    {
        var (tokenExpiry, refreshExpiryDays) = TokenLifetimes();

        var effectiveTenantId = role == UserRole.PlatformAdmin ? null : tenantId;
        var effectiveClinicId = role == UserRole.PlatformAdmin ? null : clinicId;
        var effectiveRole = role;
        var familyId = Guid.NewGuid();

        var extraClaims = SessionClaims(user, effectiveRole);
        extraClaims["session_id"] = familyId.ToString();
        if (effectiveClinicId.HasValue && effectiveClinicId.Value != Guid.Empty)
            extraClaims["clinicId"] = effectiveClinicId.Value.ToString();

        var accessToken = await GenerateJwtAsync(
            user.Id, user.Email, effectiveRole.ToString(), effectiveTenantId, tokenExpiry, ct, extraClaims);

        var refreshToken = Guid.NewGuid().ToString("N");
        _db.RefreshTokens.Add(NewRefreshToken(
            user.Id, refreshToken, effectiveTenantId, effectiveClinicId,
            effectiveRole, familyId, refreshExpiryDays));

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        AppendRefreshCookie(refreshToken, refreshExpiryDays);

        return Ok(await BuildSessionResponseAsync(
            user, accessToken, tokenExpiry, effectiveTenantId, effectiveClinicId, effectiveRole, ct));
    }

    private (int AccessMinutes, int RefreshDays) TokenLifetimes()
    {
        if (!int.TryParse(_config["Jwt:AccessTokenExpiryMinutes"], out var tokenExpiry))
            tokenExpiry = 15;
        if (!int.TryParse(_config["Jwt:RefreshTokenExpiryDays"], out var refreshExpiryDays))
            refreshExpiryDays = 7;
        return (tokenExpiry, refreshExpiryDays);
    }

    private static RefreshToken NewRefreshToken(
        Guid userId, string rawToken, Guid? tenantId, Guid? clinicId,
        UserRole role, Guid familyId, int refreshExpiryDays) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TenantId = tenantId,
            ClinicId = clinicId,
            Role = role,
            TokenHash = rawToken.HashToken(),
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshExpiryDays),
            LastUsedAt = DateTimeOffset.UtcNow
        };

    private void AppendRefreshCookie(string token, int days) =>
        Response.Cookies.Append("refreshToken", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !_env.IsDevelopment() || Request.IsHttps,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(days)
        });

    private void DeleteRefreshCookie() =>
        Response.Cookies.Delete("refreshToken", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !_env.IsDevelopment() || Request.IsHttps,
            Path = "/"
        });

    private sealed record SessionMembership(
        Guid TenantId, Guid? ClinicId, string ClinicName, string Role, bool IsDefault);

    private async Task<List<SessionMembership>> ActiveMembershipsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await _db.TenantMemberships
            .AsNoTracking()
            .Include(m => m.Clinic)
            .Where(m => m.UserId == userId && m.Status == "active")
            .ToListAsync(ct);

        return rows.Select(m => new SessionMembership(
            m.TenantId,
            m.ClinicId,
            m.Clinic?.Name ?? "Main Clinic",
            m.Role.ToString().ToLowerInvariant(),
            m.IsDefault)).ToList();
    }

    private async Task<object> BuildSessionResponseAsync(
        User user, string accessToken, int tokenExpiry,
        Guid? tenantId, Guid? clinicId, UserRole role, CancellationToken ct) => new
        {
            accessToken,
            expiresIn = tokenExpiry * 60,
            tenantId,
            clinicId,
            mustChangePassword = user.MustChangePassword,
            mfaEnabled = user.MfaEnabled,
            mfaMethod = user.MfaEnabled ? NormalizeMethod(user.MfaMethod) : null,
            mfaEnrolmentRequired = MfaGateOn && !user.MfaEnabled && MfaPolicy.RequiresEnrolment(role),
            mfaRequiredForRole = MfaGateOn && MfaPolicy.RequiresEnrolment(role),
            user = new
            {
                id = user.Id,
                name = user.Name,
                email = user.Email,
                role = role.ToString().ToLowerInvariant()
            },
            memberships = await ActiveMembershipsAsync(user.Id, ct)
        };


    /// <summary>
    /// Production enforces second-factor enrolment for privileged roles. Development turns it off
    /// (<c>Mfa:RequireEnrolment=false</c> in appsettings.Development.json) so seeded accounts can log
    /// in without a phone; the go-live checklist requires the flag to stay unset in production.
    /// </summary>
    private bool MfaGateOn => _config.GetValue("Mfa:RequireEnrolment", true);

    private Dictionary<string, object?> SessionClaims(User user, UserRole role)
    {
        var claims = new Dictionary<string, object?>();
        if (user.MustChangePassword)
            claims["must_change_password"] = "true";
        if (user.MfaEnabled)
            claims["mfa_enabled"] = "true";
        // The scoped role decides: an admin membership needs MFA even if the account's
        // primary role is a nurse, and vice versa.
        else if (MfaGateOn && MfaPolicy.RequiresEnrolment(role))
            claims["mfa_enrolment_required"] = "true";
        return claims;
    }

    [HttpPost("refresh")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        var rawToken = !string.IsNullOrWhiteSpace(request?.RefreshToken)
            ? request.RefreshToken
            : Request.Cookies["refreshToken"];

        // No credential at all (fresh visitor, or cookie cleared): nothing to refresh.
        // 204 keeps the boot probe out of the console; a presented-but-invalid token still gets 401.
        if (string.IsNullOrWhiteSpace(rawToken))
            return NoContent();

        // Deterministic digest => one indexed lookup. No scan, no BCrypt cost.
        var tokenHash = rawToken.HashToken();
        var storedToken = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (storedToken is null)
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshRejected, "failure",
                detail: "unknown_token", ct: ct);
            return Unauthorized(new { error = "invalid_refresh_token" });
        }

        return await SessionMutation.RunAsync(_db, storedToken.UserId, () => RefreshLockedAsync(storedToken, ct), ct);
    }

    private async Task<IActionResult> RefreshLockedAsync(RefreshToken storedToken, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Entry(storedToken).ReloadAsync(ct);
        }
        // Presenting an already-revoked token means the credential leaked. Burn the family.
        if (storedToken.RevokedAt is not null)
        {
            var family = await _db.RefreshTokens
                .Where(t => t.UserId == storedToken.UserId && t.RevokedAt == null
                    && (storedToken.FamilyId != null && t.FamilyId == storedToken.FamilyId
                        || storedToken.FamilyId == null && t.Id == storedToken.Id))
                .ToListAsync(ct);

            foreach (var token in family)
                token.RevokedAt = DateTimeOffset.UtcNow;

            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshReuseDetected, "denied",
                storedToken.UserId, storedToken.TenantId,
                detail: $"family={storedToken.FamilyId}; additionally_revoked={family.Count}", ct: ct);

            DeleteRefreshCookie();
            return Unauthorized(new
            {
                error = "token_theft_detected",
                message = "Refresh token reuse detected. The affected session has been revoked."
            });
        }

        if (storedToken.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshRejected, "failure",
                storedToken.UserId, storedToken.TenantId, detail: "expired", ct: ct);
            return Unauthorized(new { error = "invalid_refresh_token" });
        }

        var user = storedToken.User ?? await _db.Users.FindAsync([storedToken.UserId], ct);
        if (user is null)
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshRejected, "failure",
                storedToken.UserId, storedToken.TenantId, detail: "user_missing", ct: ct);
            return Unauthorized(new { error = "invalid_refresh_token" });
        }

        if (user.LockedUntil > DateTimeOffset.UtcNow)
            return Unauthorized(new { error = "invalid_refresh_token" });

        var scope = await ResolveStoredScopeAsync(storedToken, user, ct);
        if (!scope.Allowed)
        {
            storedToken.RevokedAt = DateTimeOffset.UtcNow;
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshRejected, "denied",
                user.Id, storedToken.TenantId, detail: scope.Error, ct: ct);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = scope.Error,
                message = scope.Error == "tenant_suspended"
                    ? "Tenant subscription is suspended or expired."
                    : "The selected clinic is no longer available to this account."
            });
        }

        // Claim the token before issuing anything: two parallel refreshes must not both mint a
        // new token, or the loser's child silently defeats reuse detection.
        var now = DateTimeOffset.UtcNow;
        bool claimed;
        if (_db.Database.IsRelational())
        {
            claimed = await _db.RefreshTokens
                .Where(t => t.Id == storedToken.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct) == 1;
        }
        else
        {
            // shortcut: the in-memory provider has no conditional update, so re-read instead.
            var stillLive = await _db.RefreshTokens.AsNoTracking()
                .Where(t => t.Id == storedToken.Id)
                .Select(t => t.RevokedAt)
                .FirstOrDefaultAsync(ct) is null;
            claimed = stillLive;
            if (claimed) storedToken.RevokedAt = now;
        }

        if (!claimed)
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshRejected, "failure",
                user.Id, storedToken.TenantId, detail: "concurrent_rotation", ct: ct);
            return Unauthorized(new { error = "invalid_refresh_token" });
        }

        var (tokenExpiry, refreshExpiryDays) = TokenLifetimes();
        var familyId = storedToken.FamilyId is { } existingFamily && existingFamily != Guid.Empty
            ? existingFamily : Guid.NewGuid();
        // Bind the rotated parent too, so replay of a legacy token burns its new child.
        storedToken.FamilyId = familyId;
        var extraClaims = SessionClaims(user, scope.Role);
        extraClaims["session_id"] = familyId.ToString();
        if (scope.ClinicId.HasValue && scope.ClinicId.Value != Guid.Empty)
            extraClaims["clinicId"] = scope.ClinicId.Value.ToString();

        var newAccessToken = await GenerateJwtAsync(
            user.Id, user.Email, scope.Role.ToString(), scope.TenantId, tokenExpiry, ct, extraClaims);

        var newRefreshToken = Guid.NewGuid().ToString("N");
        _db.RefreshTokens.Add(NewRefreshToken(
            user.Id, newRefreshToken, scope.TenantId, scope.ClinicId,
            scope.Role, familyId, refreshExpiryDays));

        await _db.SaveChangesAsync(ct);
        AppendRefreshCookie(newRefreshToken, refreshExpiryDays);

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RefreshRotated, "success",
            user.Id, scope.TenantId, detail: $"family={storedToken.FamilyId}", ct: ct);

        return Ok(await BuildSessionResponseAsync(
            user, newAccessToken, tokenExpiry, scope.TenantId, scope.ClinicId, scope.Role, ct));
    }

    private sealed record ScopeResolution(
        bool Allowed, Guid? TenantId, Guid? ClinicId, UserRole Role, string? Error);

    /// <summary>
    /// Rebuilds the exact tenant/clinic/role the session was issued for. Falling back to
    /// <see cref="User.TenantId"/> here would silently move a multi-clinic practitioner.
    /// </summary>
    private async Task<ScopeResolution> ResolveStoredScopeAsync(
        RefreshToken token, User user, CancellationToken ct)
    {
        if (user.Role == UserRole.PlatformAdmin)
            return new ScopeResolution(token.Role == UserRole.PlatformAdmin && token.TenantId is null && token.ClinicId is null,
                null, null, UserRole.PlatformAdmin, "unauthorized_tenant");
        if (token.Role == UserRole.PlatformAdmin)
            return new ScopeResolution(false, null, null, token.Role, "unauthorized_tenant");

        var effectiveTenantId = token.TenantId;
        if (effectiveTenantId is null || effectiveTenantId == Guid.Empty || token.ClinicId is null || token.ClinicId == Guid.Empty)
            return new ScopeResolution(false, null, null, token.Role, "unauthorized_tenant");
        using var selectedTenant = _tenantContext?.PushTenant(effectiveTenantId.Value);

        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == effectiveTenantId.Value)
            .Select(t => new { t.SubscriptionStatus, t.SubscriptionEndsAt })
            .FirstOrDefaultAsync(ct);

        if (tenant is null)
            return new ScopeResolution(false, null, null, token.Role, "tenant_not_found");

        var access = SubscriptionPolicy.EffectiveAccess(
            tenant.SubscriptionStatus, tenant.SubscriptionEndsAt, DateTimeOffset.UtcNow);
        if (access == EffectiveAccessLevel.Blocked)
            return new ScopeResolution(false, null, null, token.Role, "tenant_suspended");

        var membership = await _db.TenantMemberships.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == user.Id
                && m.TenantId == token.TenantId
                && m.ClinicId == token.ClinicId
                && m.Status == "active" && m.Clinic != null && m.Clinic.TenantId == token.TenantId, ct);

        if (membership is not null && membership.Role != UserRole.PlatformAdmin && Enum.IsDefined(membership.Role))
            return new ScopeResolution(true, membership.TenantId, membership.ClinicId, membership.Role, null);

        return new ScopeResolution(false, null, null, token.Role, "unauthorized_tenant");
    }


    /// <summary>
    /// Switches the active clinic / tenant scope for a multi-clinic practitioner
    /// or completes clinic selection from a login challenge.
    /// Issues a new 15-minute token strictly scoped to the chosen tenant.
    /// </summary>
    [HttpPost("select-tenant")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> SelectTenant([FromBody] SelectTenantRequest request, CancellationToken ct)
    {
        if (User.HasClaim(c => c.Type is "impersonated_by" or "impersonation_id"))
            return Unauthorized(new { error = "invalid_credentials" });
        Guid? userId = User.GetUserId();
        if (!userId.HasValue && !string.IsNullOrWhiteSpace(request.SelectionToken))
        {
            userId = ReadTenantSelectionChallenge(request.SelectionToken);
        }

        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_credentials" });

        return await SessionMutation.RunAsync(_db, userId.Value, () => SelectTenantLockedAsync(userId.Value, request, ct), ct);
    }

    private async Task<IActionResult> SelectTenantLockedAsync(Guid userId, SelectTenantRequest request, CancellationToken ct)
    {
        if (User.GetUserId().HasValue && await CurrentSessionScopeAsync(ct) is null)
            return Unauthorized(new { error = "invalid_credentials" });
        var user = await _db.Users.FindAsync([userId], ct);
        if (user is null || user.LockedUntil > DateTimeOffset.UtcNow || user.Role == UserRole.PlatformAdmin)
            return Unauthorized(new { error = "invalid_credentials" });
        if (!User.GetUserId().HasValue && (ReadTenantSelectionChallenge(request.SelectionToken) != userId
            || !ChallengeMatches(request.SelectionToken, user)))
            return Unauthorized(new { error = "invalid_credentials" });

        var membership = await _db.TenantMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == user.Id
                && m.TenantId == request.TenantId
                && (request.ClinicId == null || m.ClinicId == request.ClinicId)
                && m.Status == "active", ct);

        if (membership is null || membership.Role == UserRole.PlatformAdmin || !Enum.IsDefined(membership.Role))
        {
            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.TenantSelected, "denied",
                user.Id, request.TenantId, user.Email, detail: "no_membership", ct: ct);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "unauthorized_tenant",
                message = "User has no active membership with the specified clinic/tenant."
            });
        }

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.TenantSelected, "success",
            user.Id, membership.TenantId, user.Email, detail: $"clinic={membership.ClinicId}", ct: ct);

        return await CheckSubscriptionAndIssueSessionAsync(user, membership.TenantId, membership.ClinicId, membership.Role, ct);
    }

    /// <summary>Lists all active clinic / tenant memberships for the current user.</summary>
    [HttpGet("memberships")]
    [Authorize]
    public async Task<IActionResult> GetMemberships(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "not_authenticated" });

        var memberships = await _db.TenantMemberships
            .AsNoTracking()
            .Include(m => m.Clinic)
            .Include(m => m.Tenant)
            .Where(m => m.UserId == userId.Value && m.Status == "active")
            .Select(m => new
            {
                tenantId = m.TenantId,
                tenantName = m.Tenant.Name,
                clinicId = m.ClinicId,
                clinicName = m.Clinic != null ? m.Clinic.Name : m.Tenant.Name,
                role = m.Role.ToString().ToLower(),
                isDefault = m.IsDefault,
                status = m.Status
            })
            .ToListAsync(ct);

        return Ok(memberships);
    }

    /// <summary>Revokes all active sessions / refresh tokens for the current user (front-desk shared PC safety).</summary>
    [HttpPost("revoke-all")]
    [Authorize]
    public async Task<IActionResult> RevokeAllSessions(CancellationToken ct)
        => await SessionMutation.RunAsync(_db, User.GetUserId() ?? Guid.Empty, () => RevokeAllSessionsLockedAsync(ct), ct);

    private async Task<IActionResult> RevokeAllSessionsLockedAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "not_authenticated" });

        var activeTokens = await _db.RefreshTokens
            .Where(t => t.UserId == userId.Value && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        DeleteRefreshCookie();

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.SessionsRevoked, "success",
            userId.Value, User.GetTenantId(), detail: $"revoked={activeTokens.Count}", ct: ct);

        return Ok(new { message = "all_sessions_revoked", revokedCount = activeTokens.Count });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        var rawToken = !string.IsNullOrWhiteSpace(request?.RefreshToken)
            ? request.RefreshToken
            : Request.Cookies["refreshToken"];

        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            // Same deterministic digest as refresh; a BCrypt scan here would silently
            // never match and leave the row alive after "log out".
            var tokenHash = rawToken.HashToken();
            var storedToken = await _db.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

            if (storedToken != null)
            {
                await SessionMutation.RunAsync(_db, storedToken.UserId, async () =>
                {
                    if (_db.Database.IsRelational()) await _db.Entry(storedToken).ReloadAsync(ct);
                    var family = await _db.RefreshTokens.Where(t => t.UserId == storedToken.UserId
                        && t.RevokedAt == null && (t.Id == storedToken.Id
                            || storedToken.FamilyId != null && t.FamilyId == storedToken.FamilyId)).ToListAsync(ct);
                    foreach (var token in family)
                        token.RevokedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    return true;
                }, ct);
            }
        }

        DeleteRefreshCookie();

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.Logout, "success",
            User.GetUserId(), User.GetTenantId(), ct: ct);

        return Ok(new { message = "logged_out" });
    }

    // ── A1: bootstrap hardening ──────────────────────────────────────────────
    // The production bootstrap command sets MustChangePassword; the token carries the flag
    // and AccountSetupEnforcementMiddleware allows only the calls below until it is cleared.

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
        => await SessionMutation.RunAsync(_db, User.GetUserId() ?? Guid.Empty, () => ChangePasswordLockedAsync(request, ct), ct);

    private async Task<IActionResult> ChangePasswordLockedAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        var scope = await CurrentSessionScopeAsync(ct);
        if (scope is null)
            return Unauthorized(new { error = "invalid_session" });
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "not_authenticated" });

        var user = await _db.Users.FindAsync([userId.Value], ct);
        if (user is null)
            return Unauthorized(new { error = "not_authenticated" });

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            !request.CurrentPassword.VerifyPassword(user.PasswordHash))
        {
            return BadRequest(new { error = "invalid_current_password" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest(new { error = "password_too_short", message = "Use at least 8 characters." });

        if (request.NewPassword == request.CurrentPassword)
            return BadRequest(new { error = "password_unchanged" });

        user.PasswordHash = request.NewPassword.HashPassword();
        user.MustChangePassword = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        // Sessions that predate the new password die with it.
        var liveTokens = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in liveTokens)
            token.RevokedAt = DateTimeOffset.UtcNow;

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.PasswordChanged, "success",
            user.Id, User.GetTenantId(), user.Email, ct: ct);

        // Keep the clinic the caller is currently scoped to; never snap back to the primary tenant.
        return await IssueSessionAsync(user, ct,
            scope.TenantId, scope.ClinicId, scope.Role);
    }

    private async Task<ScopeResolution?> CurrentSessionScopeAsync(CancellationToken ct)
    {
        if (User.HasClaim(c => c.Type is "impersonated_by" or "impersonation_id")
            || User.GetUserId() is not { } userId
            || User.FindAll("session_id").Count() != 1
            || !Guid.TryParse(User.FindFirst("session_id")?.Value, out var familyId) || familyId == Guid.Empty)
            return null;

        var now = DateTimeOffset.UtcNow;
        var user = await _db.Users.FindAsync([userId], ct);
        if (user is null || user.LockedUntil > now
            || user.MfaEnabled && !User.HasClaim("mfa_enabled", "true"))
            return null;
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        var token = await _db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId
            && t.FamilyId == familyId && t.TenantId == tenantId && t.ClinicId == clinicId
            && t.RevokedAt == null && t.ExpiresAt > now, ct);
        if (token is null) return null;
        var scope = await ResolveStoredScopeAsync(token, user, ct);
        return scope.Allowed ? scope : null;
    }

    /// <summary>Current second-factor state for the signed-in user.</summary>
    [HttpGet("mfa/status")]
    [Authorize]
    public async Task<IActionResult> GetMfaStatus(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "not_authenticated" });

        var user = await _db.Users.FindAsync([userId.Value], ct);
        if (user is null)
            return Unauthorized(new { error = "not_authenticated" });

        return Ok(new
        {
            enabled = user.MfaEnabled,
            method = user.MfaEnabled ? NormalizeMethod(user.MfaMethod) : null
        });
    }

    /// <summary>
    /// Starts (or resumes) enrolment. Both methods keep a server-side secret: TOTP signs the
    /// authenticator code, email signs the 6-digit code we mail out.
    /// </summary>
    [HttpPost("mfa/setup")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> StartMfaSetup([FromBody] MfaSetupRequest? request, CancellationToken ct)
        => await SessionMutation.RunAsync(_db, User.GetUserId() ?? Guid.Empty, () => StartMfaSetupLockedAsync(request, ct), ct);

    private async Task<IActionResult> StartMfaSetupLockedAsync(MfaSetupRequest? request, CancellationToken ct)
    {
        if (await CurrentSessionScopeAsync(ct) is null)
            return Unauthorized(new { error = "invalid_session" });
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "not_authenticated" });

        var user = await _db.Users.FindAsync([userId.Value], ct);
        if (user is null)
            return Unauthorized(new { error = "not_authenticated" });

        if (user.MfaEnabled)
            return BadRequest(new { error = "mfa_already_enabled" });

        var method = NormalizeMethod(request?.Method);

        // Reuse a pending secret: a double-submit must not race a second write (it used to 500).
        var secret = _secretProtector.Unprotect(user.MfaSecret);
        if (secret is null)
        {
            secret = TotpService.GenerateSecret();
            user.MfaSecret = _secretProtector.Protect(secret);
            user.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent setup won the write; read back what it stored.
                await _db.Entry(user).ReloadAsync(ct);
                secret = _secretProtector.Unprotect(user.MfaSecret);
                if (secret is null)
                    return StatusCode(StatusCodes.Status500InternalServerError, new { error = "mfa_setup_failed" });
            }
        }

        if (method == "email" && !await SendEmailCodeAsync(user, ct))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "email_delivery_failed",
                message = "Could not send the confirmation code. Check the mail relay, or use an authenticator app."
            });
        }

        return Ok(new
        {
            method,
            secret,
            otpauthUri = method == "totp"
                ? TotpService.BuildOtpAuthUri("SAMSTACK Care", user.Email, secret)
                : null
        });
    }

    [HttpPost("mfa/enable")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> EnableMfa([FromBody] MfaEnableRequest request, CancellationToken ct)
        => await SessionMutation.RunAsync(_db, User.GetUserId() ?? Guid.Empty, () => EnableMfaLockedAsync(request, ct), ct);

    private async Task<IActionResult> EnableMfaLockedAsync(MfaEnableRequest request, CancellationToken ct)
    {
        var scope = await CurrentSessionScopeAsync(ct);
        if (scope is null)
            return Unauthorized(new { error = "invalid_session" });
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "not_authenticated" });

        var user = await _db.Users.FindAsync([userId.Value], ct);
        if (user is null)
            return Unauthorized(new { error = "not_authenticated" });

        if (user.MfaEnabled)
            return BadRequest(new { error = "mfa_already_enabled" });

        var secret = _secretProtector.Unprotect(user.MfaSecret);
        if (secret is null)
            return BadRequest(new { error = "mfa_not_initialized" });

        var method = NormalizeMethod(request.Method ?? user.MfaMethod);
        if (!VerifyCode(secret, request.Code, method))
            return BadRequest(new { error = "invalid_code" });

        user.MfaEnabled = true;
        user.MfaMethod = method;
        var liveTokens = await _db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in liveTokens)
            token.RevokedAt = DateTimeOffset.UtcNow;

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.MfaEnabled, "success",
            user.Id, User.GetTenantId(), user.Email, method, ct);

        // A fresh session so the mfa_enabled claim is current. Keep the selected clinic.
        return await IssueSessionAsync(user, ct,
            scope.TenantId, scope.ClinicId, scope.Role);
    }

    [HttpPost("mfa/disable")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DisableMfa([FromBody] MfaDisableRequest request, CancellationToken ct)
        => await SessionMutation.RunAsync(_db, User.GetUserId() ?? Guid.Empty, () => DisableMfaLockedAsync(request, ct), ct);

    private async Task<IActionResult> DisableMfaLockedAsync(MfaDisableRequest request, CancellationToken ct)
    {
        var scope = await CurrentSessionScopeAsync(ct);
        if (scope is null)
            return Unauthorized(new { error = "invalid_session" });
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "not_authenticated" });

        var user = await _db.Users.FindAsync([userId.Value], ct);
        if (user is null)
            return Unauthorized(new { error = "not_authenticated" });

        if (!user.MfaEnabled)
            return Ok(new { mfaEnabled = false });

        // Mandatory for money/clinical/platform roles; self-service disable would defeat it.
        // Check the scoped role as well as the account's primary role: a multi-clinic user
        // must not be able to strip MFA from a nurse-scoped session.
        var membershipRoles = await _db.TenantMemberships.Where(m => m.UserId == user.Id && m.Status == "active")
            .Select(m => m.Role).ToListAsync(ct);
        if (MfaGateOn && (MfaPolicy.RequiresEnrolment(user.Role) || MfaPolicy.RequiresEnrolment(scope.Role)
            || membershipRoles.Any(MfaPolicy.RequiresEnrolment)))
            return BadRequest(new
            {
                error = "mfa_required_for_role",
                message = "Two-factor authentication is mandatory for this role and cannot be disabled."
            });

        // Password re-entry: a stolen session alone must not strip the second factor.
        if (string.IsNullOrWhiteSpace(request.Password) || !request.Password.VerifyPassword(user.PasswordHash))
            return BadRequest(new { error = "invalid_password" });

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaMethod = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.MfaDisabled, "success",
            user.Id, User.GetTenantId(), user.Email, ct: ct);

        return Ok(new { mfaEnabled = false });
    }

    /// <summary>Re-sends an email sign-in code for a live challenge.</summary>
    [HttpPost("mfa/resend")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendMfaCode([FromBody] MfaResendRequest request, CancellationToken ct)
    {
        var userId = ReadMfaChallenge(request.ChallengeToken);
        if (userId is null)
            return Unauthorized(new { error = "invalid_challenge" });

        var user = await _db.Users.FindAsync([userId.Value], ct);
        if (user is null || !user.MfaEnabled || NormalizeMethod(user.MfaMethod) != "email"
            || !ChallengeMatches(request.ChallengeToken, user))
            return Unauthorized(new { error = "invalid_challenge" });

        if (!await SendEmailCodeAsync(user, ct))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "email_delivery_failed" });

        return Ok(new { sent = true });
    }

    /// <summary>Second login step for a user with a second factor enrolled.</summary>
    [HttpPost("mfa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> VerifyMfa([FromBody] MfaVerifyRequest request, CancellationToken ct)
    {
        var userId = ReadMfaChallenge(request.ChallengeToken);
        if (userId is null)
            return Unauthorized(new { error = "invalid_challenge" });

        return await SessionMutation.RunAsync(_db, userId.Value, () => VerifyMfaLockedAsync(userId.Value, request, ct), ct);
    }

    private async Task<IActionResult> VerifyMfaLockedAsync(Guid userId, MfaVerifyRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([userId], ct);
        if (user is null || !user.MfaEnabled || ReadMfaChallenge(request.ChallengeToken) != userId
            || !ChallengeMatches(request.ChallengeToken, user))
            return Unauthorized(new { error = "invalid_challenge" });

        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTimeOffset.UtcNow)
            return Unauthorized(new { error = "invalid_challenge" });

        var secret = _secretProtector.Unprotect(user.MfaSecret);
        if (secret is null || !VerifyCode(secret, request.Code, NormalizeMethod(user.MfaMethod)))
        {
            var maxAttempts = _config.GetValue<int>("Auth:MaxFailedAttempts", 5);
            var lockMinutes = _config.GetValue<int>("Auth:LockoutMinutes", 15);

            user.FailedLoginCount++;
            if (user.FailedLoginCount >= maxAttempts)
                user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(lockMinutes);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.MfaFailed, "failure",
                user.Id, user.TenantId, user.Email,
                user.LockedUntil.HasValue ? "bad_code_locked" : "bad_code", ct);

            return Unauthorized(new { error = "invalid_code" });
        }

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.MfaVerified, "success",
            user.Id, user.TenantId, user.Email, NormalizeMethod(user.MfaMethod), ct);

        return await ResolveTenantAndIssueSessionAsync(user, null, ct);
    }

    /// <summary>"email" is the only alternative to the authenticator app; anything else means TOTP.</summary>
    private static string NormalizeMethod(string? method) =>
        string.Equals(method, "email", StringComparison.OrdinalIgnoreCase) ? "email" : "totp";

    private static bool VerifyCode(string secret, string? code, string method) =>
        method == "email"
            ? TotpService.Verify(secret, code, DateTimeOffset.UtcNow, skewSteps: 1, periodSeconds: TotpService.EmailPeriodSeconds)
            : TotpService.Verify(secret, code, DateTimeOffset.UtcNow);

    private async Task<bool> SendEmailCodeAsync(User user, CancellationToken ct)
    {
        var secret = _secretProtector.Unprotect(user.MfaSecret);
        if (secret is null)
            return false;

        var code = TotpService.ComputeCode(secret, DateTimeOffset.UtcNow, TotpService.EmailPeriodSeconds);

        return await _email.SendAsync(
            user.Email,
            "Your SAMSTACK sign-in code",
            $"Your sign-in code is {code}. It expires in 5 minutes. " +
            "If you did not try to sign in, change your password.",
            ct);
    }

    private string CreateMfaChallenge(User user) =>
        _secretProtector.Protect($"mfa|{user.Id}|{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}|{CredentialVersion(user)}");

    private static string CredentialVersion(User user) =>
        $"{user.PasswordHash}|{user.MfaSecret}|{user.MfaEnabled}".HashToken();

    private bool ChallengeMatches(string? challenge, User user) =>
        _secretProtector.Unprotect(challenge)?.Split('|') is { Length: 4 } parts
        && parts[3] == CredentialVersion(user);

    private Guid? ReadMfaChallenge(string? challengeToken)
    {
        if (string.IsNullOrWhiteSpace(challengeToken))
            return null;

        var raw = _secretProtector.Unprotect(challengeToken);
        if (raw is null)
            return null;

        var parts = raw.Split('|');
        if (parts.Length != 4 || parts[0] != "mfa")
            return null;

        if (!long.TryParse(parts[2], out var expiresAt) ||
            expiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return null;
        }

        return Guid.TryParse(parts[1], out var userId) ? userId : null;
    }

    private string CreateTenantSelectionChallenge(User user) =>
        _secretProtector.Protect($"tsel|{user.Id}|{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}|{CredentialVersion(user)}");

    private Guid? ReadTenantSelectionChallenge(string? selectionToken)
    {
        if (string.IsNullOrWhiteSpace(selectionToken))
            return null;

        var raw = _secretProtector.Unprotect(selectionToken);
        if (raw is null)
            return null;

        var parts = raw.Split('|');
        if (parts.Length != 4 || parts[0] != "tsel")
            return null;

        if (!long.TryParse(parts[2], out var expiresAt) ||
            expiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return null;
        }

        return Guid.TryParse(parts[1], out var userId) ? userId : null;
    }

    [HttpGet(".well-known/jwks.json")]
    public IActionResult GetJwks()
    {
        return Ok(_keyService.GetJwks());
    }

    private async Task<string> GenerateJwtAsync(Guid userId, string email, string role, Guid? tenantId, int expiryMinutes,
        CancellationToken ct, IReadOnlyDictionary<string, object?>? extraClaims = null)
    {
        var issuer = _config["Jwt:Issuer"] ?? "Hospital_CRM";
        var audience = _config["Jwt:Audience"] ?? "Hospital_CRM";
        var keyId = _config["Jwt:KeyId"] ?? "hospital-crm-rsa-key-1";
        var expires = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = JsonSerializer.SerializeToElement(new { alg = "RS256", typ = "JWT", kid = keyId });
        var payloadDict = new Dictionary<string, object?>
        {
            ["sub"] = userId.ToString(),
            ["email"] = email,
            ["role"] = role,
            ["jti"] = Guid.NewGuid().ToString(),
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = expires,
            ["iat"] = now
        };

        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            payloadDict["tenant_id"] = tenantId.Value.ToString();
        }

        if (extraClaims is not null)
        {
            foreach (var (name, value) in extraClaims)
                payloadDict[name] = value;
        }

        var payload = JsonSerializer.SerializeToElement(payloadDict);

        var headerB64 = Base64UrlEncode(header.GetRawText());
        var payloadB64 = Base64UrlEncode(payload.GetRawText());
        var signingInput = $"{headerB64}.{payloadB64}";
        var signature = await _keyService.SignAsync(Encoding.UTF8.GetBytes(signingInput), ct);
        var signatureB64 = Base64UrlEncode(signature);

        return $"{signingInput}.{signatureB64}";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlEncode(string text) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(text));
}

public record LoginRequest(
    [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Please enter a valid email address")]
    string Email,

    [Required(ErrorMessage = "Password is required")]
    string Password,

    Guid? TenantId = null);

public record SelectTenantRequest(
    Guid TenantId,
    Guid? ClinicId = null,
    string? SelectionToken = null);

public record RefreshRequest(
    string? RefreshToken = null);

public record ChangePasswordRequest(
    string? CurrentPassword,
    string? NewPassword);

public record MfaSetupRequest(string? Method = null);

public record MfaEnableRequest(string? Code, string? Method = null);

public record MfaVerifyRequest(string? ChallengeToken, string? Code);

public record MfaResendRequest(string? ChallengeToken);

public record MfaDisableRequest(string? Password);
