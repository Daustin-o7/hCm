using System.Security.Claims;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
public class AuthorizationController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IConfiguration? _config;
    private readonly ITenantContext? _tenantContext;

    public AuthorizationController(HospitalCrmDbContext db,
        IOpenIddictApplicationManager applicationManager, IOpenIddictScopeManager scopeManager,
        IConfiguration? config = null, ITenantContext? tenantContext = null)
    {
        _db = db;
        _config = config;
        _tenantContext = tenantContext;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction?.Request
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        var result = await HttpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
            return Deny(Errors.LoginRequired);

        var principal = await CurrentPrincipalAsync(result.Principal, HttpContext.RequestAborted);
        if (principal is null)
            return Deny(Errors.LoginRequired);
        principal.SetScopes(request.GetScopes());
        principal.SetResources("api");
        await AuditAsync(principal, "oidc_authorize", "success");
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction?.Request
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // The server enables only authorization-code + PKCE and refresh; password is deliberately unsupported.
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return Deny(Errors.UnsupportedGrantType);

        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
            return Deny(Errors.InvalidGrant);

        var principal = await CurrentPrincipalAsync(result.Principal, HttpContext.RequestAborted);
        if (principal is null)
        {
            await AuditAsync(result.Principal, "oidc_exchange", "denied");
            return Deny(Errors.InvalidGrant);
        }
        await AuditAsync(principal, "oidc_exchange", "success");
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Produces("application/json")]
    public async Task<IActionResult> Userinfo()
    {
        var principal = await CurrentPrincipalAsync(User, HttpContext.RequestAborted);
        if (principal is null)
            return Challenge(new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken
            }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        return Ok(principal.Claims.Where(c => c.Type is Claims.Subject or Claims.Name or Claims.Email
                or Claims.Role or "tenant_id" or "clinicId")
            .ToDictionary(c => c.Type, c => c.Value, StringComparer.Ordinal));
    }

    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> LogOut()
    {
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Succeeded && result.Principal is { } principal && HasUnambiguousAuthority(principal)
            && principal.GetUserId() is { } userId
            && Guid.TryParse(principal.GetClaim("session_id"), out var familyId))
        {
            await SessionMutation.RunAsync(_db, userId, async () =>
            {
                var tokens = await _db.RefreshTokens.Where(t => t.UserId == userId
                    && t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(HttpContext.RequestAborted);
                foreach (var token in tokens) token.RevokedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(HttpContext.RequestAborted);
                await AuditAsync(principal, AuthEvent.Logout, "success");
                return true;
            }, HttpContext.RequestAborted);
        }
        return SignOut(new AuthenticationProperties { RedirectUri = "/" },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private ForbidResult Deny(string error) => Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Re-authenticate and select an available clinic."
    }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    private Task AuditAsync(ClaimsPrincipal principal, string eventType, string outcome) =>
        AuthAudit.LogAsync(_db, HttpContext, eventType, outcome, principal.GetUserId(), principal.GetTenantId(),
            ct: HttpContext.RequestAborted);

    private static bool HasUnambiguousAuthority(ClaimsPrincipal principal) => principal.Claims
        .Where(c => c.Type is Claims.Subject or ClaimTypes.NameIdentifier or ClaimTypes.Name
            or "tenant_id" or "tenantId" or "clinicId" or "clinic_id" or Claims.Role or ClaimTypes.Role
            or "session_id" or "must_change_password" or "mfa_enrolment_required" or "mfa_enabled")
        .GroupBy(c => c.Type switch
        {
            ClaimTypes.NameIdentifier or ClaimTypes.Name => Claims.Subject,
            "tenantId" => "tenant_id",
            "clinic_id" => "clinicId",
            ClaimTypes.Role => Claims.Role,
            _ => c.Type
        })
        .All(group => group.Count() == 1);

    private async Task<ClaimsPrincipal?> CurrentPrincipalAsync(ClaimsPrincipal source, CancellationToken ct)
    {
        // Duplicate claims/aliases require re-authentication, never first-match authority selection.
        if (!HasUnambiguousAuthority(source) || source.GetUserId() is not { } userId
            || source.HasClaim(c => c.Type is "impersonated_by" or "impersonation_id")
            || source.GetClaim("must_change_password") == "true"
            || source.GetClaim("mfa_enrolment_required") == "true"
            || !Guid.TryParse(source.GetClaim("session_id"), out var familyId) || familyId == Guid.Empty)
            return null;

        return await SessionMutation.RunAsync(_db, userId,
            () => CurrentPrincipalLockedAsync(source, userId, familyId, ct), ct);
    }

    private async Task<ClaimsPrincipal?> CurrentPrincipalLockedAsync(ClaimsPrincipal source, Guid userId, Guid familyId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        var now = DateTimeOffset.UtcNow;
        if (user is null || user.MustChangePassword || user.LockedUntil > now
            || (user.MfaEnabled && source.GetClaim("mfa_enabled") != "true"))
            return null;

        var tenantId = source.GetTenantId();
        var clinicId = source.GetClinicId();
        UserRole role;
        if (user.Role == UserRole.PlatformAdmin)
        {
            if (!string.Equals(source.GetUserRole(), nameof(UserRole.PlatformAdmin), StringComparison.OrdinalIgnoreCase)
                || source.HasClaim(c => c.Type is "tenant_id" or "tenantId" or "clinicId" or "clinic_id"))
                return null;
            tenantId = null;
            clinicId = null;
            role = UserRole.PlatformAdmin;
        }
        else
        {
            if (tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty)
                return null;

            using var scope = _tenantContext?.PushTenant(tenantId.Value);
            if (_db.Database.IsRelational())
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('app.current_tenant_id', {tenantId.Value.ToString()}, true), set_config('app.is_platform_admin', 'false', true)", ct);
            var membership = await _db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(m =>
                m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active"
                && m.Role != UserRole.PlatformAdmin && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
            if (membership is null || !Enum.IsDefined(membership.Role)) return null;
            role = membership.Role;
            var tenant = await _db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null || SubscriptionPolicy.EffectiveAccess(tenant.SubscriptionStatus, tenant.SubscriptionEndsAt, now)
                == EffectiveAccessLevel.Blocked) return null;
        }

        if ((_config?.GetValue("Mfa:RequireEnrolment", true) ?? true) && MfaPolicy.RequiresEnrolment(role) && !user.MfaEnabled)
            return null;

        // Bind every OIDC grant to the existing API refresh family; never infer a session from another live login.
        if (!await _db.RefreshTokens.AnyAsync(t => t.UserId == userId && t.FamilyId == familyId
            && t.TenantId == tenantId && t.ClinicId == clinicId && t.RevokedAt == null && t.ExpiresAt > now, ct))
            return null;

        // Preserve OpenIddict's validated protocol metadata, but rebuild all application claims from current authority.
        var identity = new ClaimsIdentity(source.Claims.Where(c => c.Type.StartsWith("oi_", StringComparison.Ordinal)),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.Name, user.Name)
            .SetClaim(Claims.Role, role.ToString().ToLowerInvariant())
            .SetClaim("tenant_id", tenantId?.ToString())
            .SetClaim("clinicId", clinicId?.ToString())
            .SetClaim("session_id", familyId.ToString())
            .SetClaim("mfa_enabled", user.MfaEnabled ? "true" : null);
        identity.SetDestinations(GetDestinations);
        return new ClaimsPrincipal(identity);
    }

    private static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        Claims.Subject or Claims.Name or Claims.Email or Claims.Role or "tenant_id" or "clinicId" or "session_id"
            => [Destinations.AccessToken, Destinations.IdentityToken],
        "mfa_enabled" => [Destinations.AccessToken],
        _ => []
    };
}
