using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Typesense;

namespace Hospital_CRM.Api.Services.Typesense;

public interface IPatientSearchService
{
    Task IndexAsync(Patient patient, CancellationToken ct);
    Task IndexManyAsync(IEnumerable<Patient> patients, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<List<PatientSearchHit>> SearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default);
    Task<List<PatientSearchHit>> CheckDuplicatesAsync(string name, string? phone, DateOnly? dob, Guid tenantId, CancellationToken ct = default);
    Task IndexMedicineAsync(Drug drug, CancellationToken ct);
    Task IndexManyMedicinesAsync(IEnumerable<Drug> drugs, CancellationToken ct);
    Task<List<MedicineSearchHit>> MedicinesSearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default);
    Task EnsureCollectionAsync(CancellationToken ct);
    Task EnsureCollectionsAsync(CancellationToken ct);
}

public record PatientSearchHit(
    Guid Id,
    string Name,
    string Phone,
    string? Dob,
    string? Gender,
    string? Address,
    int Score);

public record MedicineSearchHit(
    Guid Id,
    string Name,
    string? GenericName,
    string? CommonBrands,
    string? Strength,
    string? DosageForm,
    int Score);

/// <summary>
/// Patient and Medicine search via self-hosted Typesense with automatic PostgreSQL fallback.
/// Await write-through after the database commit; provider failures are repaired by nightly reconciliation.
/// PostgreSQL validates ownership and supplies current demographics, including on stale or empty indexes.
/// </summary>
public class PatientSearchService : IPatientSearchService
{
    private readonly ITypesenseClient _ts;
    private readonly TypesenseOptions _opts;
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<PatientSearchService> _log;

    public PatientSearchService(
        ITypesenseClientFactory factory,
        IOptions<TypesenseOptions> opts,
        HospitalCrmDbContext db,
        ILogger<PatientSearchService> log)
    {
        _ts = factory.Create();
        _opts = opts.Value;
        _db = db;
        _log = log;
    }

    public async Task EnsureCollectionsAsync(CancellationToken ct)
    {
        await EnsurePatientCollectionAsync(ct);
        await EnsureMedicineCollectionAsync(ct);
    }

    public Task EnsureCollectionAsync(CancellationToken ct) => EnsureCollectionsAsync(ct);

    private async Task EnsurePatientCollectionAsync(CancellationToken ct)
    {
        try
        {
            await _ts.RetrieveCollection(_opts.PatientsCollection, ct);
            _log.LogInformation("Typesense collection {Collection} verified", _opts.PatientsCollection);
        }
        catch (TypesenseApiNotFoundException)
        {
            var fields = new List<Field>
            {
                new Field("id", FieldType.String),
                new Field("tenant_id", FieldType.String) { Facet = true },
                new Field("name", FieldType.String) { Sort = true },
                new Field("phone", FieldType.String),
                new Field("dob", FieldType.String) { Optional = true },
                new Field("gender", FieldType.String) { Optional = true },
                new Field("address", FieldType.String) { Optional = true },
                new Field("email", FieldType.String) { Optional = true },
                new Field("created_at", FieldType.Int64)
            };
            var schema = new Schema(_opts.PatientsCollection, fields, "created_at");
            await _ts.CreateCollection(schema);
            _log.LogInformation("Created Typesense collection {Collection}", _opts.PatientsCollection);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense patient collection unavailable ({FailureType})", ex.GetType().Name);
        }
    }

