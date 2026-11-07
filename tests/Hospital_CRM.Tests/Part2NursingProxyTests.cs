using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Hospital_CRM.Tests.Part2NursingTests;

namespace Hospital_CRM.Tests;

public class Part2NursingProxyTests
{
    public static IEnumerable<object[]> DeniedCases => new[] {
        "delegator-removed", "delegator-suspended", "delegator-role", "delegator-clinic",
        "designee-removed", "designee-suspended", "designee-role", "designee-clinic",
        "future", "expired", "revoked", "self", "foreign-tenant", "foreign-clinic", "suffix", "extra-permission",
        "actor-party", "delegator-party"
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(DeniedCases))]
    public async Task ProxyRevalidatedAtBothApprovalPaths_RejectsWithoutRosterOrLeaveWrites(string scenario)
    {
        await using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, TestMaintenanceTenantContext.Instance);
        var s = await SeedAsync(db);
        await VerifyDeniedAsync(db, db, s, scenario);
    }

    internal static async Task VerifyDeniedAsync(HospitalCrmDbContext db, HospitalCrmDbContext writer, Scenario s, string scenario)
    {
        var actor = scenario == "actor-party" ? s.Nurse : s.Delegator;
        var delegator = scenario == "delegator-party" ? s.Nurse : s.Admin;
        var now = DateTimeOffset.UtcNow;
        var proxy = Assert.IsType<AuthorityProxy>(Assert.IsType<OkObjectResult>(await Roster(db, s, s.Admin).CreateAuthorityProxy(
            new(delegator.Id, actor.Id, "roster.approve,leave.approve", now.AddMinutes(-1), now.AddHours(1), null), default)).Value);
        var swap = new ShiftChangeRequest { TenantId = s.B, ClinicId = s.ClinicB, RequestType = ShiftChangeType.DirectSwap,
            Status = ShiftChangeStatus.Accepted, RequesterNurseProfileId = s.NurseProfile.Id, RequesterShiftId = s.Shift.Id,
            TargetNurseProfileId = s.PeerProfile.Id, TargetShiftId = s.PeerShift.Id };
        var leave = new LeaveRequest { Id = Guid.NewGuid(), TenantId = s.B, NurseProfileId = s.NurseProfile.Id,
            StartDate = s.Day, EndDate = s.Day, Status = LeaveStatus.Pending, Type = LeaveType.Sick };
        writer.AddRange(swap, leave);
        var stored = await writer.AuthorityProxies.SingleAsync(p => p.Id == proxy.Id);
        if (scenario.StartsWith("delegator-") && scenario != "delegator-party" || scenario.StartsWith("designee-"))
        {
            var party = scenario.StartsWith("delegator-") ? delegator : actor;
            var membership = await writer.TenantMemberships.SingleAsync(m => m.TenantId == s.B && m.UserId == party.Id);
            if (scenario.EndsWith("removed")) writer.Remove(membership);
            if (scenario.EndsWith("suspended")) membership.Status = "suspended";
            if (scenario.EndsWith("role")) membership.Role = UserRole.Doctor;
            if (scenario.EndsWith("clinic")) membership.ClinicId = s.ClinicA;
        }
        switch (scenario)
        {
            case "future": stored.ValidFrom = now.AddHours(2); stored.ValidUntil = now.AddHours(3); break;
            case "expired": stored.ValidUntil = now.AddSeconds(-1); break;
            case "revoked": stored.IsRevoked = true; break;
            case "self": stored.DelegatorUserId = stored.DesigneeUserId; break;
            case "foreign-tenant": stored.TenantId = s.A; break;
            case "foreign-clinic": stored.ClinicId = s.ClinicA; break;
            case "suffix": stored.Permissions = "roster.approve.all,leave.approve.all"; break;
            case "extra-permission": stored.Permissions = "roster.approve,leave.approve,payment.record"; break;
        }
        await writer.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var swapResult = await Roster(db, s, actor).ApproveShiftChange(swap.Id, null, default);
        var leaveResult = await Nurse(db, s, actor).ReviewLeave(leave.Id, new("Approved", null), default);
        if (scenario.EndsWith("party"))
        {
            Assert.IsType<BadRequestObjectResult>(swapResult);
            Assert.IsType<BadRequestObjectResult>(leaveResult);
        }
        else
        {
            Assert.IsType<ForbidResult>(swapResult);
            Assert.IsType<ForbidResult>(leaveResult);
        }
        writer.ChangeTracker.Clear();
        var unchangedSwap = await writer.ShiftChangeRequests.AsNoTracking().SingleAsync(x => x.Id == swap.Id);
        Assert.Equal(ShiftChangeStatus.Accepted, unchangedSwap.Status);
        Assert.Null(unchangedSwap.ApprovedByUserId); Assert.Null(unchangedSwap.ApprovedOnBehalfOfUserId);
        var unchangedLeave = await writer.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == leave.Id);
        Assert.Equal(LeaveStatus.Pending, unchangedLeave.Status); Assert.Null(unchangedLeave.ReviewedBy);
        Assert.Equal(s.NurseProfile.Id, (await writer.NurseShifts.AsNoTracking().SingleAsync(x => x.Id == s.Shift.Id)).NurseProfileId);
        Assert.Equal(s.PeerProfile.Id, (await writer.NurseShifts.AsNoTracking().SingleAsync(x => x.Id == s.PeerShift.Id)).NurseProfileId);
        Assert.Equal(s.ForeignProfile.Id, (await writer.NurseShifts.AsNoTracking().SingleAsync(x => x.Id == s.ForeignShift.Id)).NurseProfileId);
        await AssertPrimaryUnchangedAsync(writer, s);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("reassigned")]
    [InlineData("inactive-requester")]
    [InlineData("inactive-target")]
    [InlineData("foreign-shift")]
    public async Task SwapApproval_RevalidatesPartiesAndCurrentShiftState(string scenario)
    {
        await using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, TestMaintenanceTenantContext.Instance);
        var s = await SeedAsync(db);
        var swap = new ShiftChangeRequest { TenantId = s.B, ClinicId = s.ClinicB, RequestType = ShiftChangeType.DirectSwap,
            Status = ShiftChangeStatus.Accepted, RequesterNurseProfileId = s.NurseProfile.Id, RequesterShiftId = s.Shift.Id,
            TargetNurseProfileId = s.PeerProfile.Id, TargetShiftId = s.PeerShift.Id };
        db.Add(swap);
        if (scenario == "locked") s.PeerShift.IsLocked = true;
        if (scenario == "reassigned") s.PeerShift.NurseProfileId = s.DelegatorProfile.Id;
        if (scenario.StartsWith("inactive"))
            (await db.TenantMemberships.SingleAsync(m => m.TenantId == s.B &&
                m.UserId == (scenario == "inactive-requester" ? s.Nurse.Id : s.Peer.Id))).Status = "suspended";
        if (scenario == "foreign-shift") swap.TargetShiftId = s.ForeignShift.Id;
        await db.SaveChangesAsync();
        var originalTarget = s.PeerShift.NurseProfileId;
        Assert.IsType<ForbidResult>(await Roster(db, s, s.Admin).ApproveShiftChange(swap.Id, null, default));
        Assert.Equal(ShiftChangeStatus.Accepted, swap.Status);
        Assert.Equal(s.NurseProfile.Id, s.Shift.NurseProfileId);
        Assert.Equal(originalTarget, s.PeerShift.NurseProfileId);
    }
}
