using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Middlewares;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Infrastructure.Interceptors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Hospital_CRM.Tests;

public sealed class Part3PublicTokenLifecycleFactAttribute : FactAttribute
{
    public Part3PublicTokenLifecycleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PART3_PUBLIC_FIX") != "55451")
            Skip = "Opt-in: owned synthetic PG16.15 on port 55451 required.";
    }
}

public sealed class Part3PublicTokenLifecycleTests(Part3PublicTokenLifecycleDatabase f, ITestOutputHelper output)
    : IClassFixture<Part3PublicTokenLifecycleDatabase>
{
    private static readonly object Answers = new { dateOfBirth = "1980-01-01", chiefComplaint = "Synthetic complaint" };

    [Part3PublicTokenLifecycleFact]
    public async Task ActiveToken_UsesItsTenantAcrossAnonymousSuspendedAndPlatformSessions()
    {
        await f.SetStatus(SubscriptionStatus.Active, null, SubscriptionStatus.Suspended);
        var (queue, rx) = await f.SeedReadTokens();
        string? expected = null;
        foreach (var actor in new[] { "anonymous", "other", "platform" })
        {
            var result = await Send("GET", "/api/v1/queue-status/" + queue, actor, 200);
            var queueState = JsonSerializer.Serialize(result.EnumerateObject().Where(p => p.Name != "expiresInSeconds").ToDictionary(p => p.Name, p => p.Value));
            expected ??= queueState;
            Assert.Equal(expected, queueState);
            Assert.InRange(result.GetProperty("expiresInSeconds").GetDouble(), 0, 86400);
            Assert.Equal(new[] { "currentlyServing", "estimatedWaitMinutes", "expiresInSeconds", "position", "status", "yourToken" }, result.EnumerateObject().Select(p => p.Name).Order());
            foreach (var route in new[] { "/api/v1/prescriptions/verify/", "/api/v1/consultations/verify-rx/" })
                Assert.Equal(rx, (await Send("GET", route + rx, actor, 200)).GetProperty("verificationCode").GetString());
            var (token, id) = await f.SeedPrecheck();
            Assert.Equal("valid", (await Send("GET", "/api/v1/precheck/" + token, actor, 200)).GetProperty("message").GetString());
            Assert.Equal("submitted", (await Send("POST", "/api/v1/precheck/" + token, actor, 200, Answers)).GetProperty("message").GetString());
            await using var db = f.OwnerDb();
            var saved = await db.PrecheckSubmissions.SingleAsync(s => s.Id == id);
            Assert.Equal(f.TenantA, saved.TenantId);
            Assert.NotNull(saved.SubmittedAt);
            Assert.Equal("Synthetic complaint", saved.ChiefComplaint);
        }
        Assert.Equal("tenant_suspended", (await Send("GET", "/api/v1/appointments", "other", 403)).GetProperty("error").GetString());
    }

    [Part3PublicTokenLifecycleFact]
    public async Task SuspendedOwner_BlocksPublicReadsAndWritesIncludingPlatformAmbient()
    {
        await f.SetStatus(SubscriptionStatus.Suspended, null, SubscriptionStatus.Active);
        var (queue, rx) = await f.SeedReadTokens();
        foreach (var actor in new[] { "anonymous", "other", "platform" })
        {
            var (token, id) = await f.SeedPrecheck();
            foreach (var route in new[] { "/api/v1/queue-status/" + queue, "/api/v1/precheck/" + token,
                         "/api/v1/prescriptions/verify/" + rx, "/api/v1/consultations/verify-rx/" + rx })
                Assert.Equal("tenant_suspended", (await Send("GET", route, actor, 403)).GetProperty("error").GetString());
            Assert.Equal("tenant_suspended", (await Send("POST", "/api/v1/precheck/" + token, actor, 403, Answers)).GetProperty("error").GetString());
            await AssertUnsubmitted(id);
        }
    }

    [Part3PublicTokenLifecycleFact]
    public async Task ExpiredQueueToken_DoesNotRevealSuspendedOwnerAcrossAmbientSessions()
    {
        await f.SetStatus(SubscriptionStatus.Suspended, null, SubscriptionStatus.Active);
        var (queue, _) = await f.SeedReadTokens();
        await using (var db = f.OwnerDb())
            await db.Appointments.Where(a => a.QueueTrackingToken == queue).ExecuteUpdateAsync(s => s.SetProperty(a => a.QueueTrackingExpiresAt, DateTimeOffset.UtcNow));
        foreach (var actor in new[] { "anonymous", "other", "platform" })
        {
            var expired = await Send("GET", "/api/v1/queue-status/" + queue, actor, 404);
            var unknown = await Send("GET", "/api/v1/queue-status/qtrk_" + Guid.NewGuid().ToString("N"), actor, 404);
            Assert.Equal(unknown.GetRawText(), expired.GetRawText());
        }
    }

    [Part3PublicTokenLifecycleFact]
    public async Task GraceUsesExistingPolicy_ReadsAllowedWritesDeniedThenBlockedWithoutWorker()
    {
        var (token, id) = await f.SeedPrecheck();
        foreach (var status in new[] { SubscriptionStatus.Active, SubscriptionStatus.Trial, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled })
        {
            await f.SetStatus(status, DateTimeOffset.UtcNow.AddDays(-1), SubscriptionStatus.Suspended);
            foreach (var actor in new[] { "anonymous", "other", "platform" })
            {
                await Send("GET", "/api/v1/precheck/" + token, actor, 200);
                Assert.Equal("subscription_read_only", (await Send("POST", "/api/v1/precheck/" + token, actor, 403, Answers)).GetProperty("error").GetString());
            }
            await f.SetStatus(status, DateTimeOffset.UtcNow.AddDays(-4), SubscriptionStatus.Active);
            Assert.Equal("tenant_suspended", (await Send("GET", "/api/v1/precheck/" + token, "anonymous", 403)).GetProperty("error").GetString());
            await AssertUnsubmitted(id);
        }
    }

    [Part3PublicTokenLifecycleFact]
    public async Task PrecheckExpiryIdentitySingleUseAndEntitlement_RemainTokenBound()
    {
        await f.SetStatus(SubscriptionStatus.Active, null, SubscriptionStatus.Suspended);
        foreach (var actor in new[] { "anonymous", "other", "platform" })
        {
            var (expired, expiredId) = await f.SeedPrecheck(expired: true);
            Assert.Equal("token_expired", (await Send("POST", "/api/v1/precheck/" + expired, actor, 400, Answers)).GetProperty("error").GetString());
            await AssertUnsubmitted(expiredId);
            var (token, id) = await f.SeedPrecheck();
            Assert.Equal("identity_verification_failed", (await Send("POST", "/api/v1/precheck/" + token, actor, 400,
                new { dateOfBirth = "1981-01-01", chiefComplaint = "Wrong identity" })).GetProperty("error").GetString());
            await AssertUnsubmitted(id);
            await Send("POST", "/api/v1/precheck/" + token, actor, 200, Answers);
            Assert.Equal("already_submitted", (await Send("POST", "/api/v1/precheck/" + token, actor, 200,
                new { dateOfBirth = "1980-01-01", chiefComplaint = "Must not overwrite" })).GetProperty("message").GetString());
            await using var db = f.OwnerDb();
            Assert.Equal("Synthetic complaint", (await db.PrecheckSubmissions.SingleAsync(s => s.Id == id)).ChiefComplaint);
            await Send("GET", "/api/v1/precheck/malformed", actor, 400);
            await Send("GET", "/api/v1/queue-status/qtrk_" + Guid.NewGuid().ToString("N"), actor, 404);
        }
        var (disabled, disabledId) = await f.SeedPrecheck();
        await using (var db = f.OwnerDb())
            await db.Tenants.Where(t => t.Id == f.TenantA).ExecuteUpdateAsync(s => s.SetProperty(t => t.ActivatedModules, "core"));
        Assert.Equal("module_not_activated", (await Send("POST", "/api/v1/precheck/" + disabled, "other", 403, Answers)).GetProperty("error").GetString());
        await AssertUnsubmitted(disabledId);
    }

    [Part3PublicTokenLifecycleFact]
    public async Task RoutedMiddleware_RestoresAmbientAndOpenSqlConnectionAfterSuccessAndDenial()
    {
        var (token, _) = await f.SeedPrecheck();
        foreach (var suspended in new[] { false, true })
        {
            await f.SetStatus(suspended ? SubscriptionStatus.Suspended : SubscriptionStatus.Active, null, SubscriptionStatus.Suspended);
            var http = new DefaultHttpContext { User = f.Principal("other") };
            http.Request.Method = "GET";
            http.Request.Path = "/api/v1/precheck/" + token;
            http.Request.RouteValues["token"] = token;
            http.Response.Body = new MemoryStream();
            var method = typeof(AppointmentsController).GetMethod(nameof(AppointmentsController.GetPrecheckStatus))!;
            http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(method.GetCustomAttributes(true)), "precheck"));
            var tenant = new TenantContext(new HttpContextAccessor { HttpContext = http });
            using var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
            http.RequestServices = services;
            await using var db = f.AppDb(tenant);
            await db.Database.OpenConnectionAsync();
            var called = false;
            await new SubscriptionEnforcementMiddleware(_ =>
            {
                called = true;
                Assert.Equal(f.TenantA, tenant.CurrentTenantId);
                Assert.False(tenant.IsPlatformAdmin);
                return Task.CompletedTask;
            }, NullLogger<SubscriptionEnforcementMiddleware>.Instance).InvokeAsync(http, db);
            Assert.Equal(!suspended, called);
            Assert.Equal(f.TenantB, tenant.CurrentTenantId);
            Assert.False(tenant.IsPlatformAdmin);
            Assert.Equal(f.TenantB.ToString(), await db.Database.SqlQueryRaw<string>("SELECT current_setting('app.current_tenant_id') AS \"Value\"").SingleAsync());
            Assert.Equal("false", await db.Database.SqlQueryRaw<string>("SELECT current_setting('app.is_platform_admin') AS \"Value\"").SingleAsync());
        }
    }

    private async Task AssertUnsubmitted(Guid id)
    {
        await using var db = f.OwnerDb();
        var saved = await db.PrecheckSubmissions.SingleAsync(s => s.Id == id);
        Assert.Null(saved.SubmittedAt);
        Assert.Null(saved.ChiefComplaint);
    }

    private async Task<JsonElement> Send(string method, string path, string actor, int expected, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (actor != "anonymous") request.Headers.Authorization = new("Bearer", f.Token(actor));
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await f.Client.SendAsync(request);
        if (path.StartsWith("/api/v1/queue-status/", StringComparison.Ordinal)) Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        output.WriteLine(JsonSerializer.Serialize(new { database = f.Database, actor, method, path, expected, status = (int)response.StatusCode, body = text }));
        Assert.True((int)response.StatusCode == expected, $"{actor}: {method} {path}: expected {expected}, got {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
}

public sealed class Part3PublicTokenLifecycleDatabase : IAsyncLifetime
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "part3-completion-public-fix");
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    private readonly Guid _clinic = Guid.NewGuid(), _patient = Guid.NewGuid(), _doctor = Guid.NewGuid();
    private readonly Part2IdentitySigningKey _key = new();
    private string _owner = "", _runtime = "";
    private int _day;
    private WebApplication? _host;
    public HttpClient Client { get; private set; } = null!;
    public string Database { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("PART3_PUBLIC_FIX") != "55451") return;
        var target = new NpgsqlConnectionStringBuilder("Host=127.0.0.1;Port=55451;Database=postgres;Username=postgres;Pooling=false");
        using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root, "identity.json")));
        await using (var connection = new NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_setting('server_version'), current_setting('data_directory'), system_identifier::text, inet_server_port() FROM pg_control_system()", connection);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("16.15", reader.GetString(0));
                Assert.Equal(Path.GetFullPath(Path.Combine(Root, "data")), Path.GetFullPath(reader.GetString(1)), ignoreCase: true);
                Assert.Equal(recorded.RootElement.GetProperty("systemId").GetString(), reader.GetString(2));
                Assert.Equal(55451, reader.GetInt32(3));
            }
            Database = "part3_public_fix_" + Guid.NewGuid().ToString("N");
            command.CommandText = $"CREATE DATABASE \"{Database}\"";
            await command.ExecuteNonQueryAsync();
        }
        target.Database = Database;
        _owner = target.ConnectionString;
        target.Username = "app_user";
        _runtime = target.ConnectionString;
        await using (var db = OwnerDb())
        {
            await db.Database.MigrateAsync();
            Assert.Equal(56, (await db.Database.GetAppliedMigrationsAsync()).Count());
            var source = Environment.GetEnvironmentVariable("PART3_PUBLIC_FIX_SOURCE") ?? throw new InvalidOperationException("Source root required.");
            var grants = await File.ReadAllTextAsync(Path.Combine(source, "scripts", "setup-least-privilege-db.sql"));
            const string connect = "SELECT format('GRANT CONNECT ON DATABASE %I TO app_user', current_database()) \\gexec";
            Assert.Contains(connect, grants);
            await db.Database.ExecuteSqlRawAsync(grants.Replace("\\set ON_ERROR_STOP on", "").Replace(connect, $"GRANT CONNECT ON DATABASE \"{Database}\" TO app_user;"));
            db.AddRange(new Tenant { Id = TenantA, ActivatedModules = "core,precheck", SubscriptionStatus = SubscriptionStatus.Active },
                new Tenant { Id = TenantB, ActivatedModules = "core,precheck", SubscriptionStatus = SubscriptionStatus.Active },
                new Clinic { Id = _clinic, TenantId = TenantA, Name = "Synthetic public clinic" },
                new User { Id = _doctor, TenantId = TenantA, ClinicId = _clinic, Role = UserRole.Doctor, Email = "public-fix@synthetic.invalid", PasswordHash = "unused" },
                new Patient { Id = _patient, TenantId = TenantA, CreatedBy = _doctor, Name = "Synthetic patient", Phone = "9000000051", Uhid = "SYN-55451", DobHasValue = true, Dob = new(1980, 1, 1) });
            await db.SaveChangesAsync();
        }
        await using (var db = AppDb(new TenantContext()))
        {
            Assert.Equal("app_user", await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
            Assert.Empty(await db.Patients.IgnoreQueryFilters().ToArrayAsync());
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ApplicationName = typeof(AppointmentsController).Assembly.GetName().Name, Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<TenantConnectionInterceptor>();
        builder.Services.AddDbContext<HospitalCrmDbContext>((sp, o) => o.UseNpgsql(_runtime).AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        builder.Services.AddControllers().AddApplicationPart(typeof(AppointmentsController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new() { ValidIssuer = "synthetic-public-fix", ValidAudience = "api", IssuerSigningKey = _key.GetPrivateKey(), ValidateIssuerSigningKey = true,
                ValidateLifetime = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256], RoleClaimType = "role", NameClaimType = "sub" };
        });
        builder.Services.AddRateLimiter(o => { foreach (var name in new[] { "precheck", "queue-status" })
            o.AddFixedWindowLimiter(name, p => { p.PermitLimit = 1000; p.Window = TimeSpan.FromMinutes(5); }); });
        builder.Services.AddScoped<IModuleGateService, ModuleGateService>();
        builder.Services.AddScoped<PrecheckService>();
        builder.Services.AddSingleton<ITimezoneService, TimezoneService>();
        builder.Services.AddSingleton<INotificationService, Part2TenantBoundaryTests.NotificationStub>();
        _host = builder.Build();
        _host.UseDeveloperExceptionPage();
        _host.UseRouting();
        _host.UseRateLimiter();
        _host.UseAuthentication();
        _host.UseMiddleware<SubscriptionEnforcementMiddleware>();
        _host.UseMiddleware<ModuleGateMiddleware>();
        _host.UseAuthorization();
        _host.MapControllers();
        await _host.StartAsync();
        Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new(_host.Urls.Single()) };
    }

    public HospitalCrmDbContext OwnerDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_owner)
        .AddInterceptors(new TenantConnectionInterceptor(TestMaintenanceTenantContext.Instance)).Options, TestMaintenanceTenantContext.Instance);
    public HospitalCrmDbContext AppDb(ITenantContext tenant) => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_runtime)
        .AddInterceptors(new TenantConnectionInterceptor(tenant)).Options, tenant);
    public ClaimsPrincipal Principal(string actor) => new(new ClaimsIdentity(actor == "platform"
        ? [new Claim("role", "PlatformAdmin")] : [new Claim("role", "Receptionist"), new Claim("tenant_id", TenantB.ToString())], "synthetic", "sub", "role"));
    public string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("synthetic-public-fix", "api", Principal(actor).Claims,
        DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(20), new SigningCredentials(_key.GetPrivateKey(), SecurityAlgorithms.RsaSha256)));

    public async Task SetStatus(SubscriptionStatus status, DateTimeOffset? end, SubscriptionStatus other)
    {
        await using var db = OwnerDb();
        await db.Tenants.Where(t => t.Id == TenantA).ExecuteUpdateAsync(s => s.SetProperty(t => t.SubscriptionStatus, status)
            .SetProperty(t => t.SubscriptionEndsAt, end).SetProperty(t => t.ActivatedModules, "core,precheck"));
        await db.Tenants.Where(t => t.Id == TenantB).ExecuteUpdateAsync(s => s.SetProperty(t => t.SubscriptionStatus, other));
    }

    private Appointment Visit() => new() { Id = Guid.NewGuid(), TenantId = TenantA, ClinicId = _clinic, PatientId = _patient, DoctorId = _doctor,
        Date = new DateOnly(2036, 1, 1).AddDays(Interlocked.Increment(ref _day)), TimeSlot = "10:00", Status = AppointmentStatus.CheckedIn };
    public async Task<(string Token, Guid Id)> SeedPrecheck(bool expired = false)
    {
        var token = Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var visit = Visit();
        var submission = new PrecheckSubmission { Id = Guid.NewGuid(), TenantId = TenantA, AppointmentId = visit.Id, TokenHash = token.HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1) };
        await using var db = OwnerDb();
        db.AddRange(visit, submission);
        await db.SaveChangesAsync();
        return (token, submission.Id);
    }
    public async Task<(string Queue, string Rx)> SeedReadTokens()
    {
        var visit = Visit();
        visit.QueueToken = 8;
        visit.QueueTrackingToken = "qtrk_" + Guid.NewGuid().ToString("N");
        visit.QueueTrackingExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var code = "RX-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        await using var db = OwnerDb();
        db.AddRange(visit, new Consultation { Id = Guid.NewGuid(), TenantId = TenantA, AppointmentId = visit.Id, DoctorId = _doctor,
            Status = ConsultationStatus.Finalized, VerificationQrCode = code, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (visit.QueueTrackingToken, code);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_host != null) await _host.DisposeAsync();
        _key.Dispose();
    }
}
