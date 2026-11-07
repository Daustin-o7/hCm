using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Hospital_CRM.Api.Services;

public class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly AsyncLocal<Guid?> _explicitTenantId = new();
    private readonly AsyncLocal<bool> _maintenance = new();

    public TenantContext(IHttpContextAccessor? httpContextAccessor = null)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? CurrentTenantId
    {
        get
        {
            if (_explicitTenantId.Value.HasValue)
                return _explicitTenantId.Value;

            var user = _httpContextAccessor?.HttpContext?.User;
            var tenantId = user?.Identity?.IsAuthenticated == true ? user.GetTenantId() : null;
            return tenantId == Guid.Empty ? null : tenantId;
        }
    }

    public bool IsPlatformAdmin
    {
        get
        {
            // A routed public link or tenant job must never inherit platform authority.
            if (_explicitTenantId.Value.HasValue)
                return false;
            var httpContext = _httpContextAccessor?.HttpContext;
            if (httpContext == null)
                return _maintenance.Value;

            var user = httpContext.User;
            var role = user?.GetUserRole();
            return user?.Identity?.IsAuthenticated == true &&
                string.Equals(role, "PlatformAdmin", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Explicitly scopes the tenant for background jobs, impersonation flows, or tests.
    /// Returns an IDisposable that restores previous tenant scope upon disposal.
    /// </summary>
    public IDisposable PushTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant scope requires a non-empty tenant ID.", nameof(tenantId));
        var prior = _explicitTenantId.Value;
        _explicitTenantId.Value = tenantId;
        return new ActionDisposable(() => _explicitTenantId.Value = prior);
    }

    public IDisposable PushMaintenance()
    {
        if (_httpContextAccessor?.HttpContext != null || _explicitTenantId.Value.HasValue)
            throw new InvalidOperationException("Maintenance scope is restricted to unscoped, non-HTTP operations.");
        var prior = _maintenance.Value;
        _maintenance.Value = true;
        return new ActionDisposable(() => _maintenance.Value = prior);
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public ActionDisposable(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _onDispose();
            }
        }
    }
}
