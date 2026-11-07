using System.Security.Claims;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hospital_CRM.Tests;

public class RequireModuleAttributeTests
{
    private static (RequireModuleAttribute gate, IServiceProvider services, Guid tenantId) Fixture(
        string activatedModules, bool? labFlagOverride = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase($"ModuleGate_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Gate Test",
            ActivatedModules = activatedModules,
            SubscriptionTier = SubscriptionTier.Starter,
            SubscriptionStatus = SubscriptionStatus.Active
        });

        if (labFlagOverride.HasValue)
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FlagName = "lab",
                Enabled = labFlagOverride.Value,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = Guid.NewGuid()
            });
        }

        db.SaveChanges();

        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IModuleGateService, ModuleGateService>()
            .BuildServiceProvider();

        return (new RequireModuleAttribute("lab"), services, tenantId);
    }

    private static async Task<(bool executed, IActionResult? result)> Run(
        RequireModuleAttribute gate, IServiceProvider services, Guid? tenantId)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        if (tenantId.HasValue)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", tenantId.Value.ToString())], "test"));
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());

        var executed = false;
        await gate.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });

        return (executed, context.Result);
    }

    [Fact]
    public async Task EntitledTenant_IsAllowed()
    {
        var (gate, services, tenantId) = Fixture("core,lab");
        var (executed, result) = await Run(gate, services, tenantId);

        Assert.True(executed);
        Assert.Null(result);
    }

    [Fact]
    public async Task TenantWithoutModule_Gets403()
    {
        var (gate, services, tenantId) = Fixture("core");
        var (executed, result) = await Run(gate, services, tenantId);

        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task FeatureFlagOverride_DisablesEntitledModule()
    {
        var (gate, services, tenantId) = Fixture("core,lab", labFlagOverride: false);
        var (executed, result) = await Run(gate, services, tenantId);

        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task MissingTenantClaim_Gets403()
    {
        var (gate, services, _) = Fixture("core,lab");
        var (executed, result) = await Run(gate, services, tenantId: null);

        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
