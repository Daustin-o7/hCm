using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2NursingTests
{
    [Theory]
    [InlineData("profiles-leave")]
    [InlineData("care")]
    [InlineData("shifts")]
    [InlineData("roster-proxies")]
    public async Task PrimaryA_SelectedB_UsesCurrentMembershipAcrossNamedEndpoints(string area)
    {
        await using var db = MemoryDb();
        var s = await SeedAsync(db);
        await ExerciseAsync(db, s, area);
        await AssertPrimaryUnchangedAsync(db, s);
    }

    [Theory]
    [InlineData("missing-tenant")]
    [InlineData("missing-clinic")]
    [InlineData("foreign-clinic")]
    [InlineData("removed")]
    [InlineData("suspended")]
    [InlineData("changed-role")]
    public async Task InvalidSelectedScope_DeniesAllFourControllersWithoutWrites(string scenario)
    {
        await using var db = MemoryDb();
        var s = await SeedAsync(db);
        var membership = await db.TenantMemberships.SingleAsync(m => m.UserId == s.Admin.Id && m.TenantId == s.B);
        if (scenario == "removed") db.Remove(membership);
        if (scenario == "suspended") membership.Status = "suspended";
        if (scenario == "changed-role") membership.Role = UserRole.Nurse;
        await db.SaveChangesAsync();
        var principal = Principal(s.Admin, s);
        var identity = (ClaimsIdentity)principal.Identity!;
        if (scenario == "missing-tenant") identity.RemoveClaim(identity.FindFirst("tenant_id"));
        if (scenario is "missing-clinic" or "foreign-clinic") identity.RemoveClaim(identity.FindFirst("clinicId"));
        if (scenario == "foreign-clinic") identity.AddClaim(new Claim("clinicId", s.ClinicA.ToString()));
        var nurse = Nurse(db, s, s.Admin);
        var care = Care(db, s, s.Admin);
        var shifts = Shifts(db, s, s.Admin);
        var roster = Roster(db, s, s.Admin);
        foreach (var controller in new ControllerBase[] { nurse, care, shifts, roster }) controller.HttpContext.User = principal;
        Assert.IsType<ForbidResult>(await nurse.ListNurses(default));
        Assert.IsType<ForbidResult>(await nurse.SetAvailability(s.NurseProfile.Id, Availability(s), default));
        Assert.IsType<ForbidResult>(await care.List(null, false, default));
        Assert.IsType<ForbidResult>(await care.Assign(new(s.PatientB.Id, s.NurseProfile.Id, null, null), default));
        Assert.IsType<ForbidResult>(await shifts.List(null, null, default));
        Assert.IsType<ForbidResult>(await shifts.Create(new(s.Day, ShiftType.Night, null, null), default));
        Assert.IsType<ForbidResult>(await roster.ListRosters(default));
        Assert.IsType<ForbidResult>(await roster.CreateRoster(new("Denied", s.Day, s.Day), default));
        Assert.Empty(db.NurseAvailabilities.Where(x => x.TenantId == s.B));
        Assert.Empty(db.PatientNurseAssignments.Where(x => x.TenantId == s.B));
        Assert.Empty(db.StaffRosters.Where(x => x.TenantId == s.B));
        Assert.Equal(3, await db.NurseShifts.CountAsync());
        Assert.Empty(db.NurseAuditLogs);
    }

    [Fact]
    public async Task LegacyForeignReferences_DoNotLeakThroughJoinedListsOrCounts()
    {
        await using var db = MemoryDb();
        var s = await SeedAsync(db);
        var foreignTemplate = await db.ShiftTemplates.SingleAsync(t => t.TenantId == s.A);
        var open = new NurseShift { Id = Guid.NewGuid(), TenantId = s.B, ClinicId = s.ClinicB, Date = s.Day,
            Status = ShiftStatus.Open, ShiftTemplateId = foreignTemplate.Id };
        db.AddRange(open,
            new NurseShift { Id = Guid.NewGuid(), TenantId = s.B, ClinicId = s.ClinicB, Date = s.Day, NurseProfileId = s.ForeignProfile.Id, Status = ShiftStatus.Assigned },
            new NurseShiftApplication { Id = Guid.NewGuid(), TenantId = s.A, ShiftId = open.Id, NurseProfileId = s.ForeignProfile.Id, Status = ShiftApplicationStatus.Applied },
            new NurseShiftApplication { Id = Guid.NewGuid(), TenantId = s.B, ShiftId = open.Id, NurseProfileId = s.InactiveProfile.Id, Status = ShiftApplicationStatus.Applied },
            new ShiftChangeRequest { TenantId = s.B, ClinicId = s.ClinicB, RequesterNurseProfileId = s.NurseProfile.Id, RequesterShiftId = s.Shift.Id,
                TargetNurseProfileId = s.ForeignProfile.Id, TargetShiftId = s.ForeignShift.Id },
            new AuthorityProxy { TenantId = s.B, ClinicId = s.ClinicB, DelegatorUserId = s.Foreign.Id, DesigneeUserId = s.Delegator.Id,
                ValidFrom = DateTimeOffset.UtcNow.AddHours(-1), ValidUntil = DateTimeOffset.UtcNow.AddHours(1), Permissions = "roster.approve" },
            new StaffAnnouncement { TenantId = s.B, ClinicId = s.ClinicB, Title = "B", Content = "B", AuthorUserId = s.Foreign.Id },
            new PatientNurseAssignment { Id = Guid.NewGuid(), TenantId = s.B, PatientId = s.PatientA.Id, NurseProfileId = s.NurseProfile.Id },
            new ShiftHandover { Id = Guid.NewGuid(), TenantId = s.B, PatientId = s.PatientB.Id, AuthorNurseProfileId = s.NurseProfile.Id, ToNurseProfileId = s.ForeignProfile.Id });
        await db.SaveChangesAsync();
        var shifts = Ok(await Shifts(db, s, s.Admin).List(s.Day, s.Day.AddDays(1), default));
        OnlyIds(shifts, s.Shift.Id, s.PeerShift.Id, open.Id);
        Assert.All(shifts.EnumerateArray(), row => Assert.Equal(0, row.GetProperty("appliedCount").GetInt32()));
        OnlyIds(Ok(await Shifts(db, s, s.Admin).ListApplications(open.Id, default)));
        var roster = Roster(db, s, s.Admin);
        OnlyIds(Ok(await roster.ListShiftChanges(null, default)));
        var openList = Ok(await roster.ListOpenShifts(default));
        Assert.Equal(JsonValueKind.Null, Assert.Single(openList.EnumerateArray()).GetProperty("templateName").ValueKind);
        var proxyList = Ok(await roster.ListAuthorityProxies(default));
        var proxy = Assert.Single(proxyList.EnumerateArray());
        Assert.False(proxy.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proxy.GetProperty("delegatorName").ValueKind);
        var announcements = Ok(await roster.ListAnnouncements(default));
        Assert.Equal(JsonValueKind.Null, Assert.Single(announcements.EnumerateArray()).GetProperty("authorName").ValueKind);
        OnlyIds(Ok(await Care(db, s, s.Admin).List(null, false, default)));
        OnlyIds(Ok(await Care(db, s, s.Admin).ListHandovers(null, default)));
    }

    internal static Task ExerciseAsync(HospitalCrmDbContext db, Scenario s, string area) => area switch
    {
        "profiles-leave" => ProfilesLeaveAsync(db, s),
        "care" => CareAsync(db, s),
        "shifts" => ShiftsAsync(db, s),
        "roster-proxies" => RosterAsync(db, s),
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };

    private static async Task ProfilesLeaveAsync(HospitalCrmDbContext db, Scenario s)
    {
        var admin = Nurse(db, s, s.Admin);
        var nurse = Nurse(db, s, s.Nurse);
        OnlyIds(Ok(await admin.ListNurses(default)), s.NurseProfile.Id, s.PeerProfile.Id, s.DelegatorProfile.Id);
        OnlyIds(Ok(await nurse.ListPeers(default)), s.PeerProfile.Id, s.DelegatorProfile.Id);
        Assert.Equal(s.NurseProfile.Id, Ok(await nurse.GetMyProfile(default)).GetProperty("id").GetGuid());
        Assert.Equal(s.NurseProfile.Id, Ok(await nurse.GetNurse(s.NurseProfile.Id, default)).GetProperty("id").GetGuid());
        Assert.IsType<NotFoundObjectResult>(await nurse.GetNurse(s.PeerProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.GetNurse(s.ForeignProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.GetNurse(s.InactiveProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.GetAvailability(s.ForeignProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.ListLeaves(s.ForeignProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.ReviewLeave(s.ForeignLeave, new("Approved", null), default));
        Assert.IsType<ForbidResult>(await nurse.UpdateNurse(s.NurseProfile.Id, Update(), default));
        Assert.IsType<NotFoundObjectResult>(await admin.UpdateNurse(s.NurseProfile.Id, Update() with { DepartmentId = s.ForeignDepartment }, default));
        Assert.IsType<NotFoundObjectResult>(await admin.UpdateNurse(s.NurseProfile.Id, Update() with { SupervisorId = s.Foreign.Id }, default));
        Ok(await admin.UpdateNurse(s.NurseProfile.Id, Update() with { Designation = "B charge nurse", DepartmentId = s.Department, SupervisorId = s.Admin.Id }, default));
        Assert.Equal("B charge nurse", (await db.NurseProfiles.SingleAsync(p => p.Id == s.NurseProfile.Id)).Designation);
        var availabilityId = Ok(await nurse.SetAvailability(s.NurseProfile.Id, Availability(s), default)).GetProperty("id").GetGuid();
        OnlyIds(Ok(await nurse.GetAvailability(s.NurseProfile.Id, default)), availabilityId);
        OnlyIds(Ok(await admin.GetAvailability(s.NurseProfile.Id, default)), availabilityId);
        Assert.IsType<ForbidResult>(await nurse.SetAvailability(s.PeerProfile.Id, Availability(s), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.GetAvailability(s.PeerProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.SetAvailability(s.InactiveProfile.Id, Availability(s), default));
        var leaveId = Ok(await nurse.RequestLeave(s.NurseProfile.Id, new(s.Day, s.Day, LeaveType.Vacation, "B leave"), default)).GetProperty("id").GetGuid();
        OnlyIds(Ok(await nurse.ListLeaves(s.NurseProfile.Id, default)), leaveId);
        OnlyIds(Ok(await admin.ListAllLeaveRequests("Pending", default)), leaveId);
        Assert.IsType<ForbidResult>(await nurse.RequestLeave(s.PeerProfile.Id, new(s.Day, s.Day, LeaveType.Vacation, null), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.ListLeaves(s.PeerProfile.Id, default));
        Assert.IsType<NotFoundObjectResult>(await admin.GetLeaveBalance(s.ForeignProfile.Id, s.Day.Year, default));
        Assert.IsType<NotFoundObjectResult>(await nurse.GetLeaveBalance(s.PeerProfile.Id, s.Day.Year, default));
        Ok(await admin.ReviewLeave(leaveId, new("Approved", "B review"), default));
        Assert.Equal(1, Ok(await nurse.GetLeaveBalance(s.NurseProfile.Id, s.Day.Year, default)).GetProperty("totalDays").GetInt32());
        var released = await db.NurseShifts.SingleAsync(x => x.Id == s.Shift.Id);
        Assert.Null(released.NurseProfileId);
        Assert.Equal(ShiftStatus.Open, released.Status);
        var saved = await db.LeaveRequests.SingleAsync(x => x.Id == leaveId);
        Assert.Equal(s.B, saved.TenantId); Assert.Equal(s.Admin.Id, saved.ReviewedBy);
        Assert.Equal(s.B, (await db.NurseAvailabilities.SingleAsync(x => x.Id == availabilityId)).TenantId);
    }

    private static async Task CareAsync(HospitalCrmDbContext db, Scenario s)
    {
        var admin = Care(db, s, s.Admin);
        var nurse = Care(db, s, s.Nurse);
        var appointment = new Appointment { Id = Guid.NewGuid(), TenantId = s.B, ClinicId = s.ClinicB,
            PatientId = s.PatientB.Id, DoctorId = s.Admin.Id, Date = s.Day, TimeSlot = "10:00" };
        db.Add(appointment);
        await db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await admin.Assign(new(s.PatientA.Id, s.NurseProfile.Id, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await admin.Assign(new(s.PatientB.Id, s.ForeignProfile.Id, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await admin.Assign(new(s.PatientB.Id, s.InactiveProfile.Id, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await admin.Assign(new(s.PatientB.Id, s.NurseProfile.Id, null, s.ForeignShift.Id), default));
        Assert.IsType<NotFoundObjectResult>(await admin.Assign(new(s.PatientB.Id, s.NurseProfile.Id, Guid.NewGuid(), null), default));
        Assert.IsType<ForbidResult>(await nurse.Assign(new(s.PatientB.Id, s.NurseProfile.Id, null, null), default));
        var assigned = Ok(await admin.Assign(new(s.PatientB.Id, s.NurseProfile.Id, appointment.Id, s.Shift.Id), default)).GetProperty("assignmentId").GetGuid();
        var peerAssignment = Ok(await admin.Assign(new(s.PatientB.Id, s.PeerProfile.Id, null, null), default)).GetProperty("assignmentId").GetGuid();
        OnlyIds(Ok(await admin.List(null, true, default)), assigned, peerAssignment);
        OnlyIds(Ok(await nurse.List(null, true, default)), assigned);
        Assert.IsType<ForbidResult>(await nurse.Complete(peerAssignment, default));
        Assert.IsType<NotFoundObjectResult>(await nurse.Complete(Guid.NewGuid(), default));
        Assert.IsType<NotFoundObjectResult>(await admin.Complete(s.ForeignAssignment, default));
        Assert.IsType<NotFoundObjectResult>(await nurse.CreateHandover(new(s.PatientA.Id, s.PeerProfile.Id, "Denied", null, null, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.CreateHandover(new(s.PatientB.Id, s.InactiveProfile.Id, "Denied", null, null, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.CreateHandover(new(s.PatientB.Id, s.ForeignProfile.Id, "Denied", null, null, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.CreateHandover(new(s.PatientB.Id, s.PeerProfile.Id, "Denied", null, null, null, s.ForeignShift.Id), default));
        var handover = Ok(await nurse.CreateHandover(new(s.PatientB.Id, s.PeerProfile.Id, "B handover", null, "B pending", null, s.Shift.Id), default)).GetProperty("handoverId").GetGuid();
        OnlyIds(Ok(await nurse.ListHandovers(s.PatientB.Id, default)), handover);
        OnlyIds(Ok(await admin.ListHandovers(null, default)), handover);
        Assert.IsType<NotFoundObjectResult>(await nurse.ListHandovers(s.PatientA.Id, default));
        Ok(await nurse.Complete(assigned, default));
        OnlyIds(Ok(await nurse.List(null, true, default)));
        Assert.Equal(s.B, (await db.PatientNurseAssignments.SingleAsync(x => x.Id == assigned)).TenantId);
        Assert.Equal(s.B, (await db.ShiftHandovers.SingleAsync(x => x.Id == handover)).TenantId);
        Assert.All(await db.NurseAuditLogs.Where(x => x.TenantId == s.B).ToListAsync(), x => Assert.Contains(x.ChangedBy, new[] { s.Admin.Id, s.Nurse.Id }));
        var savedAssignment = await db.PatientNurseAssignments.SingleAsync(x => x.Id == assigned);
        var completedAt = savedAssignment.CompletedAt;
        var auditCount = await db.NurseAuditLogs.CountAsync(x => x.TenantId == s.B);
        appointment.ClinicId = s.ClinicA;
        await db.SaveChangesAsync();
        OnlyIds(Ok(await nurse.List(null, false, default)));
        Assert.IsType<NotFoundObjectResult>(await nurse.Complete(assigned, default));
        appointment.ClinicId = s.ClinicB;
        savedAssignment.ShiftId = s.ForeignShift.Id;
        (await db.ShiftHandovers.SingleAsync(x => x.Id == handover)).ShiftId = s.ForeignShift.Id;
        await db.SaveChangesAsync();
        OnlyIds(Ok(await nurse.List(null, false, default)));
        OnlyIds(Ok(await nurse.ListHandovers(null, default)));
        Assert.IsType<NotFoundObjectResult>(await nurse.Complete(assigned, default));
        Assert.Equal(completedAt, savedAssignment.CompletedAt);
        Assert.Equal(auditCount, await db.NurseAuditLogs.CountAsync(x => x.TenantId == s.B));
    }

    private static async Task ShiftsAsync(HospitalCrmDbContext db, Scenario s)
    {
        var admin = Shifts(db, s, s.Admin);
        var nurse = Shifts(db, s, s.Nurse);
        Assert.IsType<ConflictObjectResult>(await admin.Create(new(s.Day, ShiftType.Night, s.ForeignProfile.Id, null), default));
        Assert.IsType<ConflictObjectResult>(await admin.Create(new(s.Day, ShiftType.Night, s.InactiveProfile.Id, null), default));
        Assert.IsType<ForbidResult>(await nurse.Create(new(s.Day, ShiftType.Night, null, null), default));
        Assert.IsType<NotFoundObjectResult>(await admin.Update(s.ForeignShift.Id, new(null, true, null), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.Apply(s.ForeignShift.Id, default));
        Assert.IsType<NotFoundObjectResult>(await nurse.Withdraw(s.ForeignShift.Id, default));
        OnlyIds(Ok(await admin.ListApplications(s.ForeignShift.Id, default)));
        var shiftId = Ok(await admin.Create(new(s.Day, ShiftType.Night, null, "B open"), default)).GetProperty("shiftId").GetGuid();
        OnlyIds(Ok(await admin.List(s.Day, s.Day.AddDays(1), default)), s.Shift.Id, s.PeerShift.Id, shiftId);
        OnlyIds(Ok(await nurse.List(s.Day, s.Day.AddDays(1), default)), s.Shift.Id, shiftId);
        var appId = Ok(await nurse.Apply(shiftId, default)).GetProperty("applicationId").GetGuid();
        Ok(await nurse.Withdraw(shiftId, default));
        Assert.Equal(appId, Ok(await nurse.Apply(shiftId, default)).GetProperty("applicationId").GetGuid());
        OnlyIds(Ok(await admin.ListApplications(shiftId, default)), appId);
        Assert.IsType<ForbidResult>(await nurse.Select(shiftId, appId, default));
        Assert.IsType<NotFoundObjectResult>(await admin.Select(s.ForeignShift.Id, appId, default));
        Ok(await admin.Select(shiftId, appId, default));
        var selected = await db.NurseShifts.SingleAsync(x => x.Id == shiftId);
        Assert.Equal(s.B, selected.TenantId); Assert.Equal(s.ClinicB, selected.ClinicId); Assert.Equal(s.NurseProfile.Id, selected.NurseProfileId);
        Assert.IsType<ConflictObjectResult>(await admin.Update(shiftId, new(s.InactiveProfile.Id, null, null), default));
        Ok(await admin.Update(shiftId, new(null, true, null), default));
        Assert.Equal(ShiftStatus.Cancelled, selected.Status);
        Assert.Equal(ShiftApplicationStatus.Selected, (await db.NurseShiftApplications.SingleAsync(x => x.Id == appId)).Status);
    }

    private static async Task RosterAsync(HospitalCrmDbContext db, Scenario s)
    {
        var admin = Roster(db, s, s.Admin);
        var nurse = Roster(db, s, s.Nurse);
        var peer = Roster(db, s, s.Peer);
        var acting = Roster(db, s, s.Delegator);
        var template = Assert.IsType<ShiftTemplate>(Assert.IsType<OkObjectResult>(await admin.CreateTemplate(new("B day", "08:00", "16:00", false, 8, 2, null), default)).Value);
        Assert.Equal(s.B, template.TenantId); Assert.Equal(s.ClinicB, template.ClinicId);
        Assert.Equal(template.Id, Assert.Single(Assert.IsAssignableFrom<IEnumerable<ShiftTemplate>>(Assert.IsType<OkObjectResult>(await nurse.ListTemplates(default)).Value)).Id);
        Assert.IsType<ForbidResult>(await nurse.CreateTemplate(new("Denied", "08:00", "16:00", false, 8, 2, null), default));
        var roster = Assert.IsType<StaffRoster>(Assert.IsType<OkObjectResult>(await admin.CreateRoster(new("B roster", s.Day, s.Day.AddDays(2)), default)).Value);
        Assert.Equal(s.B, roster.TenantId); Assert.Equal(s.ClinicB, roster.ClinicId);
        OnlyIds(Ok(await nurse.ListRosters(default)), roster.Id);
        Assert.IsType<NotFoundObjectResult>(await admin.PublishRoster(Guid.NewGuid(), default));
        Assert.IsType<NotFoundObjectResult>(await admin.PublishRoster(s.ForeignRoster, default));
        Assert.IsType<NotFoundObjectResult>(await admin.LockRoster(s.ForeignRoster, default));
        Assert.IsType<NotFoundObjectResult>(await admin.RevokeAuthorityProxy(s.ForeignProxy, default));
        Assert.IsType<NotFoundObjectResult>(await nurse.ClaimOpenShift(s.ForeignShift.Id, default));
        Assert.IsType<NotFoundObjectResult>(await nurse.RequestShiftChange(new(ShiftChangeType.DirectSwap, s.Shift.Id,
            s.ForeignProfile.Id, s.ForeignShift.Id, null), default));
        var swapId = Ok(await nurse.RequestShiftChange(new(ShiftChangeType.DirectSwap, s.Shift.Id, s.PeerProfile.Id, s.PeerShift.Id, "B swap"), default)).GetProperty("id").GetGuid();
        OnlyIds(Ok(await admin.ListShiftChanges("Requested", default)), swapId);
        Assert.IsType<ForbidResult>(await acting.AcceptShiftChange(swapId, default));
        Ok(await peer.AcceptShiftChange(swapId, default));
        Assert.IsType<ForbidResult>(await acting.ApproveShiftChange(swapId, null, default));
        Assert.IsType<BadRequestObjectResult>(await nurse.ApproveShiftChange(swapId, null, default));
        var now = DateTimeOffset.UtcNow;
        var proxyRequest = new CreateAuthorityProxyRequest(s.Admin.Id, s.Delegator.Id, "roster.approve,leave.approve", now.AddMinutes(-1), now.AddHours(1), null);
        Assert.IsType<ForbidResult>(await nurse.CreateAuthorityProxy(proxyRequest, default));
        Assert.IsType<BadRequestObjectResult>(await admin.CreateAuthorityProxy(proxyRequest with { DesigneeUserId = s.Foreign.Id }, default));
        Assert.IsType<BadRequestObjectResult>(await admin.CreateAuthorityProxy(proxyRequest with { DesigneeUserId = s.Inactive.Id }, default));
        Assert.IsType<BadRequestObjectResult>(await admin.CreateAuthorityProxy(proxyRequest with { DesigneeUserId = s.Admin.Id }, default));
        Assert.IsType<BadRequestObjectResult>(await admin.CreateAuthorityProxy(proxyRequest with { Permissions = "roster.approve.all" }, default));
        var proxy = Assert.IsType<AuthorityProxy>(Assert.IsType<OkObjectResult>(await admin.CreateAuthorityProxy(proxyRequest, default)).Value);
        Assert.Equal(s.B, proxy.TenantId); Assert.Equal(s.ClinicB, proxy.ClinicId);
        OnlyIds(Ok(await acting.ListAuthorityProxies(default)), proxy.Id);
        var approved = Ok(await acting.ApproveShiftChange(swapId, null, default));
        Assert.Equal(s.Admin.Id, approved.GetProperty("approvedOnBehalfOf").GetGuid());
        Assert.Equal(s.PeerProfile.Id, (await db.NurseShifts.SingleAsync(x => x.Id == s.Shift.Id)).NurseProfileId);
        Assert.Equal(s.NurseProfile.Id, (await db.NurseShifts.SingleAsync(x => x.Id == s.PeerShift.Id)).NurseProfileId);
        var leaveId = Ok(await Nurse(db, s, s.Nurse).RequestLeave(s.NurseProfile.Id,
            new(s.Day.AddDays(5), s.Day.AddDays(5), LeaveType.Vacation, null), default)).GetProperty("id").GetGuid();
        Ok(await Nurse(db, s, s.Delegator).ReviewLeave(leaveId, new("Approved", null), default));
        Assert.Equal(s.Admin.Id, (await db.LeaveRequests.SingleAsync(x => x.Id == leaveId)).ApprovedOnBehalfOfUserId);
        Assert.IsType<ForbidResult>(await acting.RevokeAuthorityProxy(proxy.Id, default));
        Ok(await admin.RevokeAuthorityProxy(proxy.Id, default));
        OnlyIds(Ok(await acting.ListAuthorityProxies(default)));
        Assert.IsType<NotFoundObjectResult>(await admin.RevokeAuthorityProxy(Guid.NewGuid(), default));
        Assert.IsType<NotFoundObjectResult>(await nurse.CreateAnnouncement(new(s.ForeignTeam, "Denied", "Denied", "normal"), default));
        var announcement = Assert.IsType<StaffAnnouncement>(Assert.IsType<OkObjectResult>(await nurse.CreateAnnouncement(new(s.Team, "B bulletin", "B content", "normal"), default)).Value);
        OnlyIds(Ok(await admin.ListAnnouncements(default)), announcement.Id);
        Assert.Equal(s.B, announcement.TenantId); Assert.Equal(s.ClinicB, announcement.ClinicId);
        Assert.IsType<NotFoundObjectResult>(await nurse.CheckLeaveImpact(new(s.PeerProfile.Id, s.Day, s.Day), default));
        Ok(await nurse.CheckLeaveImpact(new(s.NurseProfile.Id, s.Day, s.Day.AddDays(1)), default));
        var openId = Ok(await Shifts(db, s, s.Admin).Create(new(s.Day.AddDays(2), ShiftType.Night, null, null), default)).GetProperty("shiftId").GetGuid();
        OnlyIds(Ok(await nurse.ListOpenShifts(default)), openId);
        var claimId = Ok(await nurse.ClaimOpenShift(openId, default)).GetProperty("id").GetGuid();
        Ok(await admin.ApproveShiftChange(claimId, null, default));
        Assert.Equal(s.NurseProfile.Id, (await db.NurseShifts.SingleAsync(x => x.Id == openId)).NurseProfileId);
        Ok(await admin.PublishRoster(roster.Id, default));
        Ok(await admin.LockRoster(roster.Id, default));
        Assert.All(await db.NurseShifts.Where(x => x.TenantId == s.B).ToListAsync(), x => Assert.True(x.IsLocked));
        Assert.IsType<ConflictObjectResult>(await Shifts(db, s, s.Admin).Update(openId, new(null, true, null), default));
    }

    internal sealed class Scenario
    {
        internal Guid A = Guid.NewGuid(), B = Guid.NewGuid(), ClinicA = Guid.NewGuid(), ClinicB = Guid.NewGuid();
        internal DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        internal User Admin = new(), Nurse = new(), Peer = new(), Delegator = new(), Foreign = new(), Inactive = new();
        internal NurseProfile NurseProfile = new(), PeerProfile = new(), DelegatorProfile = new(), ForeignProfile = new(), InactiveProfile = new();
        internal Patient PatientA = new(), PatientB = new();
        internal NurseShift Shift = new(), PeerShift = new(), ForeignShift = new();
        internal Guid Department = Guid.NewGuid(), ForeignDepartment = Guid.NewGuid(), Team = Guid.NewGuid(), ForeignTeam = Guid.NewGuid();
        internal Guid ForeignRoster = Guid.NewGuid(), ForeignProxy = Guid.NewGuid(), ForeignAssignment = Guid.NewGuid(), ForeignLeave = Guid.NewGuid();
    }

    internal static async Task<Scenario> SeedAsync(HospitalCrmDbContext db)
    {
        var s = new Scenario();
        db.AddRange(new Tenant { Id = s.A }, new Tenant { Id = s.B },
            new Clinic { Id = s.ClinicA, TenantId = s.A }, new Clinic { Id = s.ClinicB, TenantId = s.B });
        var users = new[] { s.Admin, s.Nurse, s.Peer, s.Delegator, s.Foreign, s.Inactive };
        foreach (var user in users)
        {
            user.Id = Guid.NewGuid(); user.TenantId = s.A; user.ClinicId = s.ClinicA;
            user.Role = user == s.Nurse ? UserRole.ClinicAdmin : user == s.Foreign ? UserRole.Nurse : UserRole.Doctor;
            user.Name = user == s.Foreign ? "FOREIGN SECRET" : "Synthetic " + user.Id;
            user.Email = user.Id + "@synthetic.invalid"; user.PasswordHash = "unused";
            db.Add(user);
            db.Add(new TenantMembership { UserId = user.Id, TenantId = s.A, ClinicId = s.ClinicA, Role = user.Role });
            if (user != s.Foreign) db.Add(new TenantMembership { UserId = user.Id, TenantId = s.B, ClinicId = s.ClinicB,
                Role = user == s.Admin ? UserRole.ClinicAdmin : UserRole.Nurse, Status = user == s.Inactive ? "suspended" : "active" });
        }
        foreach (var (profile, user) in new[] { (s.NurseProfile, s.Nurse), (s.PeerProfile, s.Peer),
                     (s.DelegatorProfile, s.Delegator), (s.ForeignProfile, s.Foreign), (s.InactiveProfile, s.Inactive) })
        {
            profile.Id = Guid.NewGuid(); profile.UserId = user.Id; profile.TenantId = user == s.Foreign ? s.A : s.B;
            db.Add(profile);
        }
        foreach (var (patient, tenant) in new[] { (s.PatientA, s.A), (s.PatientB, s.B) })
        {
            patient.Id = Guid.NewGuid(); patient.TenantId = tenant; patient.Name = "Synthetic patient";
            patient.Uhid = patient.Id.ToString("N"); patient.Phone = "9000000001"; patient.CreatedBy = s.Admin.Id;
            db.Add(patient);
        }
        foreach (var (shift, profile, date) in new[] { (s.Shift, s.NurseProfile, s.Day),
                     (s.PeerShift, s.PeerProfile, s.Day.AddDays(1)), (s.ForeignShift, s.ForeignProfile, s.Day) })
        {
            shift.Id = Guid.NewGuid(); shift.TenantId = profile.TenantId; shift.ClinicId = profile == s.ForeignProfile ? s.ClinicA : s.ClinicB;
            shift.NurseProfileId = profile.Id; shift.Date = date; shift.ShiftType = ShiftType.Morning; shift.Status = ShiftStatus.Assigned;
            shift.CreatedBy = s.Admin.Id; db.Add(shift);
        }
        db.AddRange(new Department { Id = s.Department, TenantId = s.B, ClinicId = s.ClinicB },
            new Department { Id = s.ForeignDepartment, TenantId = s.A, ClinicId = s.ClinicA },
            new StaffTeam { Id = s.Team, TenantId = s.B, ClinicId = s.ClinicB },
            new StaffTeam { Id = s.ForeignTeam, TenantId = s.A, ClinicId = s.ClinicA });
        db.AddRange(
            new StaffRoster { Id = s.ForeignRoster, TenantId = s.A, ClinicId = s.ClinicA, StartDate = s.Day, EndDate = s.Day, Title = "FOREIGN SECRET" },
            new ShiftTemplate { TenantId = s.A, ClinicId = s.ClinicA, Name = "FOREIGN SECRET", IsActive = true },
            new AuthorityProxy { Id = s.ForeignProxy, TenantId = s.A, ClinicId = s.ClinicA, DelegatorUserId = s.Admin.Id,
                DesigneeUserId = s.Foreign.Id, Permissions = "roster.approve,leave.approve", ValidFrom = DateTimeOffset.UtcNow.AddHours(-1), ValidUntil = DateTimeOffset.UtcNow.AddHours(1) },
            new StaffAnnouncement { TenantId = s.A, ClinicId = s.ClinicA, Title = "FOREIGN SECRET", Content = "FOREIGN SECRET", AuthorUserId = s.Foreign.Id },
            new PatientNurseAssignment { Id = s.ForeignAssignment, TenantId = s.A, PatientId = s.PatientA.Id, NurseProfileId = s.ForeignProfile.Id,
                AssignedBy = s.Admin.Id, Status = NurseAssignmentStatus.Active },
            new ShiftHandover { Id = Guid.NewGuid(), TenantId = s.A, PatientId = s.PatientA.Id, AuthorNurseProfileId = s.ForeignProfile.Id,
                ToNurseProfileId = s.ForeignProfile.Id, WhatHappened = "FOREIGN SECRET", WhatIsPending = "FOREIGN SECRET" },
            new LeaveRequest { Id = s.ForeignLeave, TenantId = s.A, NurseProfileId = s.ForeignProfile.Id, StartDate = s.Day, EndDate = s.Day,
                Status = LeaveStatus.Pending, Type = LeaveType.Sick },
            new NurseAvailability { Id = Guid.NewGuid(), TenantId = s.A, NurseProfileId = s.ForeignProfile.Id, StartDate = s.Day, EndDate = s.Day });
        await db.SaveChangesAsync();
        return s;
    }

    internal static async Task AssertPrimaryUnchangedAsync(HospitalCrmDbContext db, Scenario s)
    {
        foreach (var original in new[] { s.Admin, s.Nurse, s.Peer, s.Delegator, s.Foreign, s.Inactive })
        {
            var current = await db.Users.AsNoTracking().SingleAsync(u => u.Id == original.Id);
            Assert.Equal(s.A, current.TenantId); Assert.Equal(s.ClinicA, current.ClinicId);
            Assert.Equal(original == s.Nurse ? UserRole.ClinicAdmin : original == s.Foreign ? UserRole.Nurse : UserRole.Doctor, current.Role);
        }
        var assignment = await db.PatientNurseAssignments.AsNoTracking().SingleAsync(x => x.Id == s.ForeignAssignment);
        Assert.Equal(NurseAssignmentStatus.Active, assignment.Status);
        Assert.Null(assignment.CompletedAt);
        var leave = await db.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == s.ForeignLeave);
        Assert.Equal(LeaveStatus.Pending, leave.Status);
        Assert.Null(leave.ReviewedBy);
        Assert.False((await db.AuthorityProxies.AsNoTracking().SingleAsync(x => x.Id == s.ForeignProxy)).IsRevoked);
        var roster = await db.StaffRosters.AsNoTracking().SingleAsync(x => x.Id == s.ForeignRoster);
        Assert.Equal(RosterStatus.Draft, roster.Status);
        Assert.Null(roster.PublishedAt);
        Assert.Null(roster.LockedAt);
        var shift = await db.NurseShifts.AsNoTracking().SingleAsync(x => x.Id == s.ForeignShift.Id);
        Assert.Equal(s.ForeignProfile.Id, shift.NurseProfileId);
        Assert.False(shift.IsLocked);
        Assert.Null(shift.RosterId);
    }

    internal static ClaimsPrincipal Principal(User user, Scenario s) => new(new ClaimsIdentity([
        new Claim("sub", user.Id.ToString()), new Claim("tenant_id", s.B.ToString()), new Claim("clinicId", s.ClinicB.ToString()),
        new Claim("role", user == s.Admin ? "ClinicAdmin" : "Nurse")], "Synthetic", "sub", "role"));
    private static T Caller<T>(T controller, Scenario s, User user) where T : ControllerBase
    {
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = Principal(user, s) } };
        return controller;
    }
    internal static NurseController Nurse(HospitalCrmDbContext db, Scenario s, User user) => Caller(new NurseController(db, NullLogger<NurseController>.Instance), s, user);
    internal static NurseCareController Care(HospitalCrmDbContext db, Scenario s, User user) => Caller(new NurseCareController(db, NullLogger<NurseCareController>.Instance), s, user);
    internal static ShiftsController Shifts(HospitalCrmDbContext db, Scenario s, User user) => Caller(new ShiftsController(db, NullLogger<ShiftsController>.Instance), s, user);
    internal static StaffRosterController Roster(HospitalCrmDbContext db, Scenario s, User user) => Caller(new StaffRosterController(db, NullLogger<StaffRosterController>.Instance), s, user);
    private static HospitalCrmDbContext MemoryDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, TestMaintenanceTenantContext.Instance);
    private static SetAvailabilityRequest Availability(Scenario s) => new(s.Day, s.Day, ShiftPreference.Any, null, true, true, false, "B only");
    private static UpdateNurseRequest Update() => JsonSerializer.Deserialize<UpdateNurseRequest>("{}")!;
    internal static JsonElement Ok(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    private static void OnlyIds(JsonElement result, params Guid[] expected) =>
        Assert.Equal(expected.Order(), result.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Order());
}