    private async Task EnsureMedicineCollectionAsync(CancellationToken ct)
    {
        try
        {
            await _ts.RetrieveCollection(_opts.MedicinesCollection, ct);
            _log.LogInformation("Typesense collection {Collection} verified", _opts.MedicinesCollection);
        }
        catch (TypesenseApiNotFoundException)
        {
            var fields = new List<Field>
            {
                new Field("id", FieldType.String),
                new Field("tenant_id", FieldType.String) { Facet = true },
                new Field("name", FieldType.String) { Sort = true },
                new Field("generic_name", FieldType.String) { Optional = true },
                new Field("common_brands", FieldType.String) { Optional = true },
                new Field("strength", FieldType.String) { Optional = true },
                new Field("dosage_form", FieldType.String) { Optional = true },
                new Field("therapeutic_category", FieldType.String) { Optional = true },
                new Field("hsn_code", FieldType.String) { Optional = true },
                new Field("standard_pack_size", FieldType.String) { Optional = true },
                new Field("created_at", FieldType.Int64)
            };
            var schema = new Schema(_opts.MedicinesCollection, fields, "created_at");
            await _ts.CreateCollection(schema);
            _log.LogInformation("Created Typesense collection {Collection}", _opts.MedicinesCollection);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense medicine collection unavailable ({FailureType})", ex.GetType().Name);
        }
    }

