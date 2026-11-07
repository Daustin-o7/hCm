using System.Security.Claims;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Authorization;

internal static class NursingScope
{
    internal static Task<TenantMembership?> SelectedAsync(HospitalCrmDbContext db, ClaimsPrincipal user,
        CancellationToken ct, params UserRole[] roles)
    {
        var userId = user.GetUserId();
        var tenantId = user.GetTenantId();
        var clinicId = user.GetClinicId();
        if (userId is null || tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty ||
            !Enum.TryParse<UserRole>(user.GetUserRole(), true, out var role) ||
            (!roles.Contains(role) && !(role == UserRole.Owner && roles.Contains(UserRole.ClinicAdmin))))
            return Task.FromResult<TenantMembership?>(null);

        return db.TenantMemberships.AsNoTracking().Include(m => m.User).SingleOrDefaultAsync(m =>
            m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active" &&
            m.Role == role && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
    }

    internal static IQueryable<NurseProfile> Profiles(HospitalCrmDbContext db, TenantMembership scope) =>
        db.NurseProfiles.Where(n => n.TenantId == scope.TenantId && db.TenantMemberships.Any(m =>
            m.UserId == n.UserId && m.TenantId == scope.TenantId && m.ClinicId == scope.ClinicId &&
            m.Status == "active" && m.Role == UserRole.Nurse));

    internal static IQueryable<TenantMembership> ProxyParties(HospitalCrmDbContext db, TenantMembership scope) =>
        db.TenantMemberships.Where(m => m.TenantId == scope.TenantId && m.ClinicId == scope.ClinicId &&
            m.Status == "active" && (m.Role == UserRole.ClinicAdmin || m.Role == UserRole.Nurse));

    internal static async Task<AuthorityProxy?> ActiveProxyAsync(HospitalCrmDbContext db, TenantMembership scope,
        string permission, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var parties = ProxyParties(db, scope);
        var proxies = await db.AuthorityProxies.AsNoTracking().Where(p =>
            p.TenantId == scope.TenantId && p.ClinicId == scope.ClinicId && p.DesigneeUserId == scope.UserId &&
            p.DelegatorUserId != p.DesigneeUserId && !p.IsRevoked && p.ValidFrom <= now && p.ValidUntil >= now &&
            parties.Any(m => m.UserId == p.DelegatorUserId) && parties.Any(m => m.UserId == p.DesigneeUserId))
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct);

        // Exact tokens: a stored suffix such as "leave.approve.all" never grants approval.
        return proxies.FirstOrDefault(p => p.Permissions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .All(v => v is "roster.approve" or "leave.approve") &&
            p.Permissions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(permission));
    }
}
