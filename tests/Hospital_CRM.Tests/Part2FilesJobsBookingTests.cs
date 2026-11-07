using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static Hospital_CRM.Tests.Part2FilesJobsNotificationTests;

namespace Hospital_CRM.Tests;

// Apply the parent-owned integration hook in the test model until the shared DbContext is integrated.
internal sealed class Part2FilesJobsIntentDbContext(DbContextOptions<HospitalCrmDbContext> options, ITenantContext? tenant = null)
    : HospitalCrmDbContext(options, tenant)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new NotificationIntentConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class Part2FilesJobsPostgresFactAttribute : FactAttribute
{
    public Part2FilesJobsPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PART2_FILES_JOBS_TEST_OWNER")))
            Skip = "Requires a disposable part2_files_jobs_* PostgreSQL database on a non-application port.";
    }
}

public sealed class Part2FilesJobsPostgresFixture : IAsyncLifetime
{
    private string _connection = "";
    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable("PART2_FILES_JOBS_TEST_OWNER");
        if (string.IsNullOrEmpty(value)) return;
        var connection = new NpgsqlConnectionStringBuilder(value);
        if (connection.Host != "127.0.0.1" || connection.Port == 5432 || connection.Username != "postgres" ||
            connection.Database?.StartsWith("part2_files_jobs_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Use a fresh synthetic database on a disposable loopback PostgreSQL cluster.");
        _connection = connection.ConnectionString;
        await using var db = Db(TestMaintenanceTenantContext.Instance);
        Assert.True(await db.Database.EnsureCreatedAsync(), "Test database must be fresh; existing data is never overwritten.");
    }
    public HospitalCrmDbContext Db(ITenantContext context, SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_connection);
        if (interceptor != null) options.AddInterceptors(interceptor);
        return new Part2FilesJobsIntentDbContext(options.Options, context);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class Part2FilesJobsBookingTests(Part2FilesJobsPostgresFixture database) : IClassFixture<Part2FilesJobsPostgresFixture>
{
    [Part2FilesJobsPostgresFact]
    public async Task Booking_commits_intent_and_original_precheck_before_awaited_handoff()
    {
        await using var fixture = await BookingFixture.Create(database);
        var hold = new HoldHandoff();
        await using var db = database.Db(fixture.Context, hold);
        fixture.Jobs.BeforeCreate = () =>
        {
            using var committed = database.Db(fixture.Context);
            Assert.Single(committed.Appointments.ToList());
            Assert.Single(committed.PrecheckSubmissions.ToList());
            Assert.Null(committed.Set<NotificationIntent>().Single().HandedOffAt);
        };
        var booking = fixture.Controller(db).Book(fixture.Request(), default);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(booking.IsCompleted);
        hold.Release.SetResult();
        Assert.Equal(201, Assert.IsType<ObjectResult>(await booking).StatusCode);
        await using var persisted = database.Db(fixture.Context);
        var intent = await persisted.Set<NotificationIntent>().SingleAsync();
        var submission = await persisted.PrecheckSubmissions.SingleAsync();
        var link = fixture.Unprotect(intent);
        Assert.Equal(new Uri(link).Segments[^1].HashToken(), submission.TokenHash);
        Assert.Equal(submission.AppointmentId, intent.AppointmentId);
        Assert.Equal(fixture.Date, intent.AppointmentDate);
        Assert.NotNull(intent.HandedOffAt);
        Assert.DoesNotContain(new Uri(link).Segments[^1], JsonSerializer.Serialize(intent));
        Assert.DoesNotContain(new Uri(link).Segments[^1], InvocationData.SerializeJob(Assert.Single(fixture.Jobs.Jobs)).SerializePayload());
        Assert.Empty(await persisted.NotificationLogs.Where(n => n.Appointment.TenantId == fixture.TenantId).ToListAsync());
        Assert.Empty(fixture.Channel.Messages);
    }

    [Part2FilesJobsPostgresFact]
    public async Task Intent_save_failure_rolls_back_appointment_and_precheck_atomically()
    {
        await using var fixture = await BookingFixture.Create(database);
        await using var db = database.Db(fixture.Context, new FailIntentSave(EntityState.Added));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller(db).Book(fixture.Request(), default));
        await using var persisted = database.Db(fixture.Context);
        Assert.Empty(await persisted.Appointments.ToListAsync());
        Assert.Empty(await persisted.PrecheckSubmissions.ToListAsync());
        Assert.Empty(await persisted.Set<NotificationIntent>().ToListAsync());
        Assert.Empty(fixture.Jobs.Jobs);
    }

