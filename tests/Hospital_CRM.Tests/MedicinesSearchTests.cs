using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hospital_CRM.Tests;

public class MedicinesSearchTests
{
    [Fact]
    public async Task Search_Returns_EmptyArray_WhenQueryBlank()
    {
        using var f = new Fixture();
        Assert.Empty(Json(Assert.IsType<OkObjectResult>(await f.Controller.Search(" ")).Value).EnumerateArray());
        Assert.Equal(0, f.Search.Calls);
        Assert.Empty(f.Db.MedicineSearchMissLogs);
    }

    [Fact]
    public async Task Search_CallsService_AndMapsFieldsCorrectly()
    {
        using var f = new Fixture();
        var drug = new Drug { Id = Guid.NewGuid(), TenantId = f.Tenant, Name = "Paracetamol 650mg",
            GenericName = "Paracetamol / Acetaminophen", CommonBrands = "Dolo 650, Calpol", Strength = "650mg",
            DosageForm = "Tablet", GstRate = 0, ScheduleClass = ScheduleClass.General };
        f.Db.Drugs.Add(drug);
        await f.Db.SaveChangesAsync();
        f.Search.Hits = [new(drug.Id, "stale name", "stale generic", "stale brands", "stale strength", "stale form", 100)];

        var item = Assert.Single(Json(Assert.IsType<OkObjectResult>(await f.Controller.Search(" dolo ", 500)).Value).EnumerateArray());
        Assert.Equal(drug.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(drug.Name, item.GetProperty("name").GetString());
        Assert.Equal(drug.GenericName, item.GetProperty("genericName").GetString());
        Assert.Equal(drug.CommonBrands, item.GetProperty("commonBrands").GetString());
        Assert.Equal(drug.Strength, item.GetProperty("strength").GetString());
        Assert.Equal(drug.DosageForm, item.GetProperty("dosageForm").GetString());
        Assert.Equal(0, item.GetProperty("gstRate").GetDecimal());
        Assert.Equal(("dolo", f.Tenant, 50), f.Search.LastQuery);
    }

    [Theory]
    [InlineData("missing-tenant")]
    [InlineData("empty-tenant")]
    [InlineData("mismatched-tenant")]
    [InlineData("missing-clinic")]
    [InlineData("empty-clinic")]
    [InlineData("foreign-clinic")]
    [InlineData("missing-membership")]
    [InlineData("suspended")]
    [InlineData("invited")]
    [InlineData("other-user")]
    [InlineData("other-membership-clinic")]
    [InlineData("other-membership-tenant")]
    [InlineData("missing-user")]
    [InlineData("empty-user")]
    public async Task AllEndpoints_RejectInvalidSelectedContextBeforeSearchOrWrite(string mode)
    {
        using var f = new Fixture();
        if (mode == "missing-membership") f.Db.TenantMemberships.Remove(f.Membership);
        if (mode is "suspended" or "invited") f.Membership.Status = mode;
        if (mode == "other-user") f.Membership.UserId = Guid.NewGuid();
        if (mode == "other-membership-clinic") f.Membership.ClinicId = Guid.NewGuid();
        if (mode == "other-membership-tenant") f.Membership.TenantId = Guid.NewGuid();
        await f.Db.SaveChangesAsync();
        f.SetClaims(mode == "missing-tenant" ? null : mode == "empty-tenant" ? Guid.Empty : mode == "mismatched-tenant" ? Guid.NewGuid() : f.Tenant,
            mode == "missing-clinic" ? null : mode == "empty-clinic" ? Guid.Empty : mode == "foreign-clinic" ? Guid.NewGuid() : f.Clinic,
            mode == "missing-user" ? null : mode == "empty-user" ? Guid.Empty : f.User);
        foreach (var action in new Func<Task<IActionResult>>[] {
            () => f.Controller.Search(""), () => f.Controller.Search("synthetic"),
            () => f.Controller.CurateMedicineFromSource(Request(), default), () => f.Controller.GetSearchMissLogs() })
            Assert.Equal(mode is "missing-user" or "empty-user" ? 401 : 403, Assert.IsAssignableFrom<ObjectResult>(await action()).StatusCode);
        Assert.Equal(0, f.Search.Calls);
        Assert.Equal(0, f.Cdci.Calls);
        Assert.Empty(f.Db.Drugs);
        Assert.Empty(f.Db.MedicineSearchMissLogs);
    }

    [Fact]
    public async Task Search_DropsForeignAndDeletedHits_AndKeepsCdciUnverified()
    {
        using var f = new Fixture();
        var foreign = new Drug { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Foreign secret" };
        using (var seed = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance))
        { seed.Drugs.Add(foreign); await seed.SaveChangesAsync(); }
        f.Search.Hits = [new(foreign.Id, foreign.Name, "foreign", null, null, null, 100), new(Guid.NewGuid(), "deleted", "deleted", null, null, null, 100)];
        f.Cdci.Results = [new("synthetic-code", "Open source drug", "Synthetic salt", "Tablet", "1mg", null)];
        var item = Assert.Single(Json(Assert.IsType<OkObjectResult>(await f.Controller.Search("synthetic")).Value).EnumerateArray());
        Assert.Equal("Open source drug", item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("isUnverified").GetBoolean());
        Assert.Equal("CDCI (Open Source)", item.GetProperty("dataSource").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_DatabaseFailure_ReturnsSanitized503WithoutCdciOrMiss(bool wrapped)
    {
        using var f = new Fixture();
        var failure = new SyntheticDatabaseException();
        f.Search.Failure = wrapped ? new SearchUnavailableException(failure) : failure;
        var response = Assert.IsType<ObjectResult>(await f.Controller.Search("secret-query"));
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("search_unavailable", Json(response.Value).GetProperty("error").GetString());
        Assert.DoesNotContain("secret", Json(response.Value).ToString() + string.Join(" ", f.Log.Messages));
        Assert.Equal(0, f.Cdci.Calls);
        Assert.Empty(f.Db.MedicineSearchMissLogs);
    }

    [Fact]
    public async Task Search_CancellationPropagatesBeforeMutation()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Controller.Search("synthetic", ct: cts.Token));
        Assert.Empty(f.Db.MedicineSearchMissLogs);
    }

    [Fact]
    public async Task MissLogFailure_IsSanitizedAndDoesNotFailSearch()
    {
        using var f = new Fixture();
        f.Db.SavingChanges += (_, _) => throw new DbUpdateException("secret-database-payload");
        Assert.IsType<OkObjectResult>(await f.Controller.Search("secret-query"));
        Assert.Single(f.Log.Messages);
        Assert.DoesNotContain("secret", string.Join(" ", f.Log.Messages));
    }

    [Fact]
    public async Task MissLogs_ArePersistedAndReadOnlyWithinSelectedTenant()
    {
        using var f = new Fixture();
        using (var seed = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance))
        { seed.MedicineSearchMissLogs.Add(new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Query = "foreign", SearchedAt = DateTimeOffset.UtcNow }); await seed.SaveChangesAsync(); }
        Assert.IsType<OkObjectResult>(await f.Controller.Search("synthetic-miss"));
        var item = Assert.Single(Json(Assert.IsType<OkObjectResult>(await f.Controller.GetSearchMissLogs()).Value).EnumerateArray());
        Assert.Equal("synthetic-miss", item.GetProperty("query").GetString());
        Assert.Equal(f.User, item.GetProperty("searchedBy").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Curation_IndexesPersistedDrug_PreservesZeroGstAndSuccessOnFailure(bool failIndex)
    {
        using var f = new Fixture();
        f.Search.IndexFailure = failIndex ? new HttpRequestException("secret-provider-payload") : null;
        var result = Assert.IsType<ObjectResult>(await f.Controller.CurateMedicineFromSource(Request(), default));
        Assert.Equal(201, result.StatusCode);
        var id = Json(result.Value).GetProperty("id").GetGuid();
        Assert.Equal(id, Assert.Single(f.Search.Indexed));
        using var reader = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        var drug = await reader.Drugs.SingleAsync();
        Assert.Equal(id, drug.Id);
        Assert.Equal(0m, drug.GstRate);
        Assert.False(drug.IsUnverified);
        Assert.Equal("HospitalMaster", drug.DataSource);
        Assert.DoesNotContain("secret", string.Join(" ", f.Log.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Curation_PostSaveCancellationPreservesCreatedResponse(bool duringIndex)
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        if (duringIndex) f.Search.BeforeIndex = cts.Cancel;
        else f.Db.SavedChanges += (_, _) => cts.Cancel();
        f.Search.IndexFailure = new OperationCanceledException("secret-cancellation", cts.Token);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await f.Controller.CurateMedicineFromSource(Request(), cts.Token)).StatusCode);
        using var reader = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        Assert.Single(reader.Drugs);
        Assert.Equal(duringIndex ? 1 : 0, f.Search.Indexed.Count);
        Assert.Contains(f.Log.Messages, m => m.Contains("reconciliation"));
        Assert.DoesNotContain("secret", string.Join(" ", f.Log.Messages));
    }

    [Fact]
    public async Task Curation_FailedSaveDoesNotIndex()
    {
        using var f = new Fixture();
        f.Db.SavingChanges += (_, _) => throw new DbUpdateException("synthetic-failed-save");
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Controller.CurateMedicineFromSource(Request(), default));
        Assert.Empty(f.Search.Indexed);
        using var reader = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        Assert.Empty(reader.Drugs);
    }

