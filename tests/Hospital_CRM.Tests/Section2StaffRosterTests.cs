using System.Security.Claims;
using System.Reflection;
using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section2StaffRosterTests
{
    private HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static JsonElement ToJson(object? value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task Core_Vitals_CanBeRecorded_WithoutRosterModule_AndTransitionsStatusToInNursing()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var nurseUserId = Guid.NewGuid();

        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Vitals Patient", Phone = "9988776655" };
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            DoctorId = Guid.NewGuid(),
            PatientId = patient.Id,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            TimeSlot = "10:30",
            Status = AppointmentStatus.CheckedIn // Checked-in in lobby
        };

        var nurseUser = new User
        {
            Id = nurseUserId,
            TenantId = tenantId,
            ClinicId = clinicId,
            Email = "nurse@clinic.com",
            Role = UserRole.Nurse,
            Name = "Nurse Joy"
        };
        db.Users.Add(nurseUser);
        db.Patients.Add(patient);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var controller = new VitalsController(db, NullLogger<VitalsController>.Instance);
        var claims = new[]
        {
            new Claim("sub", nurseUserId.ToString()),
            new Claim("role", "Nurse"),
            new Claim("tenant_id", tenantId.ToString())
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
        };

        var req = new RecordVitalsRequest(
            SystolicBp: 124,
            DiastolicBp: 82,
            HeartRate: 76,
            Temperature: 98.6m,
            RespiratoryRate: 16,
            SpO2: 99,
            WeightKg: 70.0m,
            HeightCm: 175.0m, // BMI ~ 22.9
            BloodGlucoseMgDl: 95.0m,
            Allergies: "Penicillin",
            BedsideObservations: "Patient alert, oriented, no acute distress."
        );

        var result = await controller.RecordVitals(appointment.Id, req, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);
        var val = ToJson(result.Value);

        Assert.Equal(124, val.GetProperty("systolicBp").GetInt32());
        Assert.Equal(82, val.GetProperty("diastolicBp").GetInt32());
        Assert.Equal(99, val.GetProperty("spO2").GetInt32());
        Assert.Equal(22.9m, val.GetProperty("bmi").GetDecimal());
        Assert.Equal("InNursing", val.GetProperty("appointmentStatus").GetString());

        // Verify appointment in DB transitioned to InNursing
        var updatedApt = await db.Appointments.FindAsync(appointment.Id);
        Assert.NotNull(updatedApt);
        Assert.Equal(AppointmentStatus.InNursing, updatedApt.Status);

        // Fetch vitals endpoint
        var getResult = await controller.GetAppointmentVitals(appointment.Id, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(getResult);
        var getVal = ToJson(getResult.Value);
        Assert.Equal("Penicillin", getVal.GetProperty("allergies").GetString());
    }

    [Fact]
    public async Task StaffRoster_ModuleGate_BlocksWhenModuleNotEntitled()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        // Tenant does NOT have "staff_roster" in ActivatedModules
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Lean Clinic",
            ActivatedModules = "core",
            SubscriptionTier = SubscriptionTier.Starter,
            SubscriptionStatus = SubscriptionStatus.Active
        };
        var clinic = new Clinic { Id = clinicId, TenantId = tenantId, Name = "Main Branch" };
        var admin = new User { Id = adminId, TenantId = tenantId, ClinicId = clinicId, Email = "admin@clinic.com", Role = UserRole.ClinicAdmin, Name = "Admin" };

        db.Tenants.Add(tenant);
        db.Clinics.Add(clinic);
        db.Users.Add(admin);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        var claims = new[]
        {
            new Claim("sub", adminId.ToString()),
            new Claim("role", "ClinicAdmin"),
            new Claim("clinicId", clinicId.ToString()),
            new Claim("tenant_id", tenantId.ToString())
        };
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) };
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IModuleGateService, ModuleGateService>()
            .BuildServiceProvider();
        httpContext.RequestServices = services;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller);
        var executed = false;

        var attr = new RequireModuleAttribute(ModuleRegistry.StaffRoster);
        await attr.OnActionExecutionAsync(filterContext, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], controller));
        });

        Assert.False(executed);
        var forbiddenResult = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(403, forbiddenResult.StatusCode);
        var forbiddenJson = JsonSerializer.Serialize(forbiddenResult.Value);
        Assert.Contains("module_not_activated", forbiddenJson);
    }

    [Fact]
    public async Task RosterLifecycle_Draft_To_Published_To_Locked()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var clinic = new Clinic { Id = clinicId, TenantId = tenantId, Name = "Roster Hospital" };
        var admin = new User { Id = adminId, TenantId = tenantId, ClinicId = clinicId, Email = "admin@hospital.com", Role = UserRole.ClinicAdmin, Name = "Admin" };
        db.Clinics.Add(clinic);
        db.Users.Add(admin);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        var claims = new[]
        {
            new Claim("sub", adminId.ToString()),
            new Claim("role", "ClinicAdmin"),
            new Claim("clinicId", clinicId.ToString()),
            new Claim("tenant_id", tenantId.ToString())
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
        };

        // 1. Create Draft
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var resDraft = await controller.CreateRoster(new CreateRosterRequest("Next Week Roster", today, today.AddDays(6)), CancellationToken.None) as OkObjectResult;
        Assert.NotNull(resDraft);
        var roster = resDraft.Value as StaffRoster;
        Assert.NotNull(roster);
        Assert.Equal(RosterStatus.Draft, roster.Status);

        // 2. Publish Roster
        var resPublish = await controller.PublishRoster(roster.Id, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(resPublish);
        Assert.Equal("Published", ToJson(resPublish.Value).GetProperty("status").GetString());

        // 3. Lock Roster
        var resLock = await controller.LockRoster(roster.Id, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(resLock);
        Assert.Equal("Locked", ToJson(resLock.Value).GetProperty("status").GetString());

        var lockedRoster = await db.StaffRosters.FindAsync(roster.Id);
        Assert.NotNull(lockedRoster);
        Assert.Equal(RosterStatus.Locked, lockedRoster.Status);
    }

    [Fact]
    public async Task ShiftSwap_Workflow_DirectSwap_AtomicExecution()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();

        var nurseUserA = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "nurseA@test.com", Role = UserRole.Nurse, Name = "Nurse A" };
        var nurseUserB = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "nurseB@test.com", Role = UserRole.Nurse, Name = "Nurse B" };
        var adminUser = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "headnurse@test.com", Role = UserRole.ClinicAdmin, Name = "Head Nurse" };

        var nurseProfileA = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = nurseUserA.Id, Designation = "Staff Nurse" };
        var nurseProfileB = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = nurseUserB.Id, Designation = "Staff Nurse" };

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var shiftA = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = today, ShiftType = ShiftType.Morning, Status = ShiftStatus.Assigned, NurseProfileId = nurseProfileA.Id };
        var shiftB = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = today.AddDays(1), ShiftType = ShiftType.Evening, Status = ShiftStatus.Assigned, NurseProfileId = nurseProfileB.Id };

        db.Users.AddRange(nurseUserA, nurseUserB, adminUser);
        db.NurseProfiles.AddRange(nurseProfileA, nurseProfileB);
        db.NurseShifts.AddRange(shiftA, shiftB);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);

        // Nurse A requests direct swap with Nurse B
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", nurseUserA.Id.ToString()),
                new Claim("role", "Nurse"),
                new Claim("clinicId", clinicId.ToString()),
                new Claim("tenant_id", tenantId.ToString())
            ], "TestAuth")) }
        };

        var reqSwap = new RequestShiftChangeRequest(
            RequestType: ShiftChangeType.DirectSwap,
            RequesterShiftId: shiftA.Id,
            TargetNurseProfileId: nurseProfileB.Id,
            TargetShiftId: shiftB.Id,
            Reason: "Family function"
        );
        var resReq = await controller.RequestShiftChange(reqSwap, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(resReq);
        var swapId = ToJson(resReq.Value).GetProperty("id").GetGuid();

        // Nurse B accepts swap
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", nurseUserB.Id.ToString()),
                new Claim("role", "Nurse"),
                new Claim("clinicId", clinicId.ToString()),
                new Claim("tenant_id", tenantId.ToString())
            ], "TestAuth")) }
        };
        var resAccept = await controller.AcceptShiftChange(swapId, CancellationToken.None) as OkObjectResult;
        Assert.NotNull(resAccept);
        Assert.Equal("Accepted", ToJson(resAccept.Value).GetProperty("status").GetString());

        // Head Nurse / Admin approves swap -> Atomic apply
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", adminUser.Id.ToString()),
                new Claim("role", "ClinicAdmin"),
                new Claim("clinicId", clinicId.ToString()),
                new Claim("tenant_id", tenantId.ToString())
            ], "TestAuth")) }
        };
        var resApprove = await controller.ApproveShiftChange(swapId, new ApproveShiftChangeRequest("Approved"), CancellationToken.None) as OkObjectResult;
        Assert.NotNull(resApprove);
        Assert.Equal("Applied", ToJson(resApprove.Value).GetProperty("status").GetString());

        // Verify in DB that Shift A now belongs to Nurse B, and Shift B belongs to Nurse A!
        var updatedShiftA = await db.NurseShifts.FindAsync(shiftA.Id);
        var updatedShiftB = await db.NurseShifts.FindAsync(shiftB.Id);
        Assert.Equal(nurseProfileB.Id, updatedShiftA?.NurseProfileId);
        Assert.Equal(nurseProfileA.Id, updatedShiftB?.NurseProfileId);
    }

    [Fact]
    public async Task AuthorityProxy_EnforcesTimeBox_AndPreventsSelfApproval()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();

        var delegator = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "headnurse@clinic.com", Role = UserRole.ClinicAdmin, Name = "Head Nurse" };
        var designee = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "acting@clinic.com", Role = UserRole.Nurse, Name = "Charge Nurse Acting" };
        var designeeProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = designee.Id, Designation = "Charge Nurse" };

        db.Users.AddRange(delegator, designee);
        db.NurseProfiles.Add(designeeProfile);

        // Create time-boxed authority proxy delegation
        var proxy = new AuthorityProxy
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            DelegatorUserId = delegator.Id,
            DesigneeUserId = designee.Id,
            Permissions = "roster.approve,leave.approve",
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        db.AuthorityProxies.Add(proxy);

        // Shift owned by requester
        var reqShift = new NurseShift
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            ShiftType = ShiftType.Morning,
            Status = ShiftStatus.Assigned,
            NurseProfileId = designeeProfile.Id
        };
        db.NurseShifts.Add(reqShift);

        // Dummy swap where designee is the requester
        var swap = new ShiftChangeRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            RequestType = ShiftChangeType.OpenCover,
            Status = ShiftChangeStatus.Accepted,
            RequesterNurseProfileId = designeeProfile.Id,
            RequesterShiftId = reqShift.Id,
            TargetNurseProfileId = Guid.NewGuid()
        };
        db.ShiftChangeRequests.Add(swap);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", designee.Id.ToString()),
                new Claim("role", "Nurse"),
                new Claim("tenant_id", tenantId.ToString()),
                new Claim("clinicId", clinicId.ToString())
            ], "TestAuth")) }
        };

        // Designee attempting to approve their own request MUST be rejected
        var rawRes = await controller.ApproveShiftChange(swap.Id, null, CancellationToken.None);
        var res = rawRes as BadRequestObjectResult;
        Assert.True(res != null, $"Expected BadRequestObjectResult but got {rawRes?.GetType().Name}");
        Assert.Equal("cannot_approve_own_swap_request", ToJson(res.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LeaveApproval_ReleasesConflictingShifts_ToOpenShiftBoard()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var adminUser = new User { Id = adminId, TenantId = tenantId, ClinicId = clinicId, Email = "admin@clinic.com", Role = UserRole.ClinicAdmin, Name = "Admin" };
        var nurseUser = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "nurse@clinic.com", Role = UserRole.Nurse, Name = "Nurse On Leave" };
        var nurseProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = nurseUser.Id, Designation = "Staff Nurse" };

        var leaveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var assignedShift = new NurseShift
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            Date = leaveDate,
            ShiftType = ShiftType.Night,
            Status = ShiftStatus.Assigned,
            NurseProfileId = nurseProfile.Id
        };

        var leave = new LeaveRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NurseProfileId = nurseProfile.Id,
            StartDate = leaveDate,
            EndDate = leaveDate,
            Type = LeaveType.Sick,
            Status = LeaveStatus.Pending,
            Reason = "Medical procedure",
            CertificateSeen = true
        };

        db.Users.AddRange(adminUser, nurseUser);
        db.NurseProfiles.Add(nurseProfile);
        db.NurseShifts.Add(assignedShift);
        db.LeaveRequests.Add(leave);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var nurseController = new NurseController(db, NullLogger<NurseController>.Instance);
        nurseController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", adminId.ToString()),
                new Claim("role", "ClinicAdmin"),
                new Claim("clinicId", clinicId.ToString()),
                new Claim("tenant_id", tenantId.ToString())
            ], "TestAuth")) }
        };

        var result = await nurseController.ReviewLeave(leave.Id, new ReviewLeaveRequest("Approved", "Approved", CertificateSeen: true), CancellationToken.None) as OkObjectResult;
        Assert.NotNull(result);
        var val = ToJson(result.Value);
        Assert.True(val.GetProperty("hasRosterConflict").GetBoolean());
        Assert.True(val.GetProperty("coverageGapAlertCreated").GetBoolean());

        // Conflicting shift should now be released back to Open on the open-shift board with no assigned nurse!
        var shiftAfter = await db.NurseShifts.FindAsync(assignedShift.Id);
        Assert.NotNull(shiftAfter);
        Assert.Null(shiftAfter.NurseProfileId);
        Assert.Equal(ShiftStatus.Open, shiftAfter.Status);
        Assert.Contains("Coverage needed: Nurse on approved leave", shiftAfter.Notes);
    }

    private static void SeedMemberships(HospitalCrmDbContext db, Guid tenantId, Guid clinicId)
    {
        if (!db.Clinics.Local.Any(c => c.Id == clinicId))
            db.Clinics.Add(new Clinic { Id = clinicId, TenantId = tenantId });
        foreach (var user in db.Users.Local.Where(u => u.ClinicId == clinicId).ToArray())
            db.TenantMemberships.Add(new TenantMembership { UserId = user.Id, TenantId = tenantId, ClinicId = clinicId, Role = user.Role });
    }

    private static void SetCaller(ControllerBase controller, Guid userId, string role, Guid tenantId, Guid? clinicId = null)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("role", role),
            new("tenant_id", tenantId.ToString())
        };
        if (clinicId.HasValue) claims.Add(new Claim("clinicId", clinicId.Value.ToString()));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [Fact]
    public void Roster_WritesAreModuleGated_ReadsAreNot()
    {
        var type = typeof(StaffRosterController);

        Assert.Null(type.GetCustomAttribute<RequireModuleAttribute>());
        Assert.Equal("api/v1/staff/roster", type.GetCustomAttribute<RouteAttribute>()?.Template);

        Assert.NotNull(type.GetMethod(nameof(StaffRosterController.CreateRoster))!.GetCustomAttribute<RequireModuleAttribute>());
        Assert.NotNull(type.GetMethod(nameof(StaffRosterController.ApproveShiftChange))!.GetCustomAttribute<RequireModuleAttribute>());
        Assert.NotNull(type.GetMethod(nameof(StaffRosterController.CreateAuthorityProxy))!.GetCustomAttribute<RequireModuleAttribute>());

        Assert.Null(type.GetMethod(nameof(StaffRosterController.ListRosters))!.GetCustomAttribute<RequireModuleAttribute>());
        Assert.Null(type.GetMethod(nameof(StaffRosterController.ListOpenShifts))!.GetCustomAttribute<RequireModuleAttribute>());
        Assert.Null(type.GetMethod(nameof(StaffRosterController.ListAuthorityProxies))!.GetCustomAttribute<RequireModuleAttribute>());
    }

    [Fact]
    public async Task AuthorityProxy_CannotBeCreatedByNurse_AndRejectsUnscopedPermissions()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();

        var admin = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "admin@clinic.com", Role = UserRole.ClinicAdmin, Name = "Admin" };
        var nurse = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "nurse@clinic.com", Role = UserRole.Nurse, Name = "Nurse" };
        var designee = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "designee@clinic.com", Role = UserRole.Nurse, Name = "Designee" };
        db.Users.AddRange(admin, nurse, designee);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        var window = (From: DateTimeOffset.UtcNow.AddHours(-1), Until: DateTimeOffset.UtcNow.AddDays(2));

        SetCaller(controller, nurse.Id, "Nurse", tenantId, clinicId);
        var byNurse = await controller.CreateAuthorityProxy(
            new CreateAuthorityProxyRequest(nurse.Id, designee.Id, "roster.approve", window.From, window.Until, null),
            CancellationToken.None);
        Assert.IsType<ForbidResult>(byNurse);

        SetCaller(controller, admin.Id, "ClinicAdmin", tenantId, clinicId);
        var wideScope = await controller.CreateAuthorityProxy(
            new CreateAuthorityProxyRequest(admin.Id, designee.Id, "roster.approve,payment.record", window.From, window.Until, null),
            CancellationToken.None) as BadRequestObjectResult;
        Assert.NotNull(wideScope);
        Assert.Equal("invalid_proxy_permission", ToJson(wideScope.Value).GetProperty("error").GetString());

        var ok = await controller.CreateAuthorityProxy(
            new CreateAuthorityProxyRequest(admin.Id, designee.Id, " ROster.approve , leave.approve ", window.From, window.Until, "Cover"),
            CancellationToken.None) as OkObjectResult;
        Assert.NotNull(ok);
        var proxy = Assert.IsType<AuthorityProxy>(ok.Value);
        Assert.Equal("roster.approve,leave.approve", proxy.Permissions);
    }

    [Fact]
    public async Task ApproveSwap_RevalidatesDoubleAssignment()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();

        var admin = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "admin@clinic.com", Role = UserRole.ClinicAdmin, Name = "Admin" };
        var nurseA = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "a@clinic.com", Role = UserRole.Nurse, Name = "Nurse A" };
        var nurseB = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "b@clinic.com", Role = UserRole.Nurse, Name = "Nurse B" };
        var profileA = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = nurseA.Id, Designation = "Staff Nurse" };
        var profileB = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = nurseB.Id, Designation = "Staff Nurse" };
        db.Users.AddRange(admin, nurseA, nurseB);
        db.NurseProfiles.AddRange(profileA, profileB);

        var day = new DateOnly(2026, 11, 10);
        var shiftA = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day, ShiftType = ShiftType.Morning, Status = ShiftStatus.Assigned, NurseProfileId = profileA.Id };
        var shiftB = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day.AddDays(1), ShiftType = ShiftType.Evening, Status = ShiftStatus.Assigned, NurseProfileId = profileB.Id };

        // B takes on another Morning shift on `day` after accepting the swap.
        var lateAssignment = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day, ShiftType = ShiftType.Morning, Status = ShiftStatus.Assigned, NurseProfileId = profileB.Id };

        db.NurseShifts.AddRange(shiftA, shiftB, lateAssignment);

        var swap = new ShiftChangeRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            RequestType = ShiftChangeType.DirectSwap,
            Status = ShiftChangeStatus.Accepted,
            RequesterNurseProfileId = profileA.Id,
            RequesterShiftId = shiftA.Id,
            TargetNurseProfileId = profileB.Id,
            TargetShiftId = shiftB.Id
        };
        db.ShiftChangeRequests.Add(swap);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        SetCaller(controller, admin.Id, "ClinicAdmin", tenantId, clinicId);

        var result = await controller.ApproveShiftChange(swap.Id, null, CancellationToken.None) as ConflictObjectResult;

        Assert.NotNull(result);
        Assert.Equal("double_assignment_conflict_detected", ToJson(result.Value).GetProperty("error").GetString());
        Assert.Equal(ShiftChangeStatus.Accepted, (await db.ShiftChangeRequests.FindAsync(swap.Id))!.Status);
    }

    [Fact]
    public async Task ApproveSwap_OnBehalfOfParticipant_IsRejected()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();

        var delegator = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "head@clinic.com", Role = UserRole.Nurse, Name = "Head Nurse" };
        var designee = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "acting@clinic.com", Role = UserRole.Nurse, Name = "Acting Nurse" };
        var third = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "third@clinic.com", Role = UserRole.Nurse, Name = "Third Nurse" };

        var delegatorProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = delegator.Id, Designation = "Head Nurse" };
        var designeeProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = designee.Id, Designation = "Staff Nurse" };
        var thirdProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = third.Id, Designation = "Staff Nurse" };

        db.Users.AddRange(delegator, designee, third);
        db.NurseProfiles.AddRange(delegatorProfile, designeeProfile, thirdProfile);

        db.AuthorityProxies.Add(new AuthorityProxy
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            DelegatorUserId = delegator.Id,
            DesigneeUserId = designee.Id,
            Permissions = "roster.approve",
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidUntil = DateTimeOffset.UtcNow.AddDays(1)
        });

        var day = new DateOnly(2026, 11, 12);
        var requesterShift = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day, ShiftType = ShiftType.Morning, Status = ShiftStatus.Assigned, NurseProfileId = delegatorProfile.Id };
        var targetShift = new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day.AddDays(1), ShiftType = ShiftType.Evening, Status = ShiftStatus.Assigned, NurseProfileId = thirdProfile.Id };
        db.NurseShifts.AddRange(requesterShift, targetShift);

        var swap = new ShiftChangeRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            RequestType = ShiftChangeType.DirectSwap,
            Status = ShiftChangeStatus.Accepted,
            RequesterNurseProfileId = delegatorProfile.Id,
            RequesterShiftId = requesterShift.Id,
            TargetNurseProfileId = thirdProfile.Id,
            TargetShiftId = targetShift.Id
        };
        db.ShiftChangeRequests.Add(swap);
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        SetCaller(controller, designee.Id, "Nurse", tenantId, clinicId);

        var result = await controller.ApproveShiftChange(swap.Id, null, CancellationToken.None) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal("cannot_approve_on_behalf_of_swap_participant", ToJson(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LeaveImpact_UsesTemplateRequiredCoverage()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var admin = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "admin@clinic.com", Role = UserRole.ClinicAdmin, Name = "Admin" };
        var nurseUser = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "n@clinic.com", Role = UserRole.Nurse, Name = "Nurse" };
        var otherUser = new User { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Email = "o@clinic.com", Role = UserRole.Nurse, Name = "Other" };
        var nurseProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = nurseUser.Id };
        var otherProfile = new NurseProfile { Id = Guid.NewGuid(), TenantId = tenantId, UserId = otherUser.Id };
        var template = new ShiftTemplate { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Name = "Ward", RequiredCoverage = 3 };

        var day = new DateOnly(2026, 11, 20);
        db.Users.AddRange(admin, nurseUser, otherUser);
        db.NurseProfiles.AddRange(nurseProfile, otherProfile);
        db.ShiftTemplates.Add(template);
        db.NurseShifts.AddRange(
            new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day, ShiftType = ShiftType.Morning, Status = ShiftStatus.Assigned, NurseProfileId = nurseProfile.Id, ShiftTemplateId = template.Id },
            new NurseShift { Id = Guid.NewGuid(), TenantId = tenantId, ClinicId = clinicId, Date = day, ShiftType = ShiftType.Morning, Status = ShiftStatus.Assigned, NurseProfileId = otherProfile.Id, ShiftTemplateId = template.Id });
        SeedMemberships(db, tenantId, clinicId);
        await db.SaveChangesAsync();

        var controller = new StaffRosterController(db, NullLogger<StaffRosterController>.Instance);
        SetCaller(controller, admin.Id, "ClinicAdmin", tenantId, clinicId);

        var result = await controller.CheckLeaveImpact(
            new LeaveImpactRequest(nurseProfile.Id, day, day), CancellationToken.None) as OkObjectResult;

        Assert.NotNull(result);
        var body = ToJson(result.Value);
        Assert.Equal(1, body.GetProperty("conflictsCount").GetInt32());

        var detail = body.GetProperty("details")[0];
        Assert.Equal(3, detail.GetProperty("requiredCoverage").GetInt32());
        Assert.True(detail.GetProperty("hasCoverageGap").GetBoolean());
    }

    [Fact]
    public async Task Vitals_AreRejectedOnClosedAppointment_AndAudited()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var nurseId = Guid.NewGuid();

        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Closed Encounter", Phone = "9900112233" };
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinicId,
            DoctorId = Guid.NewGuid(),
            PatientId = patient.Id,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            TimeSlot = "11:00",
            Status = AppointmentStatus.Completed
        };
        db.Users.Add(new User { Id = nurseId, TenantId = tenantId, ClinicId = clinicId, Email = "nurse@clinic.com", Role = UserRole.Nurse, Name = "Nurse" });
        db.Patients.Add(patient);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var controller = new VitalsController(db, NullLogger<VitalsController>.Instance);
        SetCaller(controller, nurseId, "Nurse", tenantId, clinicId);

        var request = new RecordVitalsRequest(120, 80, 70, 98.4m, 16, 98, 60, 160, null, null, null);

        var closed = await controller.RecordVitals(appointment.Id, request, CancellationToken.None) as ConflictObjectResult;
        Assert.NotNull(closed);
        Assert.Equal("appointment_closed", ToJson(closed.Value).GetProperty("error").GetString());

        appointment.Status = AppointmentStatus.CheckedIn;
        await db.SaveChangesAsync();

        var ok = await controller.RecordVitals(appointment.Id, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);

        var audit = await db.PatientAuditLogs.SingleAsync(a => a.FieldName == "VITALS_RECORDED");
        Assert.Equal(patient.Id, audit.PatientId);
        Assert.Equal(nurseId, audit.ChangedBy);
    }

    [Theory]
    [InlineData(UserRole.Nurse, true)]
    [InlineData(UserRole.Receptionist, true)]
    [InlineData(UserRole.Pharmacist, false)]
    public void VitalsWrite_Permission_FollowsClinicalRoles(UserRole role, bool expected)
    {
        Assert.Equal(expected, Permissions.RoleHasPermission(role, Permissions.VitalsWrite));
    }
}
