using Hangfire.Dashboard;

namespace Hospital_CRM.Api.Authorization;

/// <summary>
/// The shared job store is available only to non-impersonating platform operators.
/// The existing JWT auth middleware has already populated the user principal.
/// </summary>
public class HangfireAdminAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var user = http.User;
        if (user?.Identity?.IsAuthenticated != true) return false;
        return user.IsInRole("PlatformAdmin") && !user.HasClaim(c => c.Type is "impersonated_by" or "impersonation_id");
    }
}
