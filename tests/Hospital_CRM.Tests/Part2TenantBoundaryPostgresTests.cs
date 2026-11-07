using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Infrastructure.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;
using static Hospital_CRM.Tests.Part2TenantBoundaryTests;

namespace Hospital_CRM.Tests;

// Opt in only against a fresh database on a disposable local cluster, with postgres,
// app_user (LOGIN, NOSUPERUSER, NOBYPASSRLS); the migration provisions its routing role.
public sealed class Part2TenantBoundaryPostgresFactAttribute : FactAttribute
{
    public Part2TenantBoundaryPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PART2_BOUNDARY_TEST_OWNER")))
            Skip = "Requires PART2_BOUNDARY_TEST_OWNER pointing to a fresh isolated synthetic database.";
    }
}

public sealed class Part2TenantBoundaryPostgresTheoryAttribute : TheoryAttribute
{
    public Part2TenantBoundaryPostgresTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PART2_BOUNDARY_TEST_OWNER")))
            Skip = "Requires PART2_BOUNDARY_TEST_OWNER pointing to a fresh isolated synthetic database.";
    }
}

public class Part2TenantBoundaryPostgresTests(Part2TenantBoundaryPostgresFixture fixture)
    : IClassFixture<Part2TenantBoundaryPostgresFixture>
{
    [Part2TenantBoundaryPostgresFact]
    public async Task AppUser_RawAndIgnoreFiltersReadsFailClosed_AndForeignWritesFail()
    {
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        Assert.Equal("app_user", await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        Assert.Empty(await db.Patients.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"Patients\"").SingleAsync());
        using (context.PushTenant(fixture.TenantA))
        {
            Assert.Equal(fixture.PatientA, Assert.Single(await db.Patients.IgnoreQueryFilters().ToListAsync()).Id);
            Assert.Equal(0, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.\"Patients\" SET \"Name\" = 'unauthorized' WHERE \"Id\" = {fixture.PatientB}"));
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.\"Patients\" SET \"TenantId\" = {fixture.TenantB} WHERE \"Id\" = {fixture.PatientA}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.Empty(await db.Patients.IgnoreQueryFilters().ToListAsync());
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task OpenConnection_AsyncAndSyncCommandsRebindScope_AndTransactionsRestoreIt()
    {
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        await db.Database.OpenConnectionAsync();
        Assert.Empty(await db.Patients.IgnoreQueryFilters().ToListAsync());
        using (context.PushTenant(fixture.TenantA))
        {
            Assert.Equal(fixture.PatientA, Assert.Single(db.Patients.IgnoreQueryFilters().ToList()).Id);
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                using (context.PushTenant(fixture.TenantB))
                {
                    Assert.Equal(fixture.PatientB, Assert.Single(await db.Patients.IgnoreQueryFilters().ToListAsync()).Id);
                    await db.Database.ExecuteSqlRawAsync("UPDATE public.\"Patients\" SET \"Name\" = 'rolled back'");
                }
                Assert.Equal(fixture.PatientA, Assert.Single(await db.Patients.IgnoreQueryFilters().ToListAsync()).Id);
                await transaction.RollbackAsync();
            }
            using (var transaction = db.Database.BeginTransaction())
            {
                Assert.Equal(fixture.PatientA, Assert.Single(db.Patients.IgnoreQueryFilters().ToList()).Id);
                transaction.Commit();
            }
        }
        Assert.Empty(await db.Patients.IgnoreQueryFilters().ToListAsync());
        using (context.PushTenant(fixture.TenantB))
            Assert.Equal("Synthetic B", (await db.Patients.SingleAsync()).Name);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task PooledConnection_DoesNotCarryTenantOrMaintenanceToNextContext()
    {
        var contextA = new TenantContext();
        int backend;
        await using (var db = fixture.AppDb(contextA))
        {
            using var tenant = contextA.PushTenant(fixture.TenantA);
            backend = await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            Assert.Single(await db.Patients.IgnoreQueryFilters().ToListAsync());
        }
        var nextContext = new TenantContext();
        await using var next = fixture.AppDb(nextContext);
        Assert.Equal(backend, await next.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync());
        Assert.Empty(await next.Patients.IgnoreQueryFilters().ToListAsync());
        using (nextContext.PushMaintenance()) Assert.Equal(2, await next.Patients.CountAsync());
        Assert.Empty(await next.Patients.IgnoreQueryFilters().ToListAsync());
        using (nextContext.PushTenant(fixture.TenantB)) Assert.Equal(fixture.PatientB, (await next.Patients.SingleAsync()).Id);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Routing_ExactPurposeOnly_ReturnsBindingWithoutElevatingOrReadingPhi()
    {
        var (token, submission) = await fixture.SeedPrecheckAsync();
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        var binding = await PublicTokenRouting.ResolveAsync(db, PublicTokenPurpose.Precheck, token);
        Assert.Equal(new PublicTokenBinding(fixture.TenantA, submission.Id), binding);
        Assert.Null(context.CurrentTenantId);
        Assert.False(context.IsPlatformAdmin);
        Assert.Empty(await db.PrecheckSubmissions.IgnoreQueryFilters().ToListAsync());
        Assert.Null(await PublicTokenRouting.ResolveAsync(db, PublicTokenPurpose.Precheck, new string('Z', 43)));
        Assert.Null(await PublicTokenRouting.ResolveAsync(db, PublicTokenPurpose.Queue, token));
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT pg_has_role(current_user, 'public_token_router', 'MEMBER') AS \"Value\"").SingleAsync());
        var roleError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SET ROLE public_token_router"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, roleError.SqlState);
        using (context.PushTenant(binding!.TenantId)) Assert.Equal(submission.Id, (await db.PrecheckSubmissions.SingleAsync(s => s.Id == binding.ResourceId)).Id);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task PrescriptionRouting_ReusedAmendmentCodeHasOneVisitBinding_AndCollisionsFailClosed()
    {
        var (_, submission) = await fixture.SeedPrecheckAsync();
        var code = "RX-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var latestId = Guid.NewGuid();
        await using (var owner = fixture.OwnerDb())
        {
            owner.Consultations.AddRange(
                new Consultation { Id = Guid.NewGuid(), TenantId = fixture.TenantA, AppointmentId = submission.AppointmentId, DoctorId = fixture.Doctor, VerificationQrCode = code, Version = 1, Status = ConsultationStatus.Finalized },
                new Consultation { Id = latestId, TenantId = fixture.TenantA, AppointmentId = submission.AppointmentId, DoctorId = fixture.Doctor, VerificationQrCode = code, Version = 2, Status = ConsultationStatus.Amended });
            await owner.SaveChangesAsync();
        }
        await using var db = fixture.AppDb(new TenantContext());
        Assert.Equal(new PublicTokenBinding(fixture.TenantA, latestId), await PublicTokenRouting.ResolveAsync(db, PublicTokenPurpose.Prescription, code));
        var (_, another) = await fixture.SeedPrecheckAsync();
        await using (var owner = fixture.OwnerDb())
        {
            owner.Consultations.Add(new Consultation { Id = Guid.NewGuid(), TenantId = fixture.TenantA, AppointmentId = another.AppointmentId,
                DoctorId = fixture.Doctor, VerificationQrCode = code, Version = 1, Status = ConsultationStatus.Finalized });
            await owner.SaveChangesAsync();
        }
        Assert.Null(await PublicTokenRouting.ResolveAsync(db, PublicTokenPurpose.Prescription, code));
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Queue_UsesBoundTenantClinicDoctorDate_AndReturnsOnlyQueueFields()
    {
        var token = "qtrk_" + Guid.NewGuid().ToString("N");
        var date = new DateOnly(2036, 1, 1);
        await using (var owner = fixture.OwnerDb())
        {
            owner.Appointments.AddRange(
                fixture.Appointment(fixture.TenantA, fixture.ClinicA, fixture.PatientA, fixture.Doctor, date, "10:00", 8, AppointmentStatus.CheckedIn, token),
                fixture.Appointment(fixture.TenantA, fixture.ClinicA, fixture.PatientA, fixture.Doctor, date, "09:00", 2, AppointmentStatus.InConsultation),
                fixture.Appointment(fixture.TenantA, fixture.ClinicA, fixture.PatientA, fixture.Doctor, date, "09:15", 3, AppointmentStatus.InNursing),
                // Deliberately inconsistent legacy row: the clinic predicate must still exclude it.
                fixture.Appointment(fixture.TenantA, fixture.ClinicB, fixture.PatientA, fixture.Doctor, date, "09:30", 4, AppointmentStatus.CheckedIn),
                fixture.Appointment(fixture.TenantA, fixture.ClinicA, fixture.PatientA, fixture.OtherDoctor, date, "09:00", 1, AppointmentStatus.CheckedIn),
                fixture.Appointment(fixture.TenantB, fixture.ClinicB, fixture.PatientB, fixture.Doctor, date, "09:00", 1, AppointmentStatus.CheckedIn),
                fixture.Appointment(fixture.TenantA, fixture.ClinicA, fixture.PatientA, fixture.Doctor, date.AddDays(-1), "09:00", 1, AppointmentStatus.CheckedIn));
            await owner.SaveChangesAsync();
        }
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        var controller = new QueueStatusController(db, context);
        var response = Assert.IsType<OkObjectResult>(await controller.GetStatus(token, default));
        var json = Json(response.Value);
        Assert.Equal(new[] { "currentlyServing", "estimatedWaitMinutes", "expiresInSeconds", "position", "status", "yourToken" }, json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(8, json.GetProperty("yourToken").GetInt32());
        Assert.Equal(2, json.GetProperty("currentlyServing").GetInt32());
        Assert.Equal(2, json.GetProperty("position").GetInt32());
        Assert.Equal(15, json.GetProperty("estimatedWaitMinutes").GetInt32());
        Assert.Null(context.CurrentTenantId);
        Assert.Empty(await db.Appointments.IgnoreQueryFilters().ToListAsync());
        Assert.IsType<NotFoundObjectResult>(await controller.GetStatus("qtrk_" + Guid.NewGuid().ToString("N"), default));
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task BookingAndCheckIn_UseSelectedBMembership_OnPostgres()
    {
        await using (var owner = fixture.OwnerDb())
        {
            owner.TenantMemberships.AddRange(
                new TenantMembership { UserId = fixture.Doctor, TenantId = fixture.TenantB, ClinicId = fixture.ClinicB, Role = UserRole.Doctor },
                new TenantMembership { UserId = fixture.OtherDoctor, TenantId = fixture.TenantB, ClinicId = fixture.ClinicB, Role = UserRole.Receptionist });
            owner.DoctorDailyQueueCounters.Add(new DoctorDailyQueueCounter { Id = Guid.NewGuid(), TenantId = fixture.TenantA,
                DoctorId = fixture.Doctor, Date = new TimezoneService().GetClinicToday("Asia/Kolkata"), LastToken = 40 });
            await owner.SaveChangesAsync();
        }
        var http = new DefaultHttpContext { User = Principal(fixture.OtherDoctor, fixture.TenantB, fixture.ClinicB, "Receptionist") };
        var context = new TenantContext(new HttpContextAccessor { HttpContext = http });
        await using var db = fixture.AppDb(context);
        var controller = fixture.Controller(db, context, new GateStub(false));
        controller.ControllerContext.HttpContext = http;
        var booked = Assert.IsType<ObjectResult>(await controller.Book(new(fixture.PatientB, fixture.Doctor, "2037-01-02", "10:00", "scheduled"), default));
        Assert.Equal(201, booked.StatusCode);
        var id = Json(booked.Value).GetProperty("appointmentId").GetGuid();
        var appointment = await db.Appointments.SingleAsync(a => a.Id == id);
        Assert.Equal(fixture.TenantB, appointment.TenantId);
        Assert.Equal(fixture.ClinicB, appointment.ClinicId);
        var checkedIn = Assert.IsType<OkObjectResult>(await controller.CheckIn(id, default));
        Assert.Equal(1, Json(checkedIn.Value).GetProperty("queueToken").GetInt32());
        Assert.True(PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Queue, Json(checkedIn.Value).GetProperty("trackingToken").GetString()));
        Assert.Equal(Json(checkedIn.Value).GetRawText(), Json(Assert.IsType<OkObjectResult>(await controller.CheckIn(id, default)).Value).GetRawText());
        Assert.IsType<NotFoundObjectResult>(await controller.Book(new(fixture.PatientA, fixture.Doctor, "2037-01-02", "11:00", "scheduled"), default));
        var list = Json(Assert.IsType<OkObjectResult>(await controller.List("2037-01-02", null, null, default)).Value);
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("appointmentId").GetGuid());
        await using var verification = fixture.OwnerDb();
        Assert.Equal(40, (await verification.DoctorDailyQueueCounters.SingleAsync(c => c.TenantId == fixture.TenantA)).LastToken);
        Assert.Equal(1, (await verification.DoctorDailyQueueCounters.SingleAsync(c => c.TenantId == fixture.TenantB)).LastToken);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Precheck_ValidDobSubmitAndReplay_PreserveOriginalFieldsAndOtherTenant()
    {
        var (token, submission) = await fixture.SeedPrecheckAsync();
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        var gate = new GateStub(true);
        var controller = fixture.Controller(db, context, gate);
        Assert.Equal("valid", Message(await controller.GetPrecheckStatus(token, default)));
        Assert.IsType<BadRequestObjectResult>(await controller.SubmitPrecheck(token, new("1999-01-01", "wrong DOB", null, null, null), default));
        Assert.Equal("submitted", Message(await controller.SubmitPrecheck(token, new("1980-01-01", "original", "1 day", "none", "none"), default)));
        Assert.Equal("already_submitted", Message(await controller.SubmitPrecheck(token, new("1980-01-01", "overwrite", null, null, null), default)));
        Assert.Equal("already_submitted", Message(await controller.GetPrecheckStatus(token, default)));
        Assert.All(gate.CheckedTenants, tenant => Assert.Equal(fixture.TenantA, tenant));
        await using var owner = fixture.OwnerDb();
        Assert.Equal("original", (await owner.PrecheckSubmissions.SingleAsync(s => s.Id == submission.Id)).ChiefComplaint);
        Assert.Equal("Synthetic B", (await owner.Patients.SingleAsync(p => p.Id == fixture.PatientB)).Name);
        Assert.Null(context.CurrentTenantId);
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task Precheck_ExpiredDisabledAndMismatchedBindingsMakeNoWrites()
    {
        var (expiredToken, expired) = await fixture.SeedPrecheckAsync(expired: true);
        var (token, submission) = await fixture.SeedPrecheckAsync();
        var (foreignToken, foreign) = await fixture.SeedPrecheckAsync(foreignPatient: true);
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        var controller = fixture.Controller(db, context, new GateStub(true));
        Assert.IsType<BadRequestObjectResult>(await controller.GetPrecheckStatus(expiredToken, default));
        Assert.IsType<BadRequestObjectResult>(await controller.SubmitPrecheck(expiredToken, new("1980-01-01", "expired", null, null, null), default));
        Assert.IsType<BadRequestObjectResult>(await controller.GetPrecheckStatus(foreignToken, default));
        Assert.IsType<BadRequestObjectResult>(await controller.SubmitPrecheck(foreignToken, new("1980-01-01", "foreign", null, null, null), default));
        var disabled = fixture.Controller(db, context, new GateStub(false));
        Assert.Equal(403, Assert.IsType<ObjectResult>(await disabled.SubmitPrecheck(token, new("1980-01-01", "disabled", null, null, null), default)).StatusCode);
        await using var owner = fixture.OwnerDb();
        Assert.All(await owner.PrecheckSubmissions.Where(s => s.Id == expired.Id || s.Id == submission.Id || s.Id == foreign.Id).ToListAsync(), s =>
        { Assert.Null(s.SubmittedAt); Assert.Null(s.ChiefComplaint); });
    }

    [Part2TenantBoundaryPostgresFact]
    public async Task ConcurrentPrecheckSubmissions_OnlyOnePayloadWins()
    {
        var (token, submission) = await fixture.SeedPrecheckAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var gate = new ConcurrentGate(() => { if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult(); return ready.Task; });
        async Task<string> Submit(string complaint)
        {
            var context = new TenantContext();
            await using var db = fixture.AppDb(context, pooled: false);
            return Message(await fixture.Controller(db, context, gate).SubmitPrecheck(token, new("1980-01-01", complaint, null, null, null), default));
        }
        var responses = await Task.WhenAll(Submit("first"), Submit("second")).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "already_submitted", "submitted" }, responses.Order());
        await using var owner = fixture.OwnerDb();
        var saved = await owner.PrecheckSubmissions.SingleAsync(s => s.Id == submission.Id);
        Assert.NotNull(saved.SubmittedAt);
        Assert.Equal(responses[0] == "submitted" ? "first" : "second", saved.ChiefComplaint);
    }

    private static string Message(IActionResult result) => Json(Assert.IsType<OkObjectResult>(result).Value).GetProperty("message").GetString()!;
    private sealed class ConcurrentGate(Func<Task> wait) : IModuleGateService
    {
        public async Task<bool> IsModuleEnabledAsync(Guid tenantId, string moduleCode, CancellationToken ct = default) { await wait(); return true; }
        public Task<ModuleGateStatus> EvaluateAsync(Guid tenantId, string moduleCode, CancellationToken ct = default) => Task.FromResult(ModuleGateStatus.Enabled);
    }
}

public sealed class Part2TenantBoundaryPostgresFixture : IAsyncLifetime
{
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid ClinicA { get; } = Guid.NewGuid();
    public Guid ClinicB { get; } = Guid.NewGuid();
    public Guid PatientA { get; } = Guid.NewGuid();
    public Guid PatientB { get; } = Guid.NewGuid();
    public Guid Doctor { get; } = Guid.NewGuid();
    public Guid OtherDoctor { get; } = Guid.NewGuid();
    private string _owner = "";
    private string _app = "";
    private int _nextDate;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("PART2_BOUNDARY_TEST_OWNER");
        if (string.IsNullOrEmpty(connection)) return;
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (builder.Host != "127.0.0.1" || builder.Port == 5432 || builder.Username != "postgres" ||
            builder.Database?.StartsWith("part2_tenant_boundary_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Use a fresh part2_tenant_boundary_* database on a disposable loopback PostgreSQL cluster, never the application server.");
        builder.Database = "part2_tenant_boundary_" + Guid.NewGuid().ToString("N");
        _owner = builder.ConnectionString;
        builder.Username = "app_user";
        builder.Password = null;
        builder.MaxPoolSize = 1;
        _app = builder.ConnectionString;
        await using var db = OwnerDb();
        Assert.True(await db.Database.EnsureCreatedAsync(), "Database must be fresh; fixture does not overwrite existing data.");
        db.AddRange(new Tenant { Id = TenantA }, new Tenant { Id = TenantB },
            new Clinic { Id = ClinicA, TenantId = TenantA }, new Clinic { Id = ClinicB, TenantId = TenantB },
            new Patient { Id = PatientA, TenantId = TenantA, CreatedBy = Doctor, Name = "Synthetic A", Phone = "9000000001", Uhid = "SYN-A", DobHasValue = true, Dob = new DateOnly(1980, 1, 1) },
            new Patient { Id = PatientB, TenantId = TenantB, CreatedBy = Doctor, Name = "Synthetic B", Phone = "9000000002", Uhid = "SYN-B" },
            new User { Id = Doctor, TenantId = TenantA, ClinicId = ClinicA, Role = UserRole.Receptionist, Email = "doctor@synthetic.invalid", PasswordHash = "unused" },
            new User { Id = OtherDoctor, TenantId = TenantA, ClinicId = ClinicA, Role = UserRole.Doctor, Email = "other@synthetic.invalid", PasswordHash = "unused" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO app_user;
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_user;
            """);
        // Apply the shipped policy, including its anonymous exception, before the replacement migration.
        foreach (var operation in new Hospital_CRM.Infrastructure.Migrations.EnableRowLevelSecurityForTenants().UpOperations.Cast<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
        foreach (var table in new[] { "Consultations", "TenantMemberships", "DoctorDailyQueueCounters", "Payments", "ProcessedWebhookEvents", "FinanceLedgerEntries" })
#pragma warning disable EF1002 // Table identifiers come exclusively from the fixed allowlist above.
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE public."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."{table}" FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation_policy ON public."{table}" TO app_user
                USING ("TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                    OR current_setting('app.is_platform_admin', true) = 'true')
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                    OR current_setting('app.is_platform_admin', true) = 'true');
                """);
#pragma warning restore EF1002
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var operation in new Hospital_CRM.Infrastructure.Migrations.PublicTokenRouting().UpOperations.Cast<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
        await transaction.CommitAsync();
    }

    public HospitalCrmDbContext OwnerDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_owner).Options,
        TestMaintenanceTenantContext.Instance);
    public HospitalCrmDbContext AppDb(TenantContext context, bool pooled = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(_app) { Pooling = pooled };
        return new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(builder.ConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor(context)).Options, context);
    }
    public AppointmentsController Controller(HospitalCrmDbContext db, TenantContext context, IModuleGateService gate) =>
        new(db, new NotificationStub(), new PrecheckService(db, new ConfigurationBuilder().Build()), new TimezoneService(), gate, context)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

    public Appointment Appointment(Guid tenant, Guid clinic, Guid patient, Guid doctor, DateOnly date, string time,
        int? queue = null, AppointmentStatus status = AppointmentStatus.Booked, string? token = null) => new()
        {
            Id = Guid.NewGuid(), TenantId = tenant, ClinicId = clinic, PatientId = patient, DoctorId = doctor,
            Date = date, TimeSlot = time, QueueToken = queue, Status = status, QueueTrackingToken = token,
            QueueTrackingExpiresAt = token is null ? null : DateTimeOffset.UtcNow.AddHours(24)
        };

    public async Task<(string Token, PrecheckSubmission Submission)> SeedPrecheckAsync(bool expired = false, bool foreignPatient = false)
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var db = OwnerDb();
        var appointment = Appointment(TenantA, ClinicA, foreignPatient ? PatientB : PatientA, Doctor,
            new DateOnly(2035, 1, 1).AddDays(Interlocked.Increment(ref _nextDate)), "10:00");
        var submission = new PrecheckSubmission
        {
            Id = Guid.NewGuid(), TenantId = TenantA, AppointmentId = appointment.Id,
            TokenHash = token.HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 24)
        };
        db.AddRange(appointment, submission);
        await db.SaveChangesAsync();
        return (token, submission);
    }

    public Task DisposeAsync() => Task.CompletedTask; // Keep synthetic evidence; never drop a caller's database.
}
