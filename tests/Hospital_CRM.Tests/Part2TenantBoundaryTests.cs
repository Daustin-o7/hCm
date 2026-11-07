using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2TenantBoundaryTests
{
    [Fact]
    public async Task Context_FailsClosed_AndRestoresNestedExplicitScopes()
    {
        var context = new TenantContext();
        Assert.Null(context.CurrentTenantId);
        Assert.False(context.IsPlatformAdmin);
        Assert.Throws<ArgumentException>(() => context.PushTenant(Guid.Empty));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using (context.PushMaintenance())
        {
            Assert.True(context.IsPlatformAdmin);
            using (context.PushTenant(tenantA))
            {
                Assert.False(context.IsPlatformAdmin);
                Assert.Throws<InvalidOperationException>(() => context.PushMaintenance());
                using (context.PushTenant(tenantB))
                {
                    await Task.Yield();
                    Assert.Equal(tenantB, context.CurrentTenantId);
                }
                Assert.Equal(tenantA, context.CurrentTenantId);
                var separateScope = new TenantContext();
                Assert.Null(separateScope.CurrentTenantId);
                Assert.False(separateScope.IsPlatformAdmin);
            }
            Assert.True(context.IsPlatformAdmin);
        }
        Assert.Null(context.CurrentTenantId);
        Assert.False(context.IsPlatformAdmin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpContext_RequiresAuthenticatedClaims_AndNeverAllowsMaintenance(bool authenticated)
    {
        var tenant = Guid.NewGuid();
        var http = new DefaultHttpContext { User = Principal(Guid.NewGuid(), tenant, Guid.NewGuid(), "PlatformAdmin", authenticated) };
        var context = new TenantContext(new HttpContextAccessor { HttpContext = http });
        Assert.Equal(authenticated ? tenant : (Guid?)null, context.CurrentTenantId);
        Assert.Equal(authenticated, context.IsPlatformAdmin);
        Assert.Throws<InvalidOperationException>(() => context.PushMaintenance());
        using (context.PushTenant(Guid.NewGuid())) Assert.False(context.IsPlatformAdmin);
        Assert.Equal(authenticated, context.IsPlatformAdmin);
    }

    [Theory]
    [InlineData(PublicTokenPurpose.Queue, "1")]
    [InlineData(PublicTokenPurpose.Queue, "qtrk_0000000000000000000000000000000G")]
    [InlineData(PublicTokenPurpose.Precheck, "' OR 1=1 --")]
    [InlineData(PublicTokenPurpose.Precheck, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData(PublicTokenPurpose.Prescription, "RX-abcdef123456")]
    public async Task MalformedPublicToken_IsRejectedBeforeDatabaseAccess(PublicTokenPurpose purpose, string token)
    {
        using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>().Options, new TenantContext());
        Assert.Null(await PublicTokenRouting.ResolveAsync(db, purpose, token));
    }

    [Fact]
    public async Task Booking_UsesSelectedBDoctorMembership_DespiteGlobalAAndDifferentGlobalRole()
    {
        using var fixture = new BookingFixture();
        await fixture.SeedAsync();
        var response = Assert.IsType<ObjectResult>(await fixture.Controller.Book(fixture.Request(), default));
        Assert.Equal(201, response.StatusCode);
        var appointment = Assert.Single(await fixture.Db.Appointments.ToListAsync());
        Assert.Equal(fixture.ClinicB.Id, appointment.ClinicId);
        Assert.Equal(fixture.ClinicB.TenantId, appointment.TenantId);
        Assert.Equal(fixture.Doctor.Id, appointment.DoctorId);
    }

    [Theory]
    [InlineData("suspended", UserRole.Doctor)]
    [InlineData("active", UserRole.Nurse)]
    [InlineData("invited", UserRole.Doctor)]
    public async Task Booking_RejectsInactiveOrWrongRoleProvider(string status, UserRole role)
    {
        using var fixture = new BookingFixture();
        fixture.Provider.Status = status;
        fixture.Provider.Role = role;
        await fixture.SeedAsync();
        var result = Assert.IsType<NotFoundObjectResult>(await fixture.Controller.Book(fixture.Request(), default));
        Assert.Equal("doctor_not_found", Json(result.Value).GetProperty("error").GetString());
        Assert.Empty(await fixture.Db.Appointments.ToListAsync());
    }

    [Fact]
    public async Task Booking_RejectsForeignProviderAndPatient_WithNoWrites()
    {
        using var fixture = new BookingFixture();
        fixture.Provider.TenantId = fixture.ClinicA.TenantId;
        fixture.Provider.ClinicId = fixture.ClinicA.Id;
        await fixture.SeedAsync();
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.Book(fixture.Request(), default));
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.Book(fixture.Request() with { PatientId = fixture.PatientA.Id }, default));
        Assert.Empty(await fixture.Db.Appointments.ToListAsync());
    }

    [Theory]
    [InlineData("suspended", UserRole.Receptionist)]
    [InlineData("active", UserRole.Nurse)]
    public async Task SelectedCaller_IsRevalidatedBeforeListOrMutation(string status, UserRole role)
    {
        using var fixture = new BookingFixture();
        fixture.CallerMembership.Status = status;
        fixture.CallerMembership.Role = role;
        await fixture.SeedAsync();
        Assert.IsType<ForbidResult>(await fixture.Controller.List(null, null, null, default));
        Assert.IsType<ForbidResult>(await fixture.Controller.Book(fixture.Request(), default));
    }

    [Fact]
    public async Task SelectedClinic_HidesSameTenantOtherClinic_FromListAndMutations()
    {
        using var fixture = new BookingFixture();
        await fixture.SeedAsync();
        var otherClinic = new Clinic { Id = Guid.NewGuid(), TenantId = fixture.ClinicB.TenantId };
        var foreign = fixture.Appointment(otherClinic.Id);
        fixture.Db.AddRange(otherClinic, foreign);
        await fixture.Db.SaveChangesAsync();
        var list = Assert.IsType<OkObjectResult>(await fixture.Controller.List(null, null, null, default));
        Assert.Equal(0, Json(list.Value).GetArrayLength());
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.CheckIn(foreign.Id, default));
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.Update(foreign.Id, new("confirmed"), default));
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.Reschedule(foreign.Id, new("2035-01-02", "11:00"), default));
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.UpdatePriority(foreign.Id, new("urgent"), default));
        Assert.Equal(AppointmentStatus.Booked, foreign.Status);
        Assert.Null(foreign.QueueTrackingToken);
    }

    [Theory]
    [InlineData("holiday", "clinic_closed_on_selected_date")]
    [InlineData("annual", "clinic_closed_on_selected_date")]
    [InlineData("weekly", "clinic_closed_on_selected_day")]
    [InlineData("split", "time_slot_outside_working_hours")]
    [InlineData("special", "time_slot_outside_special_working_hours")]
    [InlineData("occupied", "slot_already_booked")]
    [InlineData("malformed", "invalid_time_slot")]
    public async Task SharedBookingValidation_EnforcesCalendarAndSlotRules(string scenario, string expected)
    {
        using var fixture = new BookingFixture();
        if (scenario is "holiday" or "annual") fixture.ClinicB.Holidays.Add(new ClinicHoliday
        {
            Id = Guid.NewGuid(), StartDate = scenario == "annual" ? "2000-12-29" : "2035-01-02",
            EndDate = scenario == "annual" ? "2001-01-03" : "2035-01-02", RecurringAnnually = scenario == "annual"
        });
        if (scenario is "weekly" or "split") fixture.ClinicB.WorkingHours.Add(new ClinicHours
        {
            Id = Guid.NewGuid(), DayOfWeek = scenario == "weekly" ? (int)DayOfWeek.Sunday : (int)new DateOnly(2035, 1, 2).DayOfWeek,
            OpenTime = "11:00", CloseTime = "13:00"
        });
        if (scenario == "special") fixture.ClinicB.SpecialHours.Add(new ClinicSpecialHour
        { Id = Guid.NewGuid(), Date = "2035-01-02", OpenTime = "11:00", CloseTime = "13:00" });
        await fixture.SeedAsync();
        if (scenario == "occupied")
        {
            fixture.Db.Appointments.Add(fixture.Appointment(fixture.ClinicB.Id));
            await fixture.Db.SaveChangesAsync();
        }
        Assert.Equal(expected, await AppointmentsController.ValidateBookingAsync(fixture.Db,
            fixture.ClinicB.TenantId, fixture.ClinicB.Id, fixture.PatientB.Id, fixture.Doctor.Id,
            new DateOnly(2035, 1, 2), scenario == "malformed" ? "9:30" : "10:00"));
    }

    [Fact]
    public async Task Reschedule_ExcludesOwnSlot_ButChecksCurrentProviderMembership()
    {
        using var fixture = new BookingFixture();
        await fixture.SeedAsync();
        var appointment = fixture.Appointment(fixture.ClinicB.Id);
        fixture.Db.Appointments.Add(appointment);
        await fixture.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await fixture.Controller.Reschedule(appointment.Id, new("2035-01-02", "10:00"), default));
        fixture.Provider.Status = "suspended";
        fixture.Db.Update(fixture.Provider);
        await fixture.Db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.Reschedule(appointment.Id, new("2035-01-02", "11:00"), default));
        Assert.Equal("10:00", appointment.TimeSlot);
    }

    [Fact]
    public async Task PrecheckGeneration_BindsOwnership_AndExpiresAtClinicLocalSlot()
    {
        using var fixture = new BookingFixture();
        await fixture.SeedAsync();
        var appointment = fixture.Appointment(fixture.ClinicB.Id);
        fixture.Db.Appointments.Add(appointment);
        await fixture.Db.SaveChangesAsync();
        var (token, submission) = await fixture.Precheck.GenerateForAppointmentAsync(appointment.Id, fixture.ClinicB.TenantId);
        Assert.NotNull(submission);
        Assert.True(PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Precheck, token));
        Assert.Equal(token!.HashToken(), submission.TokenHash);
        Assert.Equal(new DateTimeOffset(2035, 1, 2, 4, 30, 0, TimeSpan.Zero), submission.ExpiresAt);
        Assert.Equal(fixture.ClinicB.TenantId, submission.TenantId);
        Assert.Empty(await fixture.Db.PrecheckSubmissions.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Precheck.GenerateForAppointmentAsync(appointment.Id, fixture.ClinicA.TenantId));
        appointment.PatientId = fixture.PatientA.Id;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Precheck.GenerateForAppointmentAsync(appointment.Id, fixture.ClinicB.TenantId));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("no_show")]
    public async Task QueueLink_CheckInIsBoundedAndIdempotent_TerminalTransitionShortensOnlyOnce(string status)
    {
        using var f = new BookingFixture();
        await f.SeedAsync();
        var visit = f.Appointment(f.ClinicB.Id);
        f.Db.Add(visit);
        await f.Db.SaveChangesAsync();
        var before = DateTimeOffset.UtcNow;
        Assert.IsType<OkObjectResult>(await f.Controller.CheckIn(visit.Id, default));
        Assert.InRange(visit.QueueTrackingExpiresAt!.Value, before.AddHours(24), DateTimeOffset.UtcNow.AddHours(24));
        var token = visit.QueueTrackingToken;
        var deadline = visit.QueueTrackingExpiresAt;
        Assert.IsType<OkObjectResult>(await f.Controller.CheckIn(visit.Id, default));
        Assert.Equal(token, visit.QueueTrackingToken);
        Assert.Equal(deadline, visit.QueueTrackingExpiresAt);
        if (status == "completed") Assert.IsType<OkObjectResult>(await f.Controller.Update(visit.Id, new("in_consultation"), default));
        Assert.IsType<OkObjectResult>(await f.Controller.Update(visit.Id, new(status), default));
        var endedAt = (await f.Db.AppointmentHistories.OrderByDescending(h => h.ChangedAt).FirstAsync()).ChangedAt;
        Assert.Equal(endedAt.AddHours(2), visit.QueueTrackingExpiresAt);
        deadline = visit.QueueTrackingExpiresAt;
        Assert.IsType<OkObjectResult>(await f.Controller.Update(visit.Id, new(status), default));
        Assert.Equal(deadline, visit.QueueTrackingExpiresAt);
    }

    [Fact]
    public async Task QueueLink_RescheduleClearsOldCredentialAndDeadline()
    {
        using var f = new BookingFixture();
        await f.SeedAsync();
        var visit = f.Appointment(f.ClinicB.Id);
        f.Db.Add(visit);
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await f.Controller.CheckIn(visit.Id, default));
        var old = visit.QueueTrackingToken;
        Assert.IsType<OkObjectResult>(await f.Controller.Reschedule(visit.Id, new("2035-01-03", "11:00"), default));
        Assert.Null(visit.QueueTrackingToken);
        Assert.Null(visit.QueueTrackingExpiresAt);
        Assert.IsType<OkObjectResult>(await f.Controller.CheckIn(visit.Id, default));
        Assert.NotEqual(old, visit.QueueTrackingToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(24)]
    public void QueueLink_TerminalGraceCrossesMidnightButNeverExtendsOrRevives(int? hoursLeft)
    {
        var endedAt = new DateTimeOffset(2026, 11, 7, 23, 55, 0, TimeSpan.FromHours(5.5));
        var visit = new Appointment { QueueTrackingExpiresAt = hoursLeft.HasValue ? endedAt.AddHours(hoursLeft.Value) : null };
        visit.EndQueueTracking(endedAt);
        Assert.Equal(hoursLeft.HasValue ? endedAt.AddHours(Math.Min(hoursLeft.Value, 2)) : (DateTimeOffset?)null, visit.QueueTrackingExpiresAt);
        var deadline = visit.QueueTrackingExpiresAt;
        visit.UpdatedAt = endedAt.AddDays(1);
        visit.EndQueueTracking(visit.UpdatedAt);
        Assert.Equal(deadline, visit.QueueTrackingExpiresAt);
    }

    internal static ClaimsPrincipal Principal(Guid user, Guid tenant, Guid clinic, string role, bool authenticated = true) =>
        new(new ClaimsIdentity([new("sub", user.ToString()), new("tenant_id", tenant.ToString()),
            new("clinicId", clinic.ToString()), new("role", role)], authenticated ? "test" : null));

    internal static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    internal sealed class NotificationStub : INotificationService
    {
        public Task SendAppointmentConfirmationAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null) => Task.CompletedTask;
        public Task SendAppointmentReminderAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null) => Task.CompletedTask;
    }

    internal sealed class GateStub(bool enabled) : IModuleGateService
    {
        public List<Guid> CheckedTenants { get; } = [];
        public Task<bool> IsModuleEnabledAsync(Guid tenantId, string moduleCode, CancellationToken ct = default)
        { CheckedTenants.Add(tenantId); return Task.FromResult(enabled); }
        public Task<ModuleGateStatus> EvaluateAsync(Guid tenantId, string moduleCode, CancellationToken ct = default) =>
            Task.FromResult(enabled ? ModuleGateStatus.Enabled : ModuleGateStatus.NotEntitled);
    }

    private sealed class BookingFixture : IDisposable
    {
        public Clinic ClinicA { get; } = new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        public Clinic ClinicB { get; } = new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        public Patient PatientA { get; }
        public Patient PatientB { get; }
        public User Doctor { get; }
        public User Caller { get; }
        public TenantMembership Provider { get; }
        public TenantMembership CallerMembership { get; }
        public HospitalCrmDbContext Db { get; }
        public AppointmentsController Controller { get; }
        public PrecheckService Precheck { get; }
        private readonly DbContextOptions<HospitalCrmDbContext> _options;

        public BookingFixture()
        {
            PatientA = new() { Id = Guid.NewGuid(), TenantId = ClinicA.TenantId, Name = "Synthetic A", Phone = "9000000001" };
            PatientB = new() { Id = Guid.NewGuid(), TenantId = ClinicB.TenantId, Name = "Synthetic B", Phone = "9000000002" };
            Doctor = new() { Id = Guid.NewGuid(), ClinicId = ClinicA.Id, TenantId = ClinicA.TenantId, Role = UserRole.Receptionist, Email = "provider@synthetic.invalid", PasswordHash = "unused" };
            Caller = new() { Id = Guid.NewGuid(), ClinicId = ClinicA.Id, TenantId = ClinicA.TenantId, Role = UserRole.Nurse, Email = "caller@synthetic.invalid", PasswordHash = "unused" };
            Provider = new() { UserId = Doctor.Id, TenantId = ClinicB.TenantId, ClinicId = ClinicB.Id, Role = UserRole.Doctor };
            CallerMembership = new() { UserId = Caller.Id, TenantId = ClinicB.TenantId, ClinicId = ClinicB.Id, Role = UserRole.Receptionist };
            var http = new DefaultHttpContext { User = Principal(Caller.Id, ClinicB.TenantId, ClinicB.Id, "Receptionist") };
            var context = new TenantContext(new HttpContextAccessor { HttpContext = http });
            _options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new(_options, context);
            Precheck = new(Db, new ConfigurationBuilder().Build());
            Controller = new(Db, new NotificationStub(), Precheck, new TimezoneService(), new GateStub(false), context)
            { ControllerContext = new() { HttpContext = http } };
        }

        public async Task SeedAsync()
        {
            var context = new TenantContext();
            using var maintenance = context.PushMaintenance();
            using var db = new HospitalCrmDbContext(_options, context);
            db.AddRange(ClinicA, ClinicB, PatientA, PatientB, Doctor, Caller, Provider, CallerMembership);
            await db.SaveChangesAsync();
        }

        public BookAppointmentRequest Request() => new(PatientB.Id, Doctor.Id, "2035-01-02", "10:00", "scheduled");
        public Appointment Appointment(Guid clinic) => new()
        {
            Id = Guid.NewGuid(), TenantId = ClinicB.TenantId, ClinicId = clinic, PatientId = PatientB.Id,
            DoctorId = Doctor.Id, Date = new(2035, 1, 2), TimeSlot = "10:00", Type = AppointmentType.Scheduled
        };
        public void Dispose() => Db.Dispose();
    }
}