    public async Task IndexAsync(Patient patient, CancellationToken ct)
    {
        RequireTenant(patient.TenantId);
        ct.ThrowIfCancellationRequested();
        var doc = ToDocument(patient);
        try
        {
            await _ts.UpsertDocument<TypesensePatientDocument>(_opts.PatientsCollection, doc);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense patient index failed for tenant {TenantId}, record {Id} ({FailureType}); nightly reindex will reconcile",
                patient.TenantId, patient.Id, ex.GetType().Name);
        }
    }

    public async Task IndexManyAsync(IEnumerable<Patient> patients, CancellationToken ct)
    {
        RequireTenant(_db.CurrentTenantId);
        ct.ThrowIfCancellationRequested();
        foreach (var batch in patients.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var patient in batch) RequireTenant(patient.TenantId);
            var response = await _ts.ImportDocuments(_opts.PatientsCollection, batch.Select(ToDocument), 1000, ImportType.Upsert);
            ct.ThrowIfCancellationRequested();
            if (response.Count != batch.Length || response.Any(r => !r.Success))
                throw new InvalidOperationException("Typesense patient import incomplete; retry reconciliation.");
        }
    }

    public async Task IndexMedicineAsync(Drug drug, CancellationToken ct)
    {
        RequireTenant(drug.TenantId);
        ct.ThrowIfCancellationRequested();
        var doc = ToMedicineDocument(drug);
        try
        {
            await _ts.UpsertDocument<TypesenseMedicineDocument>(_opts.MedicinesCollection, doc);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense medicine index failed for tenant {TenantId}, record {Id} ({FailureType}); nightly reindex will reconcile",
                drug.TenantId, drug.Id, ex.GetType().Name);
        }
    }

    public async Task IndexManyMedicinesAsync(IEnumerable<Drug> drugs, CancellationToken ct)
    {
        RequireTenant(_db.CurrentTenantId);
        ct.ThrowIfCancellationRequested();
        foreach (var batch in drugs.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var drug in batch) RequireTenant(drug.TenantId);
            var response = await _ts.ImportDocuments(_opts.MedicinesCollection, batch.Select(ToMedicineDocument), 1000, ImportType.Upsert);
            ct.ThrowIfCancellationRequested();
            if (response.Count != batch.Length || response.Any(r => !r.Success))
                throw new InvalidOperationException("Typesense medicine import incomplete; retry reconciliation.");
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenantId = _db.CurrentTenantId;
        RequireTenant(tenantId);
        ct.ThrowIfCancellationRequested();
        try
        {
            await _ts.DeleteDocuments(_opts.PatientsCollection, $"tenant_id:={tenantId} && id:={id}");
            ct.ThrowIfCancellationRequested();
        }
        catch (TypesenseApiNotFoundException)
        {
            // already gone — fine
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense patient delete failed for tenant {TenantId}, record {Id} ({FailureType})",
                tenantId, id, ex.GetType().Name);
        }
    }

    public async Task<List<PatientSearchHit>> SearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default)
    {
        RequireTenant(tenantId);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return new List<PatientSearchHit>();

        limit = Math.Clamp(limit, 1, 50);
        var trimmed = query.Trim();
        var isNumeric = trimmed.All(char.IsDigit) && trimmed.Length >= 3;
        var sp = new SearchParameters(trimmed, isNumeric ? "phone,name" : "name,phone")
        {
            Prefix = true,
            FilterBy = $"tenant_id:={tenantId}",
            PerPage = limit,
            NumberOfTypos = isNumeric ? "0" : "2"
        };

        var candidates = new List<Guid>();
        try
        {
            var result = await _ts.Search<TypesensePatientDocument>(_opts.PatientsCollection, sp, ct);
            candidates = result.Hits.Where(h => Guid.TryParse(h.Document.TenantId, out var owner) && owner == tenantId)
                .Select(h => Guid.TryParse(h.Document.Id, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).Distinct().Take(limit).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense patient search unavailable for tenant {TenantId} ({FailureType}); using database", tenantId, ex.GetType().Name);
        }
        return await FallbackSearchDatabaseAsync(trimmed, tenantId, limit, candidates, ct);
    }

    public async Task<List<PatientSearchHit>> CheckDuplicatesAsync(string name, string? phone, DateOnly? dob, Guid tenantId, CancellationToken ct = default)
    {
        RequireTenant(tenantId);
        ct.ThrowIfCancellationRequested();
        // Duplicate clearance must not depend on a derived index's freshness or fuzzy score.
        return await FallbackDuplicateCheckDatabaseAsync(name, phone, dob, tenantId, ct);
    }

    public async Task<List<MedicineSearchHit>> MedicinesSearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default)
    {
        RequireTenant(tenantId);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return new List<MedicineSearchHit>();

        limit = Math.Clamp(limit, 1, 50);
        var trimmed = query.Trim();
        var sp = new SearchParameters(trimmed, "name,generic_name,common_brands,dosage_form,strength,hsn_code")
        {
            FilterBy = $"tenant_id:={tenantId}",
            PerPage = limit,
            NumberOfTypos = "2"
        };

        var candidates = new List<Guid>();
        try
        {
            var result = await _ts.Search<TypesenseMedicineDocument>(_opts.MedicinesCollection, sp, ct);
            candidates = result.Hits.Where(h => Guid.TryParse(h.Document.TenantId, out var owner) && owner == tenantId)
                .Select(h => Guid.TryParse(h.Document.Id, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).Distinct().Take(limit).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Typesense medicine search unavailable for tenant {TenantId} ({FailureType}); using database", tenantId, ex.GetType().Name);
        }
        return await FallbackMedicinesSearchDatabaseAsync(trimmed, tenantId, limit, candidates, ct);
    }

    private async Task<List<PatientSearchHit>> FallbackSearchDatabaseAsync(string query, Guid tenantId, int limit, List<Guid> candidates, CancellationToken ct)
    {
        try
        {
            var lowerQuery = query.Trim().ToLowerInvariant();
            var matches = await _db.Patients.AsNoTracking()
                .Where(p => p.TenantId == tenantId
                         && (p.Name.ToLower().Contains(lowerQuery) || p.Phone.Contains(query) || candidates.Contains(p.Id)))
                .OrderByDescending(p => p.Name.ToLower().Contains(lowerQuery) || p.Phone.Contains(query))
                .ThenByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
                .Take(limit)
                .ToListAsync(ct);

            return matches.Select(p => new PatientSearchHit(
                p.Id,
                p.Name,
                p.Phone,
                p.Dob?.ToString("yyyy-MM-dd"),
                p.Gender.ToString(),
                p.Address,
                100)).ToList();
        }
        catch (Exception dbEx) when (!ct.IsCancellationRequested)
        {
            throw new SearchUnavailableException(dbEx);
        }
    }

    private async Task<List<PatientSearchHit>> FallbackDuplicateCheckDatabaseAsync(string name, string? phone, DateOnly? dob, Guid tenantId, CancellationToken ct)
    {
        try
        {
            var query = _db.Patients.AsNoTracking().Where(p => p.TenantId == tenantId);

            if (!string.IsNullOrWhiteSpace(phone))
            {
                var phoneMatches = await query
                    .Where(p => p.Phone == phone.Trim())
                    .OrderBy(p => p.Id)
                    .Take(5)
                    .ToListAsync(ct);

                if (phoneMatches.Count > 0)
                {
                    return phoneMatches.Select(p => new PatientSearchHit(
                        p.Id,
                        p.Name,
                        p.Phone,
                        p.Dob?.ToString("yyyy-MM-dd"),
                        p.Gender.ToString(),
                        p.Address,
                        150)).ToList();
                }
            }

            if (string.IsNullOrWhiteSpace(name)) return [];
            var lowerName = name.Trim().ToLowerInvariant();
            var nameDobMatches = await query
                .Where(p => p.Name.ToLower().Contains(lowerName) && (dob == null || p.Dob == dob))
                .OrderBy(p => p.Id)
                .Take(5)
                .ToListAsync(ct);

            return nameDobMatches.Select(p => new PatientSearchHit(
                p.Id,
                p.Name,
                p.Phone,
                p.Dob?.ToString("yyyy-MM-dd"),
                p.Gender.ToString(),
                p.Address,
                110)).ToList();
        }
        catch (Exception dbEx) when (!ct.IsCancellationRequested)
        {
            throw new SearchUnavailableException(dbEx);
        }
    }

    private async Task<List<MedicineSearchHit>> FallbackMedicinesSearchDatabaseAsync(string query, Guid tenantId, int limit, List<Guid> candidates, CancellationToken ct)
    {
        try
        {
            var cleanQuery = query.Trim().ToLowerInvariant();
            var matches = await _db.Drugs.AsNoTracking()
                .Where(m => m.TenantId == tenantId
                         && (m.Name.ToLower().Contains(cleanQuery)
                             || m.GenericName.ToLower().Contains(cleanQuery)
                             || (m.CommonBrands != null && m.CommonBrands.ToLower().Contains(cleanQuery))
                             || (m.DosageForm != null && m.DosageForm.ToLower().Contains(cleanQuery))
                             || (m.Strength != null && m.Strength.ToLower().Contains(cleanQuery))
                             || m.HsnCode.Contains(cleanQuery) || candidates.Contains(m.Id)))
                .OrderByDescending(m => m.Name.ToLower().Contains(cleanQuery) || m.GenericName.ToLower().Contains(cleanQuery))
                .ThenBy(m => m.Name).ThenBy(m => m.Id)
                .Take(limit)
                .ToListAsync(ct);

            return matches.Select(m => new MedicineSearchHit(
                m.Id,
                m.Name,
                m.GenericName,
                m.CommonBrands,
                m.Strength,
                m.DosageForm,
                100)).ToList();
        }
        catch (Exception dbEx) when (!ct.IsCancellationRequested)
        {
            throw new SearchUnavailableException(dbEx);
        }
    }

    private void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId != _db.CurrentTenantId)
            throw new UnauthorizedAccessException("A matching selected tenant is required for search and indexing.");
    }

    private static TypesensePatientDocument ToDocument(Patient p) => new()
    {
        Id = p.Id.ToString(),
        TenantId = p.TenantId.ToString(),
        Name = p.Name,
        Phone = p.Phone,
        Dob = p.Dob?.ToString("yyyy-MM-dd"),
        Gender = p.Gender.ToString(),
        Address = null,
        Email = null,
        CreatedAt = p.CreatedAt.ToUnixTimeSeconds()
    };

    private static TypesenseMedicineDocument ToMedicineDocument(Drug d) => new()
    {
        Id = d.Id.ToString(),
        TenantId = d.TenantId.ToString(),
        Name = d.Name,
        GenericName = d.GenericName,
        CommonBrands = d.CommonBrands,
        Strength = d.Strength,
        DosageForm = d.DosageForm,
        TherapeuticCategory = d.TherapeuticCategory,
        HsnCode = d.HsnCode,
        StandardPackSize = d.StandardPackSize,
        CreatedAt = d.CreatedAt.ToUnixTimeSeconds()
    };
}

public sealed class SearchUnavailableException(Exception innerException)
    : Exception("Database search is temporarily unavailable. Retry the search before proceeding.", innerException);
