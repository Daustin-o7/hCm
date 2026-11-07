using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2SearchReportCatalogTests
{
    [Theory]
    [InlineData("create", false)]
    [InlineData("import", false)]
    [InlineData("create", true)]
    [InlineData("import", true)]
    public async Task CatalogueWrite_IndexesPersistedDrugAndPreservesSuccessOnIndexFailure(string action, bool failIndex)
    {
        using var f = new Fixture();
        f.Search.Failure = failIndex ? new HttpRequestException("secret-provider-payload") : null;
        var response = Assert.IsType<ObjectResult>(await f.Write(action));
        Assert.Equal(201, response.StatusCode);
        var id = JsonSerializer.SerializeToElement(response.Value).GetProperty("id").GetGuid();
        Assert.Equal(id, Assert.Single(f.Search.Indexed));
        using var reader = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        Assert.Equal(id, (await reader.Drugs.SingleAsync()).Id);
        Assert.DoesNotContain("secret-provider-payload", string.Join(" ", f.Log.Messages));
        if (failIndex) Assert.Contains(f.Log.Messages, m => m.Contains("reconcil", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("import")]
    public async Task FailedSave_DoesNotIndex(string action)
    {
        using var f = new Fixture();
        f.Db.SavingChanges += (_, _) => throw new DbUpdateException("synthetic-save-failure");
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Write(action));
        Assert.Empty(f.Search.Indexed);
        using var reader = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        Assert.Empty(reader.Drugs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterCommit_DoesNotTurnSavedDrugIntoFailedMutation(bool duringIndex)
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        if (duringIndex) f.Search.BeforeIndex = cts.Cancel;
        else f.Db.SavedChanges += (_, _) => cts.Cancel();
        f.Search.Failure = new OperationCanceledException(cts.Token);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await f.Write("create", cts.Token)).StatusCode);
        Assert.Single(f.Db.Drugs);
        Assert.Equal(duringIndex ? 1 : 0, f.Search.Indexed.Count);
        Assert.Contains(f.Log.Messages, m => m.Contains("reconcil", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("import")]
    public async Task OuterTransaction_DefersIndexUntilReconciliation(string action)
    {
        using var f = new Fixture();
        using var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        Assert.Equal(201, Assert.IsType<ObjectResult>(await f.Write(action)).StatusCode);
        Assert.Empty(f.Search.Indexed);
        Assert.Contains(f.Log.Messages, m => m.Contains("reconcil", StringComparison.OrdinalIgnoreCase));
        // InMemory checks the deferral contract only; it does not enlist or prove rollback/durability.
    }

    [Fact]
    public async Task FailedFormularySave_DoesNotIndexOrExposeDatabaseFailure()
    {
        using var f = new Fixture();
        f.Db.SavingChanges += (_, _) =>
        {
            if (f.Db.ChangeTracker.Entries<Drug>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("secret-database-payload");
        };
        var response = Assert.IsType<ObjectResult>(await f.Controller.SyncOfficialHospitalFormulary(new(false), default));
        Assert.Equal(500, response.StatusCode);
        Assert.Empty(f.Search.Indexed);
        using var reader = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        Assert.Empty(reader.Drugs);
        Assert.DoesNotContain("secret-database-payload", JsonSerializer.Serialize(response.Value) + string.Join(" ", f.Log.Messages));
    }

    [Fact]
    public async Task FormularySync_IndexesEveryCommittedChangeButNotUnchangedCustomDrugs()
    {
        using var f = new Fixture();
        var custom = new Drug { Id = Guid.NewGuid(), TenantId = f.Tenant, Name = "Synthetic custom drug", GenericName = "Synthetic", TherapeuticCategory = "General", DosageForm = "Tablet", Strength = "1mg", HsnCode = "3004", StandardPackSize = "1" };
        f.Db.Drugs.Add(custom);
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await f.Controller.SyncOfficialHospitalFormulary(new(false), default));
        var synced = await f.Db.Drugs.Where(d => d.Id != custom.Id).ToListAsync();
        Assert.NotEmpty(synced);
        Assert.Equal(synced.Select(d => d.Id).Order(), f.Search.Indexed.Order());

        f.Search.Indexed.Clear();
        f.Search.Failure = new HttpRequestException("secret-provider-payload");
        Assert.IsType<OkObjectResult>(await f.Controller.SyncOfficialHospitalFormulary(new(false), default));
        Assert.Equal(synced.Select(d => d.Id).Order(), f.Search.Indexed.Order());
        Assert.DoesNotContain(custom.Id, f.Search.Indexed);
        Assert.DoesNotContain("secret-provider-payload", string.Join(" ", f.Log.Messages));
    }

    private sealed class Fixture : IDisposable
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public DbContextOptions<HospitalCrmDbContext> Options { get; } = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public HospitalCrmDbContext Db { get; }
        public RecordingSearch Search { get; }
        public RecordingLogger Log { get; } = new();
        public PharmacyCatalogController Controller { get; }
        private readonly ServiceProvider _services;
        private readonly IDisposable _scope;
        public Fixture()
        {
            var context = new TenantContext();
            _scope = context.PushTenant(Tenant);
            Db = new HospitalCrmDbContext(Options, context);
            Search = new RecordingSearch(Options);
            _services = new ServiceCollection().AddSingleton<IPatientSearchService>(Search)
                .AddSingleton<ICdciMedicineCatalogService>(new UnusedCdci()).BuildServiceProvider();
            Controller = ActivatorUtilities.CreateInstance<PharmacyCatalogController>(_services, Db, Log);
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", Tenant.ToString()), new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", "Pharmacist")], "Synthetic")) } };
        }
        public Task<IActionResult> Write(string action, CancellationToken ct = default) => action == "create"
            ? Controller.CreateDrug(new("Synthetic drug", "Synthetic generic", "General", "Tablet", "1mg", "General", "3004", 12, false, null, "1", 10, null), ct)
            : Controller.ImportCdciDrugToCatalog(new("Synthetic drug", "Synthetic generic", "General", "Tablet", "1mg", "General", "3004", 12, 10, "1", null, "synthetic"), ct);
        public void Dispose() { Db.Dispose(); _services.Dispose(); _scope.Dispose(); }
    }

    private sealed class RecordingSearch(DbContextOptions<HospitalCrmDbContext> options) : FakePatientSearchService
    {
        public List<Guid> Indexed { get; } = [];
        public Exception? Failure { get; set; }
        public Action? BeforeIndex { get; set; }
        public override async Task IndexMedicineAsync(Drug drug, CancellationToken ct)
        {
            BeforeIndex?.Invoke();
            using var reader = new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
            var saved = await reader.Drugs.SingleAsync(d => d.Id == drug.Id);
            Assert.Equal(drug.TenantId, saved.TenantId);
            Assert.Equal(drug.GenericName, saved.GenericName);
            Indexed.Add(drug.Id);
            if (Failure != null) throw Failure;
        }
    }
    private sealed class RecordingLogger : ILogger<PharmacyCatalogController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }
    private sealed class UnusedCdci : ICdciMedicineCatalogService
    {
        public Task<List<CdciMedicineSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default) => throw new InvalidOperationException("Not used by catalogue mutations.");
    }
}