    [Fact]
    public async Task Curation_OuterTransactionDefersIndex()
    {
        using var f = new Fixture();
        using var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await f.Controller.CurateMedicineFromSource(Request(), default)).StatusCode);
        Assert.Empty(f.Search.Indexed);
        Assert.Contains(f.Log.Messages, m => m.Contains("reconciliation"));
        // InMemory verifies deferral only, not relational enlistment or commit durability.
    }

    [Theory]
    [InlineData("nonsense", 0, 10)]
    [InlineData("999", 0, 10)]
    [InlineData("General", -1, 10)]
    [InlineData("General", 101, 10)]
    [InlineData("General", 0, -1)]
    public async Task Curation_RejectsInvalidScheduleOrMoney(string schedule, decimal gst, decimal mrp)
    {
        using var f = new Fixture();
        Assert.IsType<BadRequestObjectResult>(await f.Controller.CurateMedicineFromSource(Request() with { ScheduleClass = schedule, GstRate = gst, IndicativeMrp = mrp }, default));
        Assert.Empty(f.Db.Drugs);
        Assert.Empty(f.Search.Indexed);
    }

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    private static CurateMedicineRequest Request() => new("Synthetic drug", "Synthetic salt", null, null, null, "General", null, 0, 10, null, null, null);

    private sealed class Fixture : IDisposable
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Clinic { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public DbContextOptions<HospitalCrmDbContext> Options { get; } = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public HospitalCrmDbContext Db { get; }
        public TenantMembership Membership { get; }
        public RecordingSearch Search { get; }
        public RecordingCdci Cdci { get; } = new();
        public RecordingLog Log { get; } = new();
        public MedicinesController Controller { get; }
        private readonly IDisposable _scope;
        public Fixture()
        {
            var context = new TenantContext();
            _scope = context.PushTenant(Tenant);
            Db = new(Options, context);
            Db.Clinics.Add(new() { Id = Clinic, TenantId = Tenant });
            Membership = new() { UserId = User, TenantId = Tenant, ClinicId = Clinic, Role = UserRole.Pharmacist };
            Db.TenantMemberships.Add(Membership);
            Db.SaveChanges();
            Search = new(Options);
            Controller = new(Db, Search, Cdci, Log);
            SetClaims(Tenant, Clinic, User);
        }
        public void SetClaims(Guid? tenant, Guid? clinic, Guid? user)
        {
            var claims = new List<Claim> { new("role", "Pharmacist") };
            if (tenant.HasValue) claims.Add(new("tenant_id", tenant.ToString()!));
            if (clinic.HasValue) claims.Add(new("clinicId", clinic.ToString()!));
            if (user.HasValue) claims.Add(new("sub", user.ToString()!));
            Controller.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "Synthetic")) } };
        }
        public void Dispose() { Db.Dispose(); _scope.Dispose(); }
    }
    private sealed class RecordingSearch(DbContextOptions<HospitalCrmDbContext> options) : FakePatientSearchService
    {
        public int Calls { get; private set; }
        public (string, Guid, int) LastQuery { get; private set; }
        public List<MedicineSearchHit> Hits { get; set; } = [];
        public List<Guid> Indexed { get; } = [];
        public Exception? Failure { get; set; }
        public Exception? IndexFailure { get; set; }
        public Action? BeforeIndex { get; set; }
        public override Task<List<MedicineSearchHit>> MedicinesSearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = (query, tenantId, limit);
            if (Failure != null) throw Failure;
            return Task.FromResult(Hits);
        }
        public override async Task IndexMedicineAsync(Drug drug, CancellationToken ct)
        {
            using var reader = new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
            var saved = await reader.Drugs.SingleAsync(d => d.Id == drug.Id);
            Assert.Equal(drug.TenantId, saved.TenantId);
            Assert.Equal(drug.GstRate, saved.GstRate);
            Indexed.Add(saved.Id);
            BeforeIndex?.Invoke();
            if (IndexFailure != null) throw IndexFailure;
        }
    }
    private sealed class RecordingCdci : ICdciMedicineCatalogService
    {
        public int Calls { get; private set; }
        public List<CdciMedicineSearchResult> Results { get; set; } = [];
        public Task<List<CdciMedicineSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Results); }
    }
    private sealed class RecordingLog : ILogger<MedicinesController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }
    private sealed class SyntheticDatabaseException() : DbException("secret-database-payload");
}

public class MockMedicineSearchService : FakePatientSearchService
{
    private readonly List<MedicineSearchHit> _hits;

    public MockMedicineSearchService(List<MedicineSearchHit> hits)
    {
        _hits = hits;
    }

    public override Task<List<MedicineSearchHit>> MedicinesSearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default)
    {
        return Task.FromResult(_hits);
    }
}

public class MockCdciMedicineCatalogService : Hospital_CRM.Api.Services.ICdciMedicineCatalogService
{
    private readonly List<Hospital_CRM.Api.Services.CdciMedicineSearchResult> _results;

    public MockCdciMedicineCatalogService(List<Hospital_CRM.Api.Services.CdciMedicineSearchResult>? results = null)
    {
        _results = results ?? new List<Hospital_CRM.Api.Services.CdciMedicineSearchResult>();
    }

    public Task<List<Hospital_CRM.Api.Services.CdciMedicineSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        return Task.FromResult(_results);
    }
}
