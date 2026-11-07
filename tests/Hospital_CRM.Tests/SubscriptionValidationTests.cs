using System.Security.Claims;
using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// A4/D5: PATCH /platform-admin/tenants/{id} must reject values that are not real enum members.
/// Enum.TryParse accepts any number, so "99" previously stored subscriptionStatus = 99, which
/// SubscriptionPolicy treats as Full access.
/// </summary>
public class SubscriptionValidationTests
{
    private class FakeKeyService : IPersistentKeyService
    {
        public string KeySource => "Test";
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default) => Task.FromResult(Encoding.UTF8.GetBytes("fake_sig"));
        public RsaSecurityKey GetPrivateKey() => null!;
        public RsaSecurityKey GetPublicKey() => null!;
        public object GetJwks() => new();
    }

    private static HospitalCrmDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase($"TenantPatchDb_{Guid.NewGuid()}")
            .Options, TestMaintenanceTenantContext.Instance);

    private static PlatformAdminController CreateController(HospitalCrmDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "Hospital_CRM_Test",
                ["Jwt:Audience"] = "Hospital_CRM_Test",
                ["Jwt:KeyId"] = "test-key-id",
                ["Jwt:AccessTokenExpiryMinutes"] = "15"
            })
            .Build();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "PlatformAdmin"),
            new("role", "PlatformAdmin")
        };

        return new PlatformAdminController(
            db, new FakeKeyService(), config,
            new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
            }
        };
    }

    private static async Task<(HospitalCrmDbContext db, Guid tenantId)> SeedAsync()
    {
        var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Validation Clinic",
            SubscriptionTier = SubscriptionTier.Starter,
            SubscriptionStatus = SubscriptionStatus.Active,
            ActivatedModules = "core"
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return (db, tenant.Id);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("not-a-status")]
    [InlineData("-1")]
    public async Task UpdateTenant_Rejects_Undefined_Status(string status)
    {
        var (db, tenantId) = await SeedAsync();
        using var _ = db;
        var controller = CreateController(db);

        var result = await controller.UpdateTenant(tenantId, new UpdateTenantRequest(null, status, null, null), default);

        Assert.IsType<BadRequestObjectResult>(result);
        var saved = await db.Tenants.SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, saved.SubscriptionStatus);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("not-a-tier")]
    public async Task UpdateTenant_Rejects_Undefined_Tier(string tier)
    {
        var (db, tenantId) = await SeedAsync();
        using var _ = db;
        var controller = CreateController(db);

        var result = await controller.UpdateTenant(tenantId, new UpdateTenantRequest(tier, null, null, null), default);

        Assert.IsType<BadRequestObjectResult>(result);
        var saved = await db.Tenants.SingleAsync();
        Assert.Equal(SubscriptionTier.Starter, saved.SubscriptionTier);
    }

    [Fact]
    public async Task UpdateTenant_Accepts_Valid_Status_And_Tier()
    {
        var (db, tenantId) = await SeedAsync();
        using var _ = db;
        var controller = CreateController(db);

        var result = await controller.UpdateTenant(
            tenantId, new UpdateTenantRequest("professional", "pastdue", null, null), default);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.Tenants.SingleAsync();
        Assert.Equal(SubscriptionTier.Professional, saved.SubscriptionTier);
        Assert.Equal(SubscriptionStatus.PastDue, saved.SubscriptionStatus);
    }
}
