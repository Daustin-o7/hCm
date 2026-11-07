using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2SearchReportSearchTests
{
    [Theory]
    [InlineData("patients")]
    [InlineData("duplicates")]
    [InlineData("medicines")]
    public async Task MissingOrForeignScope_IsRejectedBeforeIndexAccess(string kind)
    {
        using var fixture = new SearchFixture();
        foreach (var tenant in new[] { Guid.Empty, Guid.NewGuid() })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Search(fixture, kind, tenant));
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData("patients", false)]
    [InlineData("patients", true)]
    [InlineData("duplicates", false)]
    [InlineData("duplicates", true)]
    [InlineData("medicines", false)]
    [InlineData("medicines", true)]
    public async Task EmptyOrUnavailableIndex_UsesOnlyCurrentTenantDatabase(string kind, bool unavailable)
    {
        using var fixture = new SearchFixture();
        await fixture.SeedAsync();
        fixture.Transport.Unavailable = unavailable;
        var ids = await Search(fixture, kind, fixture.Tenant);
        Assert.Equal(new[] { kind == "medicines" ? fixture.Drug.Id : fixture.Patient.Id }, ids);
    }

    [Fact]
    public async Task IndexedPatient_DoesNotContainAddress_AndKeepsRequiredSearchFields()
    {
        using var fixture = new SearchFixture();
        fixture.Patient.Address = "Synthetic private address";
        await fixture.Service.IndexAsync(fixture.Patient, default);
        var body = Assert.Single(fixture.Transport.Requests).Body!;
        Assert.DoesNotContain(fixture.Patient.Address, body);
        var document = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(fixture.Patient.Name, document.GetProperty("name").GetString());
        Assert.Equal(fixture.Tenant, document.GetProperty("tenant_id").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkImport_RejectsMixedTenantsBeforeAnyWrite(bool medicines)
    {
        using var fixture = new SearchFixture();
        if (medicines)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.IndexManyMedicinesAsync(new[]
            { fixture.Drug, new Drug { TenantId = fixture.ForeignPatient.TenantId } }, default));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.IndexManyAsync(new[] { fixture.Patient, fixture.ForeignPatient }, default));
        Assert.Empty(fixture.Transport.Requests);
    }

    [Fact]
    public async Task CancellationDuringImport_IsObservedBeforeSuccess()
    {
        using var fixture = new SearchFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.OnRequest = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.IndexManyAsync(new[] { fixture.Patient }, cancellation.Token));
    }

    [Fact]
    public async Task DuplicateCheck_UsesDatabaseEvenWhenIndexReturnsUnrelatedHits()
    {
        using var fixture = new SearchFixture();
        await fixture.SeedAsync();
        fixture.Transport.Document = new TypesensePatientDocument
        {
            Id = Guid.NewGuid().ToString(), TenantId = fixture.Tenant.ToString(), Name = "Stale result", Phone = "0000000000"
        };
        Assert.Equal(new[] { fixture.Patient.Id }, await Search(fixture, "duplicates", fixture.Tenant));
    }

    [Fact]
    public async Task IndexHits_AreRehydratedFromCurrentOwnedRows()
    {
        using var fixture = new SearchFixture();
        await fixture.SeedAsync();
        fixture.Transport.Document = new TypesensePatientDocument
        {
            Id = fixture.Patient.Id.ToString(), TenantId = fixture.Tenant.ToString(), Name = "Old name", Phone = "0000000000"
        };
        var hit = Assert.Single(await fixture.Service.SearchAsync("Patient", fixture.Tenant));
        Assert.Equal(fixture.Patient.Name, hit.Name);
        Assert.Equal(fixture.Patient.Phone, hit.Phone);
        Assert.Contains($"tenant_id:={fixture.Tenant}", Uri.UnescapeDataString(Assert.Single(fixture.Transport.Requests).Uri));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOrDeletedIndexHit_IsDiscardedAndFallbackRuns(bool spoofTenant)
    {
        using var fixture = new SearchFixture();
        await fixture.SeedAsync();
        fixture.Transport.Document = new TypesensePatientDocument
        {
            Id = fixture.ForeignPatient.Id.ToString(), TenantId = (spoofTenant ? fixture.Tenant : fixture.ForeignPatient.TenantId).ToString(),
            Name = "Patient foreign", Phone = "9999999999"
        };
        Assert.Equal(new[] { fixture.Patient.Id }, await Search(fixture, "patients", fixture.Tenant));
    }

    [Theory]
    [InlineData("patients")]
    [InlineData("duplicates")]
    [InlineData("medicines")]
    public async Task DatabaseFailure_IsNeverAnEmptySuccess(string kind)
    {
        using var fixture = new SearchFixture();
        fixture.Transport.Unavailable = true;
        fixture.Db.Dispose();
        await Assert.ThrowsAsync<SearchUnavailableException>(() => Search(fixture, kind, fixture.Tenant));
    }

    [Fact]
    public async Task WriteIndexing_RejectsForeignTenant_AndPreservesCommittedPatientOnOutage()
    {
        using var fixture = new SearchFixture();
        await fixture.SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.IndexAsync(fixture.ForeignPatient, default));
        fixture.Transport.Unavailable = true;
        await fixture.Service.IndexAsync(fixture.Patient, default);
        Assert.Equal(fixture.Patient.Id, Assert.Single(await fixture.Service.SearchAsync("Patient", fixture.Tenant)).Id);
        Assert.True(await fixture.Db.Patients.AnyAsync(p => p.Id == fixture.Patient.Id));
    }

    [Fact]
    public async Task ImportDocumentFailure_IsRetryableJobFailure()
    {
        using var fixture = new SearchFixture();
        await fixture.SeedAsync();
        fixture.Transport.ImportFails = true;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.IndexManyAsync(new[] { fixture.Patient }, default));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.IndexManyMedicinesAsync(new[] { fixture.Drug }, default));
    }

    [Fact]
    public async Task Delete_UsesAtomicTenantAndIdFilter()
    {
        using var fixture = new SearchFixture();
        await fixture.Service.DeleteAsync(fixture.ForeignPatient.Id, default);
        var request = Assert.Single(fixture.Transport.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        var uri = Uri.UnescapeDataString(request.Uri);
        Assert.Contains($"tenant_id:={fixture.Tenant}", uri);
        Assert.Contains($"id:={fixture.ForeignPatient.Id}", uri);
    }

    [Fact]
    public async Task CancelledSearch_DoesNotBecomeEmptySuccess()
    {
        using var fixture = new SearchFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SearchAsync("Patient", fixture.Tenant, ct: cancellation.Token));
    }

    private static async Task<Guid[]> Search(SearchFixture fixture, string kind, Guid tenant) => kind switch
    {
        "patients" => (await fixture.Service.SearchAsync("Patient", tenant)).Select(h => h.Id).ToArray(),
        "duplicates" => (await fixture.Service.CheckDuplicatesAsync("Patient", "9876543210", null, tenant)).Select(h => h.Id).ToArray(),
        _ => (await fixture.Service.MedicinesSearchAsync("Paracetamol", tenant)).Select(h => h.Id).ToArray()
    };

    private sealed class SearchFixture : IDisposable
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public HospitalCrmDbContext Db { get; }
        public PatientSearchService Service { get; }
        public SearchTransport Transport { get; } = new();
        public Patient Patient { get; }
        public Patient ForeignPatient { get; }
        public Drug Drug { get; }
        private readonly IDisposable _scope;
        private readonly HttpClient _http;

        public SearchFixture()
        {
            var context = new TenantContext();
            _scope = context.PushTenant(Tenant);
            Db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, context);
            _http = new HttpClient(Transport);
            var options = Options.Create(new TypesenseOptions { Host = "synthetic-search.invalid:8108", ApiKey = "synthetic" });
            Service = new PatientSearchService(new TypesenseClientFactory(options, new ClientFactory(_http)), options, Db,
                NullLogger<PatientSearchService>.Instance);
            Patient = new Patient { Id = Guid.NewGuid(), TenantId = Tenant, Name = "Patient current", Phone = "9876543210" };
            ForeignPatient = new Patient { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Patient foreign", Phone = Patient.Phone };
            Drug = new Drug { TenantId = Tenant, Name = "Paracetamol 650mg", GenericName = "Paracetamol" };
        }

        public async Task SeedAsync()
        {
            Db.AddRange(Patient, ForeignPatient, Drug, new Drug { TenantId = ForeignPatient.TenantId, Name = Drug.Name, GenericName = Drug.GenericName });
            await Db.SaveChangesAsync();
        }

        public void Dispose() { Db.Dispose(); _http.Dispose(); _scope.Dispose(); }
    }

    internal sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    internal sealed class SearchTransport : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public bool ImportFails { get; set; }
        public bool MissingImportResult { get; set; }
        public Action? OnRequest { get; set; }
        public object? Document { get; set; }
        public List<JsonElement>? StoredDocuments { get; set; }
        public List<(HttpMethod Method, string Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), content));
            OnRequest?.Invoke();
            if (Unavailable) throw new HttpRequestException("Synthetic unavailable search");
            if (StoredDocuments != null)
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (request.RequestUri.AbsolutePath.EndsWith("/import"))
                {
                    if (!ImportFails)
                        foreach (var line in content!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var document = JsonSerializer.Deserialize<JsonElement>(line);
                            StoredDocuments.RemoveAll(d => d.GetProperty("id").GetString() == document.GetProperty("id").GetString());
                            StoredDocuments.Add(document);
                        }
                }
                else
                {
                    var filter = query["filter_by"].ToString();
                    var ids = Regex.Matches(filter, "[0-9a-fA-F-]{36}").Select(m => Guid.Parse(m.Value)).ToArray();
                    var tenant = ids[0];
                    if (request.Method == HttpMethod.Delete)
                    {
                        var count = StoredDocuments.RemoveAll(d => d.GetProperty("tenant_id").GetGuid() == tenant && ids.Skip(1).Contains(d.GetProperty("id").GetGuid()));
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { num_deleted = count })) };
                    }
                    var page = int.Parse(query["page"].ToString());
                    var perPage = int.Parse(query["per_page"].ToString());
                    var documents = StoredDocuments.Where(d => d.GetProperty("tenant_id").GetGuid() == tenant).ToArray();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        found = documents.Length, hits = documents.Skip((page - 1) * perPage).Take(perPage).Select(d => new { document = d }), page, search_time_ms = 1
                    })) };
                }
            }
            var body = request.RequestUri!.AbsolutePath.EndsWith("/import")
                ? string.Join('\n', (content ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(MissingImportResult ? 1 : 0)
                    .Select(_ => JsonSerializer.Serialize(new { success = !ImportFails, error = ImportFails ? "Synthetic rejected document" : null })))
                : request.Method == HttpMethod.Delete ? "{\"num_deleted\":0}"
                : JsonSerializer.Serialize(new { found = Document == null ? 0 : 1, hits = Document == null ? Array.Empty<object>() : new object[] { new { document = Document, text_match = 150 } }, search_time_ms = 1, page = 1 });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
