using System.Text.Json;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2SearchReportJobTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reindex_UsesFreshTenantContextsAndBoundedCompleteImports(bool medicines)
    {
        using var fixture = new JobFixture();
        await fixture.SeedAsync(1003);
        await fixture.RunAsync(medicines);
        var batches = fixture.Transport.Requests.Where(r => r.Uri.Contains("/import"))
            .Select(r => r.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray()).ToArray();
        Assert.Equal(3, batches.Length);
        Assert.All(batches, batch =>
        {
            Assert.InRange(batch.Length, 1, 1000);
            Assert.Single(batch.Select(d => d.GetProperty("tenant_id").GetGuid()).Distinct());
        });
        Assert.Equal(1006, batches.Sum(b => b.Length));
        Assert.Equal(1006, batches.SelectMany(b => b).Select(d => d.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal(new[] { Guid.Empty, fixture.TenantA, fixture.TenantB }.Order(), fixture.ResolvedTenants.Order());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImportFailureOrMissingOutcome_FailsThenIdempotentRetryRecovers(bool medicines, bool missing)
    {
        using var fixture = new JobFixture();
        await fixture.SeedAsync(3);
        fixture.Transport.ImportFails = !missing;
        fixture.Transport.MissingImportResult = missing;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync(medicines));
        fixture.Transport.ImportFails = false;
        fixture.Transport.MissingImportResult = false;
        fixture.Transport.Requests.Clear();
        await fixture.RunAsync(medicines);
        var requests = fixture.Transport.Requests.Where(r => r.Uri.Contains("/import")).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, r => Assert.Contains("action=upsert", r.Uri));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reindex_RemovesMissingRowsAcrossPages_WithTenantBoundDeletes(bool medicines)
    {
        using var fixture = new JobFixture();
        await fixture.SeedAsync(1);
        var unrelatedTenant = Guid.NewGuid();
        foreach (var tenant in new[] { fixture.TenantA, fixture.TenantB, unrelatedTenant })
            for (var i = 0; i < 251; i++)
                fixture.Transport.StoredDocuments!.Add(JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), tenant_id = tenant }));
        await fixture.RunAsync(medicines);
        Assert.Equal(255, fixture.Transport.StoredDocuments!.Count);
        Assert.Equal(251, fixture.Transport.StoredDocuments.Count(d => d.GetProperty("tenant_id").GetGuid() == unrelatedTenant));
        Assert.All(fixture.Transport.Requests.Where(r => r.Method == HttpMethod.Delete), r =>
        {
            var uri = Uri.UnescapeDataString(r.Uri);
            Assert.Contains("tenant_id:=", uri);
            Assert.Contains("id:=", uri);
            Assert.DoesNotContain(unrelatedTenant.ToString(), uri);
        });
    }

    [Fact]
    public void KeysetBatching_TranslatesWithPostgreSqlProviderWithoutDatabaseAccess()
    {
        var context = new TenantContext();
        var tenant = Guid.NewGuid();
        using var scope = context.PushTenant(tenant);
        using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseNpgsql("Host=synthetic.invalid;Database=synthetic;Username=synthetic").Options, context);
        var after = Guid.NewGuid();
        var sql = db.Patients.Where(p => p.TenantId == tenant && p.Id.CompareTo(after) > 0).OrderBy(p => p.Id).Take(1000).ToQueryString();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains(">", sql);
    }

    [Fact]
    public async Task CancelledReindex_PropagatesCancellationWithoutIndexAccess()
    {
        using var fixture = new JobFixture();
        await fixture.SeedAsync(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(false, cancellation.Token));
        Assert.Empty(fixture.Transport.Requests);
    }

    private sealed class JobFixture : IDisposable
    {
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public List<Guid> ResolvedTenants { get; } = [];
        public Part2SearchReportSearchTests.SearchTransport Transport { get; } = new() { StoredDocuments = [] };
        private readonly ServiceProvider _provider;
        private readonly HttpClient _http;
        private readonly DbContextOptions<HospitalCrmDbContext> _dbOptions = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public JobFixture()
        {
            _http = new HttpClient(Transport);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ITenantContext, TenantContext>();
            services.AddScoped(sp =>
            {
                var tenant = sp.GetRequiredService<ITenantContext>();
                ResolvedTenants.Add(tenant.CurrentTenantId ?? Guid.Empty);
                return new HospitalCrmDbContext(_dbOptions, tenant);
            });
            var options = Options.Create(new TypesenseOptions { Host = "synthetic-search.invalid:8108", ApiKey = "synthetic" });
            services.AddSingleton<IOptions<TypesenseOptions>>(options);
            services.AddSingleton<ITypesenseClientFactory>(new TypesenseClientFactory(options, new Part2SearchReportSearchTests.ClientFactory(_http)));
            services.AddScoped<IPatientSearchService, PatientSearchService>();
            services.AddScoped<ITypesenseHangfireJobs, TypesenseHangfireJobs>();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public async Task SeedAsync(int count)
        {
            using var db = new HospitalCrmDbContext(_dbOptions, TestMaintenanceTenantContext.Instance);
            db.Tenants.AddRange(new Tenant { Id = TenantA }, new Tenant { Id = TenantB });
            foreach (var tenant in new[] { TenantA, TenantB })
                for (var i = 0; i < (tenant == TenantA ? count : 3); i++)
                {
                    db.Patients.Add(new Patient { Id = Guid.NewGuid(), TenantId = tenant, Name = $"Synthetic {i}", Phone = "9876543210" });
                    db.Drugs.Add(new Drug { Id = Guid.NewGuid(), TenantId = tenant, Name = $"Synthetic {i}", GenericName = "Synthetic" });
                }
            await db.SaveChangesAsync();
        }

        public async Task RunAsync(bool medicines, CancellationToken ct = default)
        {
            using var scope = _provider.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<ITypesenseHangfireJobs>();
            if (medicines) await job.ReindexMedicinesTypesenseAsync(ct);
            else await job.ReindexTypesenseAsync(ct);
        }

        public void Dispose() { _provider.Dispose(); _http.Dispose(); }
    }
}
