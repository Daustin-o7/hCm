using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part1PatientSearchTests
{
    [Theory]
    [InlineData(false, "missing-user", 401)]
    [InlineData(true, "missing-user", 401)]
    [InlineData(false, "empty-user", 401)]
    [InlineData(true, "empty-user", 401)]
    [InlineData(false, "missing-tenant", 401)]
    [InlineData(true, "missing-tenant", 401)]
    [InlineData(false, "empty-tenant", 401)]
    [InlineData(true, "empty-tenant", 401)]
    [InlineData(false, "missing-db-scope", 403)]
    [InlineData(true, "missing-db-scope", 403)]
    [InlineData(false, "mismatched-tenant", 403)]
    [InlineData(true, "mismatched-tenant", 403)]
    public async Task Search_DeniesInvalidScopeBeforeAuditOrProvider(bool duplicates, string scenario, int status)
    {
        var tenant = Guid.NewGuid();
        var scope = new TenantContext();
        using var lease = scenario == "missing-db-scope" ? null : scope.PushTenant(tenant);
        await using var db = Db(scope);
        var search = new SearchStub(_ => throw new InvalidOperationException("Provider must not run."));
        var controller = Controller(db, search, scenario == "mismatched-tenant" ? Guid.NewGuid() : tenant);
        var identity = (ClaimsIdentity)controller.User.Identity!;
        var claimType = scenario.EndsWith("user") ? "sub" : "tenant_id";
        if (scenario.StartsWith("missing-") && scenario != "missing-db-scope")
            identity.RemoveClaim(identity.FindFirst(claimType)!);
        if (scenario.StartsWith("empty-"))
        {
            identity.RemoveClaim(identity.FindFirst(claimType)!);
            identity.AddClaim(new Claim(claimType, Guid.Empty.ToString()));
        }

        var result = Assert.IsAssignableFrom<ObjectResult>(await Run(controller, duplicates));
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(0, search.Calls);
        Assert.Empty(db.PatientAuditLogs);
        Assert.Empty(db.AuthAuditLogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchUnavailable_ReturnsStructured503InsteadOfNoMatches(bool duplicates)
    {
        var tenant = Guid.NewGuid();
        var scope = new TenantContext();
        using var lease = scope.PushTenant(tenant);
        await using var db = Db(scope);
        var search = new SearchStub(_ => throw new SearchUnavailableException(new InvalidOperationException("private provider detail")));

        var result = Assert.IsType<ObjectResult>(await Run(Controller(db, search, tenant), duplicates));
        Assert.Equal(503, result.StatusCode);
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("search_unavailable", body.GetProperty("error").GetString());
        Assert.Contains("Retry", body.GetProperty("message").GetString());
        Assert.DoesNotContain("private provider detail", body.ToString());
        Assert.Equal(1, search.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_UsesResolvedScopeAndPreservesPatientMatch(bool duplicates)
    {
        var tenant = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var scope = new TenantContext();
        using var lease = scope.PushTenant(tenant);
        await using var db = Db(scope);
        var search = new SearchStub(_ => [new(patientId, "Synthetic patient", "9876543210", "1990-01-01", "Female", "Synthetic address", 100)]);

        var result = Assert.IsType<OkObjectResult>(await Run(Controller(db, search, tenant), duplicates));
        var body = JsonSerializer.SerializeToElement(result.Value);
        var match = Assert.Single((duplicates ? body.GetProperty("matches") : body).EnumerateArray());
        Assert.Equal(patientId, match.GetProperty("id").GetGuid());
        Assert.Equal("3210", match.GetProperty("phoneLast4").GetString());
        if (duplicates) Assert.True(body.GetProperty("duplicate").GetBoolean());
        else Assert.False(match.TryGetProperty("phone", out _));
        Assert.Equal(tenant, search.Tenant);
        Assert.Equal(1, search.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_CancellationPropagates(bool duplicates)
    {
        var tenant = Guid.NewGuid();
        var scope = new TenantContext();
        using var lease = scope.PushTenant(tenant);
        await using var db = Db(scope);
        using var cancellation = new CancellationTokenSource();
        var search = new SearchStub(ct =>
        {
            Assert.Equal(cancellation.Token, ct);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return [];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Controller(db, search, tenant), duplicates, cancellation.Token));
        Assert.Equal(1, search.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_AuditsAuthorizedCollectionAccessWithoutInventingPatientOrCopyingQuery(bool unavailable)
    {
        var tenant = Guid.NewGuid();
        var scope = new TenantContext();
        using var lease = scope.PushTenant(tenant);
        await using var db = Db(scope);
        var search = new SearchStub(_ =>
        {
            Assert.Single(db.AuthAuditLogs);
            if (unavailable) throw new SearchUnavailableException(new InvalidOperationException("offline"));
            return [];
        });
        var controller = Controller(db, search, tenant);

        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.Search("Sensitive patient query", default));

        Assert.Equal(unavailable ? 503 : 200, result.StatusCode);
        Assert.Empty(db.PatientAuditLogs);
        var audit = Assert.Single(db.AuthAuditLogs);
        Assert.Equal(tenant, audit.TenantId);
        Assert.Equal(Guid.Parse(controller.User.FindFirst("sub")!.Value), audit.UserId);
        Assert.Equal("patient_search_authorized", audit.EventType);
        Assert.Equal("success", audit.Outcome);
        Assert.Null(audit.Detail);
        Assert.Null(audit.Email);
    }

    private static HospitalCrmDbContext Db(TenantContext scope) => new(
        new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, scope);

    private static PatientsController Controller(HospitalCrmDbContext db, SearchStub search, Guid tenant)
    {
        var controller = new PatientsController(db, search);
        Part1SaleReadinessTests.Caller(controller, Guid.NewGuid(), tenant, "Receptionist");
        return controller;
    }

    private static Task<IActionResult> Run(PatientsController controller, bool duplicates, CancellationToken ct = default) =>
        duplicates ? controller.CheckDuplicate(new("Synthetic patient", "9876543210", null), ct) : controller.Search("Synthetic", ct);

    private sealed class SearchStub(Func<CancellationToken, List<PatientSearchHit>> run) : FakePatientSearchService
    {
        public int Calls { get; private set; }
        public Guid Tenant { get; private set; }
        public override Task<List<PatientSearchHit>> SearchAsync(string query, Guid tenantId, int limit = 10, CancellationToken ct = default)
        {
            Calls++;
            Tenant = tenantId;
            return Task.FromResult(run(ct));
        }
        public override Task<List<PatientSearchHit>> CheckDuplicatesAsync(string name, string? phone, DateOnly? dob, Guid tenantId, CancellationToken ct = default) =>
            SearchAsync(name, tenantId, ct: ct);
    }
}