    [Part2FilesJobsPostgresFact]
    public async Task Failed_handoff_recovers_in_worker_after_restart_without_regenerating_token()
    {
        await using var fixture = await BookingFixture.Create(database);
        fixture.Jobs.BeforeCreate = () => throw new IOException("synthetic queue outage");
        await using (var db = database.Db(fixture.Context))
            Assert.Equal(201, Assert.IsType<ObjectResult>(await fixture.Controller(db).Book(fixture.Request(), default)).StatusCode);
        string link;
        DateTimeOffset expiry;
        await using (var committed = database.Db(fixture.Context))
        {
            var intent = await committed.Set<NotificationIntent>().SingleAsync();
            Assert.Null(intent.HandedOffAt);
            link = fixture.Unprotect(intent);
            expiry = (await committed.PrecheckSubmissions.SingleAsync()).ExpiresAt;
        }
        fixture.Jobs.BeforeCreate = null;
        fixture.ReloadKeys();
        await fixture.Recover();
        await fixture.Recover();
        await fixture.DeliverQueued();
        var message = Assert.Single(fixture.Channel.Messages);
        Assert.Equal(link, message.Parameters[^1]);
        await using var persisted = database.Db(fixture.Context);
        Assert.NotNull((await persisted.Set<NotificationIntent>().SingleAsync()).HandedOffAt);
        Assert.Equal(expiry, (await persisted.PrecheckSubmissions.SingleAsync()).ExpiresAt);
        Assert.Equal(NotificationStatus.Sent, (await persisted.NotificationLogs.SingleAsync(n => n.Appointment.TenantId == fixture.TenantId)).Status);
        Assert.Contains(fixture.Log.Messages, m => m.Contains("handoff pending"));
    }

    [Part2FilesJobsPostgresFact]
    public async Task Crash_after_enqueue_before_ledger_recovery_can_duplicate_jobs_but_not_provider_handoff()
    {
        await using var fixture = await BookingFixture.Create(database);
        fixture.Jobs.AfterCreate = () => throw new IOException("synthetic crash after durable enqueue");
        await using (var db = database.Db(fixture.Context))
            Assert.Equal(201, Assert.IsType<ObjectResult>(await fixture.Controller(db).Book(fixture.Request(), default)).StatusCode);
        fixture.Jobs.AfterCreate = null;
        await fixture.Recover();
        Assert.Equal(2, fixture.Jobs.Jobs.Count);
        await fixture.DeliverQueued();
        Assert.Single(fixture.Channel.Messages);
        await using var persisted = database.Db(fixture.Context);
        Assert.Single(await persisted.NotificationLogs.Where(n => n.Appointment.TenantId == fixture.TenantId).ToListAsync());
        Assert.NotNull((await persisted.Set<NotificationIntent>().SingleAsync()).HandedOffAt);
    }

    [Part2FilesJobsPostgresFact]
    public async Task Crash_before_handoff_ack_reuses_existing_durable_job()
    {
        await using var fixture = await BookingFixture.Create(database);
        await using (var db = database.Db(fixture.Context, new FailIntentSave(EntityState.Modified)))
            Assert.Equal(201, Assert.IsType<ObjectResult>(await fixture.Controller(db).Book(fixture.Request(), default)).StatusCode);
        await using (var persisted = database.Db(fixture.Context))
            Assert.Null((await persisted.Set<NotificationIntent>().SingleAsync()).HandedOffAt);
        await fixture.Recover();
        Assert.Single(fixture.Jobs.Jobs);
        await fixture.DeliverQueued();
        Assert.Single(fixture.Channel.Messages);
    }

    [Part2FilesJobsPostgresFact]
    public async Task Recovery_preserves_original_event_date_and_rechecks_changed_appointment()
    {
        await using var fixture = await BookingFixture.Create(database);
        fixture.Jobs.BeforeCreate = () => throw new IOException("synthetic outage");
        await using (var db = database.Db(fixture.Context))
        {
            await fixture.Controller(db).Book(fixture.Request(), default);
            (await db.Appointments.SingleAsync()).Date = fixture.Date.AddDays(1);
            await db.SaveChangesAsync();
        }
        fixture.Jobs.BeforeCreate = null;
        await fixture.Recover();
        Assert.Equal(fixture.Date, Assert.Single(fixture.Jobs.Jobs).Args[5]);
        await fixture.DeliverQueued();
        Assert.Empty(fixture.Channel.Messages);
        await using var persisted = database.Db(fixture.Context);
        Assert.Equal("appointment_no_longer_eligible", (await persisted.NotificationLogs.SingleAsync(n => n.Appointment.TenantId == fixture.TenantId)).FailedReason);
    }

