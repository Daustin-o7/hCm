using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class MedicinesController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IPatientSearchService _search;
    private readonly ICdciMedicineCatalogService _cdci;
    private readonly ILogger<MedicinesController> _logger;

    public MedicinesController(
        HospitalCrmDbContext db,
        IPatientSearchService search,
        ICdciMedicineCatalogService cdci,
        ILogger<MedicinesController> logger)
    {
        _db = db;
        _search = search;
        _cdci = cdci;
        _logger = logger;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        try
        {
            return await SearchAsync(q, limit, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is SearchUnavailableException or DbException)
        {
            _logger.LogWarning("Medicine search unavailable ({FailureType})", ex.GetType().Name);
            return StatusCode(503, new { error = "search_unavailable", message = "Database search is temporarily unavailable. Retry the search before proceeding." });
        }
    }

    private async Task<IActionResult> SearchAsync(string? q, int limit, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue || userId == Guid.Empty) return Unauthorized(new { error = "invalid_token" });
        if (!await HasSelectedContextAsync(ct)) return StatusCode(403, new { error = "invalid_selected_clinic" });

        var tenantId = User.GetTenantId()!.Value;

        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<object>());

        var cleanQuery = q.Trim();
        var maxLimit = Math.Clamp(limit, 1, 50);

        // 1. Primary search: Local clinic inventory / Hospital Master
        var hits = await _search.MedicinesSearchAsync(cleanQuery, tenantId, maxLimit, ct);

        // Fetch corresponding DB records to preserve verified schedule, datasource, etc.
        var hitIds = hits.Select(h => h.Id).ToList();
        var dbDrugs = await _db.Drugs.AsNoTracking()
            .Where(d => d.TenantId == tenantId && hitIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        var results = new List<object>();

        foreach (var h in hits)
        {
            if (!dbDrugs.TryGetValue(h.Id, out var drug)) continue;
            results.Add(new
            {
                id = drug.Id,
                name = drug.Name,
                genericName = drug.GenericName,
                commonBrands = drug.CommonBrands,
                strength = drug.Strength,
                dosageForm = drug.DosageForm,
                scheduleClass = drug.ScheduleClass.ToString(),
                dataSource = drug.DataSource,
                isUnverified = drug.IsUnverified,
                checkpointMessage = drug.IsUnverified ? "Open Source - Needs Pharmacist Review" : null,
                indicativeMrp = drug.IndicativeMrp,
                gstRate = drug.GstRate,
                hsnCode = drug.HsnCode,
                score = h.Score
            });
        }

        // 2. Secondary fallback / enrichment: CDCI Open-Source Database
        if (results.Count < maxLimit)
        {
            var needed = maxLimit - results.Count;
            var cdciHits = await _cdci.SearchAsync(cleanQuery, needed, ct);

            foreach (var cdci in cdciHits)
            {
                // Avoid duplicates if already present in results by name
                var alreadyPresent = results.Any(r =>
                {
                    var prop = r.GetType().GetProperty("name")?.GetValue(r) as string;
                    return string.Equals(prop, cdci.Name, StringComparison.OrdinalIgnoreCase);
                });

                if (alreadyPresent) continue;

                results.Add(new
                {
                    id = CreateDeterministicGuid("cdci:" + cdci.ExternalCode),
                    name = cdci.Name,
                    genericName = cdci.GenericName,
                    commonBrands = cdci.CommonBrands,
                    strength = cdci.Strength,
                    dosageForm = cdci.DosageForm,
                    scheduleClass = (string?)null, // CDCI release notes do not supply schedule class
                    dataSource = "CDCI (Open Source)",
                    isUnverified = true, // Checkpoint: explicitly unverified open-source entry
                    checkpointMessage = "Open Source - Needs Pharmacist Review",
                    indicativeMrp = 0m, // Must be configured by pharmacist
                    gstRate = 12m,
                    hsnCode = (string?)null,
                    score = 80
                });
            }
        }

        // 3. Log autocomplete miss if 0 hits across all sources
        if (results.Count == 0)
        {
            try
            {
                _db.MedicineSearchMissLogs.Add(new MedicineSearchMissLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Query = cleanQuery,
                    SearchedByUserId = userId.Value,
                    SearchedAt = DateTimeOffset.UtcNow
                });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Failed to record medicine search miss ({FailureType})", ex.GetType().Name);
            }
        }

        return Ok(results);
    }

    [HttpPost("curate-from-source")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> CurateMedicineFromSource([FromBody] CurateMedicineRequest req, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId || userId == Guid.Empty) return Unauthorized(new { error = "invalid_token" });
        if (!await HasSelectedContextAsync(ct)) return StatusCode(403, new { error = "invalid_selected_clinic" });
        var tenantId = User.GetTenantId()!.Value;

        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.GenericName))
            return BadRequest(new { error = "name_and_generic_required" });

        var schedule = ScheduleClass.General;
        if (!string.IsNullOrWhiteSpace(req.ScheduleClass)
            && (!Enum.TryParse(req.ScheduleClass, true, out schedule) || !Enum.IsDefined(schedule)))
            return BadRequest(new { error = "invalid_schedule_class" });
        if (req.GstRate < 0 || req.GstRate > 100 || req.IndicativeMrp < 0)
            return BadRequest(new { error = "invalid_price_or_gst" });

        var drug = new Drug
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = req.Name.Trim(),
            GenericName = req.GenericName.Trim(),
            TherapeuticCategory = !string.IsNullOrWhiteSpace(req.TherapeuticCategory) ? req.TherapeuticCategory.Trim() : "General",
            DosageForm = !string.IsNullOrWhiteSpace(req.DosageForm) ? req.DosageForm.Trim() : "Tablet",
            Strength = req.Strength?.Trim() ?? "",
            ScheduleClass = schedule,
            HsnCode = !string.IsNullOrWhiteSpace(req.HsnCode) ? req.HsnCode.Trim() : "30049099",
            GstRate = req.GstRate,
            IndicativeMrp = req.IndicativeMrp,
            StandardPackSize = req.StandardPackSize ?? "10 Tablets",
            CommonBrands = req.CommonBrands ?? req.Name.Trim(),
            ExternalCode = req.ExternalCode,
            DataSource = "HospitalMaster", // Promoted and verified by clinic pharmacist
            IsUnverified = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Drugs.Add(drug);
        await _db.SaveChangesAsync(ct);

        if (ct.IsCancellationRequested || _db.Database.CurrentTransaction != null || System.Transactions.Transaction.Current != null)
            _logger.LogWarning("Medicine index deferred for tenant {TenantId}; reconciliation required", tenantId);
        else
        {
            try
            {
                await _search.IndexMedicineAsync(drug, ct);
            }
            catch (Exception ex)
            {
                // Derived indexing must not turn a saved mutation into a retry, including cancellation.
                _logger.LogWarning("Medicine index failed for tenant {TenantId}, record {DrugId} ({FailureType}); reconciliation required",
                    tenantId, drug.Id, ex.GetType().Name);
            }
        }

        return StatusCode(201, new
        {
            id = drug.Id,
            name = drug.Name,
            genericName = drug.GenericName,
            scheduleClass = drug.ScheduleClass.ToString(),
            dataSource = drug.DataSource,
            isUnverified = drug.IsUnverified,
            message = "medicine_curated_and_verified"
        });
    }

    [HttpGet("miss-logs")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetSearchMissLogs([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (User.GetUserId() is not { } userId || userId == Guid.Empty) return Unauthorized(new { error = "invalid_token" });
        if (!await HasSelectedContextAsync(ct)) return StatusCode(403, new { error = "invalid_selected_clinic" });
        var tenantId = User.GetTenantId()!.Value;

        var misses = await _db.MedicineSearchMissLogs.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.SearchedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(m => new
            {
                id = m.Id,
                query = m.Query,
                timestamp = m.SearchedAt,
                searchedBy = m.SearchedByUserId
            })
            .ToListAsync(ct);

        return Ok(misses);
    }

    private async Task<bool> HasSelectedContextAsync(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        var userId = User.GetUserId();
        if (tenantId is null || tenantId == Guid.Empty || tenantId != _db.CurrentTenantId
            || clinicId is null || clinicId == Guid.Empty)
            return false;
        return await _db.Clinics.AnyAsync(c => c.Id == clinicId && c.TenantId == tenantId, ct)
            && await _db.TenantMemberships.AnyAsync(m => m.UserId == userId && m.TenantId == tenantId
                && m.ClinicId == clinicId && m.Status == "active", ct);
    }

    private static Guid CreateDeterministicGuid(string input)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}

public record CurateMedicineRequest(
    string Name,
    string GenericName,
    string? TherapeuticCategory,
    string? DosageForm,
    string? Strength,
    string? ScheduleClass,
    string? HsnCode,
    decimal GstRate,
    decimal IndicativeMrp,
    string? StandardPackSize,
    string? CommonBrands,
    string? ExternalCode
);
