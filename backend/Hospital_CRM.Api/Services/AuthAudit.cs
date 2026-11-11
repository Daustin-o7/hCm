using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public static class AuthEvent
{
    public const string LoginSucceeded = "login_succeeded";
    public const string LoginFailed = "login_failed";
    public const string LoginLocked = "login_locked";
    public const string MfaRequired = "mfa_challenge";
    public const string MfaVerified = "mfa_verified";
    public const string MfaFailed = "mfa_failed";
    public const string MfaEnabled = "mfa_enabled";
    public const string MfaDisabled = "mfa_disabled";
    public const string RefreshRotated = "refresh_rotated";
    public const string RefreshReuseDetected = "refresh_reuse_detected";
    public const string RefreshRejected = "refresh_rejected";
    public const string Logout = "logout";
    public const string SessionsRevoked = "sessions_revoked";
    public const string TenantSelected = "tenant_selected";
    public const string PasswordChanged = "password_changed";
    public const string RoleChanged = "role_changed";
    public const string RoleGranted = "role_granted";
    public const string BreakGlass = "break_glass_access";
    public const string SigningCredentialsUpdated = "signing_credentials_updated";
}

public static class AuthAudit
{
    public static async Task LogAsync(
        HospitalCrmDbContext db,
        HttpContext? http,
        string eventType,
        string outcome,
        Guid? userId = null,
        Guid? tenantId = null,
        string? email = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        db.AuthAuditLogs.Add(new AuthAuditLog
        {
            EventType = eventType,
            Outcome = outcome,
            UserId = userId,
            TenantId = tenantId,
            Email = email,
            Detail = detail,
            IpAddress = http?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http?.Request.Headers.UserAgent.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // An audit write must never turn a valid login into a 500. The failure is
            // still surfaced by the database role's lack of UPDATE/DELETE on the table.
        }
    }
}
