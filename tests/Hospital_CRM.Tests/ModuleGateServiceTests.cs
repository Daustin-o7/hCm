using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class ModuleGateServiceTests
{
    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: $"GateDb_{Guid.NewGuid()}")
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    [Fact]
    public async Task Core_Module_Is_Always_Enabled_When_Tenant_Is_Not_Empty()
    {
        using var db = CreateInMemoryDb();
        var service = new ModuleGateService(db);
        var tenantId = Guid.NewGuid();

        var result = await service.IsModuleEnabledAsync(tenantId, ModuleRegistry.Core);

        Assert.True(result);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("pharmacy")]
    [InlineData("lab")]
    public async Task Empty_TenantId_Returns_False(string moduleCode)
    {
        using var db = CreateInMemoryDb();
        var service = new ModuleGateService(db);

        var result = await service.IsModuleEnabledAsync(Guid.Empty, moduleCode);

        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown_module")]
    [InlineData("telemedicine")]
    public async Task Unknown_Or_Empty_Module_Code_Returns_False(string? moduleCode)
    {
        using var db = CreateInMemoryDb();
        var service = new ModuleGateService(db);
        var tenantId = Guid.NewGuid();

        var result = await service.IsModuleEnabledAsync(tenantId, moduleCode!);

        Assert.False(result);
    }

    [Fact]
    public async Task Nonexistent_Tenant_Returns_False()
    {
        using var db = CreateInMemoryDb();
        var service = new ModuleGateService(db);
        var tenantId = Guid.NewGuid();

        var result = await service.IsModuleEnabledAsync(tenantId, ModuleRegistry.Pharmacy);

        Assert.False(result);
    }

    [Fact]
    public async Task Unentitled_Module_Returns_False()
    {
        using var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Clinic Alpha",
            ActivatedModules = "core,lab",
            SubscriptionTier = SubscriptionTier.Starter,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var service = new ModuleGateService(db);
        var result = await service.IsModuleEnabledAsync(tenant.Id, ModuleRegistry.Pharmacy);

        Assert.False(result);
    }

    [Fact]
    public async Task Entitled_Module_Without_Override_Returns_True()
    {
        using var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Clinic Beta",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var service = new ModuleGateService(db);
        var result = await service.IsModuleEnabledAsync(tenant.Id, ModuleRegistry.Pharmacy);

        Assert.True(result);
    }

    [Fact]
    public async Task Entitled_Module_With_Disabled_Override_Returns_False()
    {
        using var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Clinic Gamma",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);

        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FlagName = "pharmacy",
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var service = new ModuleGateService(db);
        var result = await service.IsModuleEnabledAsync(tenant.Id, ModuleRegistry.Pharmacy);

        Assert.False(result);
    }

    [Fact]
    public async Task Entitled_Module_With_Enabled_Override_Returns_True()
    {
        using var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Clinic Delta",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Enterprise,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);

        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FlagName = "pharmacy",
            Enabled = true,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var service = new ModuleGateService(db);
        var result = await service.IsModuleEnabledAsync(tenant.Id, ModuleRegistry.Pharmacy);

        Assert.True(result);
    }

    [Fact]
    public async Task Two_Tenants_Toggle_Independence()
    {
        using var db = CreateInMemoryDb();

        var tenantA = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Tenant A",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };

        var tenantB = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Tenant B",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };

        db.Tenants.AddRange(tenantA, tenantB);

        // Tenant A has pharmacy disabled by override; Tenant B does not
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA.Id,
            FlagName = "pharmacy",
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Guid.NewGuid()
        });

        await db.SaveChangesAsync();

        var service = new ModuleGateService(db);

        var aEnabled = await service.IsModuleEnabledAsync(tenantA.Id, ModuleRegistry.Pharmacy);
        var bEnabled = await service.IsModuleEnabledAsync(tenantB.Id, ModuleRegistry.Pharmacy);

        Assert.False(aEnabled, "Tenant A pharmacy should be disabled via override");
        Assert.True(bEnabled, "Tenant B pharmacy should remain enabled independently");
    }

    [Fact]
    public async Task Gate_Performs_Zero_Database_Writes()
    {
        using var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Clinic Zero Writes",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var initialFlagCount = await db.TenantFeatureFlags.CountAsync();
        var service = new ModuleGateService(db);

        // Query multiple times for an un-flagged module
        await service.IsModuleEnabledAsync(tenant.Id, ModuleRegistry.Pharmacy);
        await service.IsModuleEnabledAsync(tenant.Id, ModuleRegistry.Lab);
        await service.IsModuleEnabledAsync(tenant.Id, "core");

        var finalFlagCount = await db.TenantFeatureFlags.CountAsync();
        Assert.Equal(initialFlagCount, finalFlagCount);
        Assert.False(db.ChangeTracker.HasChanges(), "Calling the module gate should not leave any tracked changes in DbContext");
    }

    [Fact]
    public async Task Module_Code_Is_Case_And_Whitespace_Insensitive()
    {
        using var db = CreateInMemoryDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Clinic Case Sensitivity",
            ActivatedModules = "core,pharmacy,inventory",
            SubscriptionTier = SubscriptionTier.Professional,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var service = new ModuleGateService(db);
        var result = await service.IsModuleEnabledAsync(tenant.Id, "   PHARMACY  ");

        Assert.True(result);
    }
}
