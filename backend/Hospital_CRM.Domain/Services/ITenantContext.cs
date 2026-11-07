namespace Hospital_CRM.Domain.Services;

/// <summary>
/// Scoped ambient context providing the resolved TenantId and role indicators
/// for the current operation (HTTP request, background worker, or batch job).
/// </summary>
public interface ITenantContext
{
    Guid? CurrentTenantId { get; }
    bool IsPlatformAdmin { get; }

    /// <summary>
    /// Scopes the ambient tenant explicitly (background jobs, webhook routing, tests).
    /// Dispose to restore the previous scope.
    /// </summary>
    IDisposable PushTenant(Guid tenantId);

    /// <summary>Explicit no-HTTP startup/maintenance inventory only; tenant business work uses PushTenant.</summary>
    IDisposable PushMaintenance() => throw new NotSupportedException("Maintenance scope is not available.");
}
