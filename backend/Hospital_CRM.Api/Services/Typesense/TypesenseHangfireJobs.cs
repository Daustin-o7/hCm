using System.Diagnostics;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Typesense;

namespace Hospital_CRM.Api.Services.Typesense;

public interface ITypesenseHangfireJobs
{
    Task ReindexTypesenseAsync(CancellationToken ct = default);
    Task ReindexMedicinesTypesenseAsync(CancellationToken ct = default);
}

/// <summary>
/// Hangfire job for Typesense background maintenance and reconciliation.
/// Injected via DI by Hangfire AspNetCoreJobActivator.
/// </summary>
public class TypesenseHangfireJobs : ITypesenseHangfireJobs
{
    private readonly HospitalCrmDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITypesenseClient _ts;
    private readonly TypesenseOptions _options;
    private readonly ILogger<TypesenseHangfireJobs> _log;

    public TypesenseHangfireJobs(
        HospitalCrmDbContext db,
        IServiceScopeFactory scopeFactory,
        ITypesenseClientFactory factory,
        IOptions<TypesenseOptions> options,
        ILogger<TypesenseHangfireJobs> log)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _ts = factory.Create();
        _options = options.Value;
        _log = log;
    }

    public Task ReindexTypesenseAsync(CancellationToken ct = default) => ReindexAsync(false, ct);

    public Task ReindexMedicinesTypesenseAsync(CancellationToken ct = default) => ReindexAsync(true, ct);

    private async Task ReindexAsync(bool medicines, CancellationToken ct)
    {
        var collection = medicines ? _options.MedicinesCollection : _options.PatientsCollection;
        Guid? activeTenant = null;
        var count = 0;
        try
        {
            // Only tenant IDs may be inventoried outside a tenant-bound context.
            var tenants = await _db.Tenants.AsNoTracking().Where(t => t.Id != Guid.Empty).Select(t => t.Id).ToListAsync(ct);
            foreach (var tenantId in tenants)
            {
                activeTenant = tenantId;
                count = 0;
                var started = Stopwatch.GetTimestamp();
                using var scope = _scopeFactory.CreateScope();
                using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContext>().PushTenant(tenantId);
                var db = scope.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
                var search = scope.ServiceProvider.GetRequiredService<IPatientSearchService>();
                if (db.CurrentTenantId != tenantId || db.BypassTenantFilter)
                    throw new UnauthorizedAccessException("Reindex requires an isolated tenant context.");

                Guid? after = null;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int batchCount;
                    if (medicines)
                    {
                        var query = db.Drugs.AsNoTracking().Where(d => d.TenantId == tenantId);
                        if (after.HasValue) query = query.Where(d => d.Id.CompareTo(after.Value) > 0);
                        var batch = await query.OrderBy(d => d.Id).Take(1000).ToListAsync(ct);
                        batchCount = batch.Count;
                        if (batchCount == 0) break;
                        await search.IndexManyMedicinesAsync(batch, ct);
                        after = batch[^1].Id;
                    }
                    else
                    {
                        var query = db.Patients.AsNoTracking().Where(p => p.TenantId == tenantId);
                        if (after.HasValue) query = query.Where(p => p.Id.CompareTo(after.Value) > 0);
                        var batch = await query.OrderBy(p => p.Id).Take(1000).ToListAsync(ct);
                        batchCount = batch.Count;
                        if (batchCount == 0) break;
                        await search.IndexManyAsync(batch, ct);
                        after = batch[^1].Id;
                    }
                    count += batchCount;
                    if (batchCount < 1000) break;
                }

                var deleted = await RemoveMissingDocumentsAsync(db, collection, medicines, tenantId, ct);
                _log.LogInformation("Typesense {Collection} reconciled tenant {TenantId}: {Count} indexed, {Deleted} removed, {ElapsedMs} ms, completed {CompletedAt}",
                    collection, tenantId, count, deleted, Stopwatch.GetElapsedTime(started).TotalMilliseconds, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _log.LogError("Typesense {Collection} reconciliation failed for tenant {TenantId} after {Count} indexed ({FailureType}); retry required",
                collection, activeTenant, count, ex.GetType().Name);
            // Provider exceptions can contain imported patient documents; keep job failure storage PHI-free.
            throw new InvalidOperationException("Typesense reconciliation failed; retry required.");
        }
    }

    private async Task<int> RemoveMissingDocumentsAsync(HospitalCrmDbContext db, string collection, bool medicines, Guid tenantId, CancellationToken ct)
    {
        var page = 1;
        var removed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _ts.Search<TypesensePatientDocument>(collection, new SearchParameters("*", "name")
            {
                FilterBy = $"tenant_id:={tenantId}",
                IncludeFields = "id,tenant_id",
                SortBy = "created_at:asc",
                Page = page,
                PerPage = 100
            }, ct);
            if (result.Hits.Count == 0) return removed;
            if (result.Hits.Any(h => !Guid.TryParse(h.Document.TenantId, out var owner) || owner != tenantId
                || !Guid.TryParse(h.Document.Id, out var id) || id == Guid.Empty))
                throw new InvalidOperationException("Invalid scoped index inventory.");

            var ids = result.Hits.Select(h => Guid.Parse(h.Document.Id)).ToArray();
            var existing = medicines
                ? await db.Drugs.Where(d => d.TenantId == tenantId && ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct)
                : await db.Patients.Where(p => p.TenantId == tenantId && ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
            var stale = ids.Except(existing).ToArray();
            if (stale.Length > 0)
            {
                ct.ThrowIfCancellationRequested();
                var deletion = await _ts.DeleteDocuments(collection, $"tenant_id:={tenantId} && id:=[{string.Join(',', stale)}]");
                ct.ThrowIfCancellationRequested();
                if (deletion.NumberOfDeleted == 0)
                    throw new InvalidOperationException("Index cleanup made no progress; retry reconciliation.");
                removed += deletion.NumberOfDeleted;
                // Deletions shift later hits into this page; reread it before advancing.
                continue;
            }
            if (result.Hits.Count < 100) return removed;
            page++;
        }
    }
}
