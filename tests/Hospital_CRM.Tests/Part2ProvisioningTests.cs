using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public sealed class Part2ProvisioningTests
{
    private static readonly ProvisionTenantRequest Request = new("Tenant", "Clinic", "Admin", "invited@example.test",
        "Trial", "Trial", ["core", "lab"], null, "Asia/Kolkata", "INR");

    [Theory]
    [InlineData("handoff")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("not_configured")]
    public async Task FreshContextReplayPreservesOutcomeAndNeverRepairsDeletedInvitation(string outcome)
    {
        await using var f = new Part2IdentityFixture();
        var actor = await Actor(f.Db);
        var protection = new EphemeralDataProtectionProvider();
        var email = new Part2IdentityEmail(outcome);
        if (outcome == "not_configured") f.Config["App:PublicBaseUrl"] = null;
        var first = Controller(f, f.Db, actor, protection, email);
        var original = Created(await first.ProvisionTenant(Request, default));
        var token = original.GetProperty("inviteToken").GetString()!;
        var receipt = await f.Db.TenantProvisioningReceipts.SingleAsync();
        Assert.DoesNotContain(token, JsonSerializer.Serialize(receipt));
        Assert.Equal(outcome, original.GetProperty("emailStatus").GetString());
        Assert.Equal("no-store", first.Response.Headers.CacheControl);
        f.Db.StaffInvites.Remove(await f.Db.StaffInvites.SingleAsync());
        await f.Db.SaveChangesAsync();

        await using var fresh = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(f.DatabaseName, f.DatabaseRoot).Options, TestMaintenanceTenantContext.Instance);
        var retry = Controller(f, fresh, actor, protection, email);
        Assert.Equal(original.GetRawText(), Created(await retry.ProvisionTenant(Request, default)).GetRawText());
        Assert.Equal("true", retry.Response.Headers["X-Idempotency-Replay"]);
        Assert.Empty(await fresh.StaffInvites.ToListAsync());
        Assert.Single(await fresh.Tenants.ToListAsync());
        Assert.Single(await fresh.Clinics.ToListAsync());
        Assert.Single(await fresh.TenantFeatureFlags.ToListAsync());
        Assert.Single(await fresh.TenantProvisioningReceipts.ToListAsync());
        Assert.Equal(outcome == "not_configured" ? 0 : 1, email.Messages.Count);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("ClinicName")]
    [InlineData("AdminName")]
    [InlineData("AdminEmail")]
    [InlineData("SubscriptionTier")]
    [InlineData("SubscriptionStatus")]
    [InlineData("ActivatedModules")]
    [InlineData("SubscriptionEndsAt")]
    [InlineData("Timezone")]
    [InlineData("Currency")]
    public async Task EveryPayloadFieldIsBoundToTheOriginalKey(string field)
    {
        await using var f = new Part2IdentityFixture();
        var actor = await Actor(f.Db);
        var email = new Part2IdentityEmail();
        var controller = Controller(f, f.Db, actor, new EphemeralDataProtectionProvider(), email);
        Created(await controller.ProvisionTenant(Request, default));
        var changed = field switch
        {
            "Name" => Request with { Name = "" },
            "ClinicName" => Request with { ClinicName = "Changed" },
            "AdminName" => Request with { AdminName = "Changed" },
            "AdminEmail" => Request with { AdminEmail = "invalid" },
            "SubscriptionTier" => Request with { SubscriptionTier = "invalid" },
            "SubscriptionStatus" => Request with { SubscriptionStatus = "invalid" },
            "ActivatedModules" => Request with { ActivatedModules = ["core"] },
            "SubscriptionEndsAt" => Request with { SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(1) },
            "Timezone" => Request with { Timezone = "UTC" },
            "Currency" => Request with { Currency = "USD" },
            _ => throw new InvalidOperationException()
        };
        var result = Assert.IsType<ConflictObjectResult>(await controller.ProvisionTenant(changed, default));
        Assert.Equal("idempotency_key_conflict", Part2IdentityFixture.Body(result).GetProperty("error").GetString());
        Assert.Single(await f.Db.Tenants.ToListAsync());
        Assert.Single(await f.Db.StaffInvites.ToListAsync());
        Assert.Single(email.Messages);
    }

    [Theory]
    [InlineData("missing_key")]
    [InlineData("long_key")]
    [InlineData("missing_actor")]
    [InlineData("demoted_actor")]
    [InlineData("corrupt_receipt")]
    public async Task InvalidKeyAuthorityOrProtectionNeverCreatesOrResends(string failure)
    {
        await using var f = new Part2IdentityFixture();
        var actor = await Actor(f.Db);
        var email = new Part2IdentityEmail();
        var controller = Controller(f, f.Db, actor, new EphemeralDataProtectionProvider(), email);
        if (failure is "demoted_actor" or "corrupt_receipt") Created(await controller.ProvisionTenant(Request, default));
        switch (failure)
        {
            case "missing_key": controller.Request.Headers.Clear(); break;
            case "long_key": controller.Request.Headers["Idempotency-Key"] = new string('x', 129); break;
            case "missing_actor": f.Db.Users.Remove(await f.Db.Users.SingleAsync()); break;
            case "demoted_actor": (await f.Db.Users.SingleAsync()).Role = UserRole.Doctor; break;
            case "corrupt_receipt": (await f.Db.TenantProvisioningReceipts.SingleAsync()).ProtectedResponse = "corrupt"; break;
        }
        await f.Db.SaveChangesAsync();
        var result = await controller.ProvisionTenant(Request, default);
        if (failure is "missing_actor" or "demoted_actor") Assert.IsType<ForbidResult>(result);
        else if (failure == "corrupt_receipt") Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        else Assert.IsType<BadRequestObjectResult>(result);
        var count = failure is "demoted_actor" or "corrupt_receipt" ? 1 : 0;
        Assert.Equal(count, await f.Db.Tenants.CountAsync());
        Assert.Equal(count, await f.Db.StaffInvites.CountAsync());
        Assert.Equal(count, await f.Db.TenantProvisioningReceipts.CountAsync());
        Assert.Equal(count, email.Messages.Count);
    }

    private static async Task<Guid> Actor(HospitalCrmDbContext db)
    {
        var actor = new User { Id = Guid.NewGuid(), Role = UserRole.PlatformAdmin, Email = "platform@example.test" };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        return actor.Id;
    }

    private static PlatformAdminController Controller(Part2IdentityFixture f, HospitalCrmDbContext db, Guid actor,
        IDataProtectionProvider protection, Part2IdentityEmail email)
    {
        var http = new DefaultHttpContext { User = new(new ClaimsIdentity(
            [new Claim("sub", actor.ToString()), new Claim("role", "PlatformAdmin")], "test")) };
        http.Request.Headers["Idempotency-Key"] = "  provision-key  ";
        return new(db, null!, f.Config, null!, email, dataProtection: protection) { ControllerContext = new() { HttpContext = http } };
    }

    internal static JsonElement Created(IActionResult result)
    {
        Assert.Equal(201, Assert.IsType<ObjectResult>(result).StatusCode);
        return Part2IdentityFixture.Body(result);
    }
}