    [Part2FilesJobsPostgresFact]
    public async Task Foreign_tenant_cannot_read_or_handoff_committed_intent()
    {
        await using var fixture = await BookingFixture.Create(database);
        fixture.Jobs.BeforeCreate = () => throw new IOException("synthetic outage");
        Guid intentId;
        await using (var db = database.Db(fixture.Context))
        {
            await fixture.Controller(db).Book(fixture.Request(), default);
            intentId = (await db.Set<NotificationIntent>().SingleAsync()).Id;
        }
        fixture.Jobs.BeforeCreate = null;
        using (fixture.Context.PushTenant(Guid.NewGuid()))
        {
            await using var other = database.Db(fixture.Context);
            Assert.Empty(await other.Set<NotificationIntent>().ToListAsync());
            await fixture.Service(other).HandoffConfirmationAsync(intentId);
        }
        Assert.Empty(fixture.Jobs.Jobs);
        await fixture.Recover();
    }

    [Part2FilesJobsPostgresFact]
    public async Task Lab_result_versions_and_format_metadata_survive_fresh_database_and_key_contexts()
    {
        await using var fixture = await BookingFixture.Create(database);
        var orderId = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Lab:UploadDir"] = "reports" }).Build();
        var env = new TestEnvironment { ContentRootPath = fixture.Root };
        var bytes = Encoding.UTF8.GetBytes("test,value\nsynthetic,7\n");
        await using (var db = database.Db(fixture.Context))
        {
            db.LabOrders.Add(new LabOrder { Id = orderId, TenantId = fixture.TenantId, PatientId = fixture.PatientId,
                DoctorId = fixture.UserId, TestName = "Synthetic persistence", Consultation = new Consultation
                {
                    Id = Guid.NewGuid(), TenantId = fixture.TenantId, DoctorId = fixture.UserId,
                    Appointment = new Appointment { Id = Guid.NewGuid(), TenantId = fixture.TenantId,
                        ClinicId = fixture.ClinicId, DoctorId = fixture.UserId, PatientId = fixture.PatientId,
                        Date = fixture.Date, TimeSlot = "10:00", Status = AppointmentStatus.InConsultation }
                } });
            await db.SaveChangesAsync();
            var controller = new LabOrdersController(db, config, new LabFileStorage(config, fixture.Protection, env))
                { ControllerContext = new() { HttpContext = fixture.Http } };
            for (var version = 1; version <= 2; version++)
            {
                using var stream = new MemoryStream(bytes);
                var file = new FormFile(stream, 0, bytes.Length, "file", "report.csv");
                Assert.Equal(201, Assert.IsType<ObjectResult>(await controller.EnterResult(orderId, null, false, null, null, file, default)).StatusCode);
            }
        }
        fixture.ReloadKeys();
        await using var persisted = database.Db(fixture.Context);
        var reader = new LabOrdersController(persisted, config, new LabFileStorage(config, fixture.Protection, env))
            { ControllerContext = new() { HttpContext = fixture.Http } };
        var detail = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await reader.GetOrder(orderId, default)).Value);
        var latest = detail.GetProperty("latestResult");
        Assert.Equal(2, latest.GetProperty("version").GetInt32());
        Assert.Equal(".csv", latest.GetProperty("fileExtension").GetString());
        var results = await persisted.LabResults.OrderBy(r => r.Version).ToListAsync();
        Assert.Equal(results[0].Id, results[1].PreviousVersionId);
        foreach (var result in results)
        {
            var response = Assert.IsType<FileContentResult>(await reader.DownloadResult(orderId, result.Id, default));
            Assert.Equal(bytes, response.FileContents);
            Assert.Equal("text/csv", response.ContentType);
            Assert.Equal($"lab-result-v{result.Version}.csv", response.FileDownloadName);
        }
    }

    private sealed class FailIntentSave(EntityState state) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<NotificationIntent>().Any(e => e.State == state))
                throw new InvalidOperationException("synthetic intent persistence failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HoldHandoff : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<NotificationIntent>().Any(e => e.State == EntityState.Modified))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return result;
        }
    }

    private sealed class BookingFixture(Part2FilesJobsPostgresFixture database) : IAsyncDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid ClinicId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid PatientId { get; } = Guid.NewGuid();
        public DateOnly Date { get; } = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "part2-booking-" + Guid.NewGuid().ToString("N"));
        public TenantContext Context { get; private set; } = null!;
        public DefaultHttpContext Http { get; private set; } = null!;
        public CaptureJobs Jobs { get; } = new();
        public MemoryJobStorage Storage { get; } = new();
        public TestChannel Channel { get; } = new();
        public CapturedLogger<StubNotificationService> Log { get; } = new();
        public IDataProtectionProvider Protection { get; private set; } = null!;
        public void ReloadKeys() => Protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys")));
        public static async Task<BookingFixture> Create(Part2FilesJobsPostgresFixture database)
        {
            var f = new BookingFixture(database);
            f.Http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, f.UserId.ToString()), new Claim("tenant_id", f.TenantId.ToString()),
                new Claim("clinicId", f.ClinicId.ToString()), new Claim(ClaimTypes.Role, "Doctor")], "test")) };
            f.Context = new TenantContext(new FixtureHttpContextAccessor(f.Http));
            f.ReloadKeys();
            await using var db = database.Db(f.Context);
            db.AddRange(new Tenant { Id = f.TenantId, Name = "Synthetic", ActivatedModules = "core,precheck" },
                new Clinic { Id = f.ClinicId, TenantId = f.TenantId, Name = "Synthetic clinic", Timezone = "Asia/Kolkata" },
                new User { Id = f.UserId, TenantId = f.TenantId, ClinicId = f.ClinicId, Role = UserRole.Doctor, Email = f.UserId + "@synthetic.invalid", PasswordHash = "unused" },
                new TenantMembership { UserId = f.UserId, TenantId = f.TenantId, ClinicId = f.ClinicId, Role = UserRole.Doctor, Status = "active" },
                new Patient { Id = f.PatientId, TenantId = f.TenantId, CreatedBy = f.UserId, Name = "Synthetic patient", Phone = "9000000000" },
                new PatientConsent { Id = Guid.NewGuid(), TenantId = f.TenantId, PatientId = f.PatientId, CapturedBy = f.UserId, Purpose = "whatsapp_opt_in" },
                new MessageTemplate { Id = Guid.NewGuid(), TenantId = f.TenantId, Name = "appointment_confirmation", ApprovalStatus = TemplateApprovalStatus.Approved,
                    Channel = NotificationChannel.WhatsApp, Content = "Hello {{patient_name}} {{precheck_link}}" });
            await db.SaveChangesAsync();
            return f;
        }
        public StubNotificationService Service(HospitalCrmDbContext db, ITenantContext? context = null) =>
            new(db, context ?? Context, Jobs, Storage, Channel, Protection, new TimezoneService(), new Part2TenantBoundaryTests.GateStub(true), Log);
        public AppointmentsController Controller(HospitalCrmDbContext db) => new(db, Service(db),
            new PrecheckService(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["App:PublicBaseUrl"] = "https://synthetic.invalid" }).Build()), new TimezoneService(), new Part2TenantBoundaryTests.GateStub(true), Context)
            { ControllerContext = new() { HttpContext = Http } };
        public BookAppointmentRequest Request() => new(PatientId, UserId, Date.ToString("yyyy-MM-dd"), "10:00", "scheduled");
        public string Unprotect(NotificationIntent intent) => Protection.CreateProtector("HospitalCrm.NotificationPrecheck.v1",
            TenantId.ToString("N"), intent.AppointmentId.ToString("N")).Unprotect(intent.ProtectedPrecheckLink!);
        public async Task Recover()
        {
            var services = new ServiceCollection();
            services.AddScoped<ITenantContext>(_ => new TenantContext());
            services.AddScoped(s => database.Db(s.GetRequiredService<ITenantContext>()));
            services.AddScoped<INotificationService>(s => Service(s.GetRequiredService<HospitalCrmDbContext>(), s.GetRequiredService<ITenantContext>()));
            services.AddSingleton<ITimezoneService, TimezoneService>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var worker = new NotificationRulesWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<NotificationRulesWorker>.Instance);
            await worker.EvaluateAsync(default);
        }
        public async Task DeliverQueued()
        {
            foreach (var saved in Jobs.Jobs)
            {
                var job = InvocationData.DeserializePayload(InvocationData.SerializeJob(saved).SerializePayload()).DeserializeJob();
                await using var db = database.Db(Context);
                await (Task)job.Method.Invoke(Service(db), job.Args.ToArray())!;
            }
        }
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    // The fixture crosses async factory boundaries; its request must not rely on ambient AsyncLocal assignment.
    private sealed class FixtureHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Part2FilesJobs";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
