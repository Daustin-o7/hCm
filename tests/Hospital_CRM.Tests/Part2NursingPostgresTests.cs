using Hospital_CRM.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part2NursingTests;

namespace Hospital_CRM.Tests;

public class Part2NursingPostgresTests(Part2TenantBoundaryPostgresFixture fixture)
    : IClassFixture<Part2TenantBoundaryPostgresFixture>
{
    [Part2TenantBoundaryPostgresTheory]
    [InlineData("profiles-leave")]
    [InlineData("care")]
    [InlineData("shifts")]
    [InlineData("roster-proxies")]
    public async Task NamedEndpoints_UseSelectedB_AsAppUser(string area)
    {
        await using var owner = fixture.OwnerDb();
        var s = await SeedAsync(owner);
        var context = new TenantContext();
        using var selected = context.PushTenant(s.B);
        await using var db = fixture.AppDb(context);
        Assert.Equal("app_user", await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        await ExerciseAsync(db, s, area);
        Assert.Empty(await db.NurseProfiles.IgnoreQueryFilters().Where(p => p.TenantId == s.A).ToListAsync());
        Assert.Equal(0, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.\"NurseShifts\" SET \"Notes\" = 'unauthorized' WHERE \"Id\" = {s.ForeignShift.Id}"));
        owner.ChangeTracker.Clear();
        Assert.Equal(s.ForeignProfile.Id, (await owner.NurseShifts.SingleAsync(x => x.Id == s.ForeignShift.Id)).NurseProfileId);
        Assert.False((await owner.NurseShifts.SingleAsync(x => x.Id == s.ForeignShift.Id)).IsLocked);
        await AssertPrimaryUnchangedAsync(owner, s);
    }

    [Part2TenantBoundaryPostgresTheory]
    [MemberData(nameof(Part2NursingProxyTests.DeniedCases), MemberType = typeof(Part2NursingProxyTests))]
    public async Task StaleProxies_DenyBothApprovals_AsAppUser(string scenario)
    {
        await using var owner = fixture.OwnerDb();
        var s = await SeedAsync(owner);
        var context = new TenantContext();
        using var selected = context.PushTenant(s.B);
        await using var db = fixture.AppDb(context);
        await Part2NursingProxyTests.VerifyDeniedAsync(db, owner, s, scenario);
    }
}
