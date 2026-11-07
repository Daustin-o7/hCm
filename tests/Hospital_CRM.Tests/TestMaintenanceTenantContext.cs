using Hospital_CRM.Domain.Services;

namespace Hospital_CRM.Tests;

/// <summary>
/// Explicit cross-tenant seeding/inspection for unscoped unit fixtures; not evidence of RLS isolation.
/// Tenant-boundary tests must use a real tenant scope or intentional missing context.
/// </summary>
internal sealed class TestMaintenanceTenantContext : ITenantContext
{
    public static TestMaintenanceTenantContext Instance { get; } = new();

    private TestMaintenanceTenantContext() { }

    public Guid? CurrentTenantId => null;
    public bool IsPlatformAdmin => true;

    public IDisposable PushTenant(Guid tenantId) =>
        throw new NotSupportedException("Use a real TenantContext for tenant-scoped tests.");
}
