using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Middlewares;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Infrastructure.Interceptors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Hospital_CRM.Tests;

public sealed class Part3CompletionIsolationFactAttribute : FactAttribute
{
    public Part3CompletionIsolationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PART3_ISOLATION_PORT") != "55450")
            Skip = "Opt-in: identified disposable PG16.15 on owned port 55450 required.";
    }
}

// The journey's fixture is sealed to port 55448. Reuse its signing/search/provider helpers,
// but keep this cluster, migration target, and mutable scenarios independently owned.
internal sealed class Part3CompletionIsolationFixture(ITestOutputHelper output) : IAsyncDisposable
{
    public Guid[] Tenants { get; } = [Guid.NewGuid(), Guid.NewGuid()];
    public Guid[] Clinics { get; } = [Guid.NewGuid(), Guid.NewGuid()];
    public Guid[] Patients { get; } = [Guid.NewGuid(), Guid.NewGuid()];
    public Guid[] Drugs { get; } = [Guid.NewGuid(), Guid.NewGuid()];
    public Dictionary<(int, UserRole), Guid> Actors { get; } = new();
    public Guid Platform { get; } = Guid.NewGuid();
    public string AppConnection { get; private set; } = "";
    public Part2SearchReportSearchTests.SearchTransport Search { get; } = new();
    public string? Address => _client?.BaseAddress?.ToString();
    private readonly Part2IdentitySigningKey _key = new();
    private readonly Part2IdentityEmail _email = new();
    private readonly Part2FilesJobsNotificationTests.TestChannel _channel = new();
    private readonly Part2FilesJobsNotificationTests.CaptureJobs _jobs = new();
    private readonly BlockedProvider _provider = new();
    private string _owner = "";
    private WebApplication? _app;
    private HttpClient? _client, _searchHttp, _providerHttp;
    private int _day;

    public static void ValidateTarget(NpgsqlConnectionStringBuilder target)
    {
        if (target.Host != "127.0.0.1" || target.Port != 55450 || target.Username != "postgres" ||
            target.Database != "postgres" || !string.IsNullOrEmpty(target.Options))
            throw new InvalidOperationException("Only the identified synthetic postgres cluster on 127.0.0.1:55450 is allowed.");
    }

    public async Task CreateAsync()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "part3-completion-isolation");
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "identity.json")));
        var recorded = identity.RootElement;
        var owner = new NpgsqlConnectionStringBuilder("Host=127.0.0.1;Port=55450;Username=postgres;Database=postgres;Pooling=false");
        ValidateTarget(owner);
        await using (var connection = new NpgsqlConnection(owner.ConnectionString))
        {
            await connection.OpenAsync();
            await using var check = new NpgsqlCommand("SELECT current_setting('server_version'), current_setting('data_directory'), system_identifier::text, inet_server_port() FROM pg_control_system()", connection);
            await using (var reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("16.15", reader.GetString(0));
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "data")), Path.GetFullPath(reader.GetString(1)), ignoreCase: true);
                Assert.Equal(recorded.GetProperty("systemId").GetString(), reader.GetString(2));
                Assert.Equal(55450, reader.GetInt32(3));
                Evidence("cluster", new { version = reader.GetString(0), data = reader.GetString(1), systemId = reader.GetString(2), port = reader.GetInt32(3) });
            }
            await using var role = new NpgsqlCommand("DO $$ BEGIN IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='app_user') THEN CREATE ROLE app_user LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION; END IF; END $$;", connection);
            await role.ExecuteNonQueryAsync();
            owner.Database = "part3_isolation_" + Guid.NewGuid().ToString("N");
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{owner.Database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        _owner = owner.ConnectionString;
        owner.Username = "app_user";
        owner.Pooling = true;
        owner.MaxPoolSize = 8;
        AppConnection = owner.ConnectionString;
        await using var db = OwnerDb();
        await db.Database.MigrateAsync();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(56, migrations.Length);
        Assert.Equal(Part3ClinicJourneyDatabase.LastMigration, migrations.Last());
        Assert.False(db.Database.HasPendingModelChanges());
        var source = Environment.GetEnvironmentVariable("PART3_ISOLATION_SOURCE") ?? throw new InvalidOperationException("PART3_ISOLATION_SOURCE is required.");
        var grants = await File.ReadAllTextAsync(Path.Combine(source, "scripts", "setup-least-privilege-db.sql"));
        const string connect = "SELECT format('GRANT CONNECT ON DATABASE %I TO app_user', current_database()) \\gexec";
        Assert.Contains(connect, grants);
        var sql = grants.Replace("\\set ON_ERROR_STOP on", "").Replace(connect, $"GRANT CONNECT ON DATABASE \"{owner.Database}\" TO app_user;");
        Assert.DoesNotContain("\\", sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        for (var i = 0; i < 2; i++)
        {
            db.AddRange(new Tenant { Id = Tenants[i], Name = "Synthetic tenant " + i, SubscriptionStatus = SubscriptionStatus.Active,
                    ActivatedModules = "core,precheck,pharmacy,inventory,finance" },
                new Clinic { Id = Clinics[i], TenantId = Tenants[i], Name = "Synthetic clinic " + i,
                    SubscriptionStatus = SubscriptionStatus.Active, PharmacyLicenceNumber20 = "SYNTHETIC-55450" },
                new TenantTaxProfile { TenantId = Tenants[i], ThresholdRefundApproval = 1000m });
            foreach (var role in new[] { UserRole.Doctor, UserRole.Nurse, UserRole.Receptionist, UserRole.Pharmacist, UserRole.ClinicAdmin })
            {
                var id = Guid.NewGuid();
                Actors[(i, role)] = id;
                db.AddRange(new User { Id = id, TenantId = Tenants[i], ClinicId = Clinics[i], Role = role,
                        Name = "Synthetic " + role, Email = id + "@synthetic.invalid", PasswordHash = "unused" },
                    new TenantMembership { UserId = id, TenantId = Tenants[i], ClinicId = Clinics[i], Role = role,
                        Status = "active", RegistrationNumber = "SYNTHETIC-55450", Qualifications = "Synthetic qualification" });
            }
            db.AddRange(new Patient { Id = Patients[i], TenantId = Tenants[i], Name = "Synthetic Patient " + i,
                    Phone = "9000000050", Uhid = "SYN-" + i, DobHasValue = true, Dob = new(1980, 1, 1), ApproxAge = 46,
                    CreatedBy = Actors[(i, UserRole.Receptionist)] },
                new Drug { Id = Drugs[i], TenantId = Tenants[i], Name = "Synthetic Paracetamol " + i, GenericName = "Paracetamol" });
        }
        db.Users.Add(new User { Id = Platform, Role = UserRole.PlatformAdmin, Name = "Synthetic operator",
            Email = "operator@synthetic.invalid", PasswordHash = "unused" });
        await db.SaveChangesAsync();
        await using var runtime = AppDb(new TenantContext());
        Assert.Equal("app_user", await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await runtime.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        Assert.Empty(await runtime.Patients.IgnoreQueryFilters().ToArrayAsync());
        foreach (var table in new[] { "PatientAuditLogs", "FinanceLedgerEntries", "NotificationLogs", "TenantProvisioningReceipts" })
        {
            var name = "public.\"" + table + "\"";
            Assert.False(await runtime.Database.SqlQuery<bool>($"SELECT has_table_privilege(current_user, {name}, 'UPDATE,DELETE,TRUNCATE') AS \"Value\"").SingleAsync());
        }
        Evidence("database", new { database = owner.Database, migrations, role = "app_user", privileged = false,
            grantsSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grants))), Tenants, Clinics, Patients, Drugs });
    }

    public HospitalCrmDbContext OwnerDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_owner)
        .AddInterceptors(new TenantConnectionInterceptor(TestMaintenanceTenantContext.Instance)).Options, TestMaintenanceTenantContext.Instance);

    public HospitalCrmDbContext AppDb(TenantContext context, string? connection = null) => new(
        new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(connection ?? AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(context)).Options, context);

    public HospitalCrmDbContext AppDb(int tenant)
    {
        var context = new TenantContext();
        context.PushTenant(Tenants[tenant]);
        return AppDb(context);
    }

    public Appointment Visit(int tenant, AppointmentStatus status = AppointmentStatus.CheckedIn) => new()
    {
        Id = Guid.NewGuid(), TenantId = Tenants[tenant], ClinicId = Clinics[tenant], PatientId = Patients[tenant],
        DoctorId = Actors[(tenant, UserRole.Doctor)], Date = new DateOnly(2036, 1, 1).AddDays(Interlocked.Increment(ref _day)),
        TimeSlot = "10:00", Status = status
    };

    public async Task<(string Token, PrecheckSubmission Row)> PrecheckAsync(int tenant = 0, bool expired = false, bool foreignPatient = false, bool foreignAppointment = false)
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var visit = Visit(foreignAppointment ? 1 - tenant : tenant);
        if (foreignPatient) visit.PatientId = Patients[1 - tenant];
        var row = new PrecheckSubmission { Id = Guid.NewGuid(), TenantId = Tenants[tenant], AppointmentId = visit.Id,
            TokenHash = token.HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 24) };
        await using var db = OwnerDb();
        db.AddRange(visit, row);
        await db.SaveChangesAsync();
        return (token, row);
    }

    public async Task<string> SnapshotAsync(int tenant)
    {
        await using var db = AppDb(tenant);
        var snapshot = new SortedDictionary<string, string>();
        foreach (var table in new[] { "Patients", "PatientConsents", "Appointments", "PrecheckSubmissions", "PatientVitals", "Consultations",
            "Prescriptions", "PrescriptionItems", "Invoices", "InvoiceLineItems", "Payments", "FinanceLedgerEntries", "Drugs", "DrugBatches", "DrugStockMovements",
            "DispenseRecords", "DispenseItems", "ControlledSubstanceRegisters", "CreditNotes" })
        {
            // Fixed identifiers only; raw SQL bypasses EF filters so this verifies actual RLS-visible business state.
#pragma warning disable EF1002
            snapshot[table] = await db.Database.SqlQueryRaw<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t.\"Id\"), '[]'::jsonb)::text AS \"Value\" FROM public.\"{table}\" t").SingleAsync();
#pragma warning restore EF1002
        }
        var json = JsonSerializer.Serialize(snapshot);
        Evidence("snapshot", new { tenant = Tenants[tenant], sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))), tables = snapshot });
        return json;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(PatientsController).Assembly.GetName().Name, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Issuer"] = "synthetic-isolation", ["Jwt:Audience"] = "api",
            ["App:BaseUrl"] = "https://synthetic.invalid", ["Razorpay:KeyId"] = "synthetic", ["Razorpay:KeySecret"] = "synthetic" });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<TenantConnectionInterceptor>();
        builder.Services.AddDbContext<HospitalCrmDbContext>((sp, o) => o.UseNpgsql(AppConnection).AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        builder.Services.AddControllers().AddApplicationPart(typeof(PatientsController).Assembly).AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new() { ValidIssuer = "synthetic-isolation", ValidAudience = "api", ValidateLifetime = true,
                ValidateIssuerSigningKey = true, IssuerSigningKey = _key.GetPrivateKey(), ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.Zero, NameClaimType = "sub", RoleClaimType = "role" };
        });
        // Elevated fixture limit permits the malformed/replay matrix without time-based sleeps. Production policy is separate.
        builder.Services.AddRateLimiter(o => { foreach (var name in new[] { "precheck", "queue-status", "auth" })
            o.AddFixedWindowLimiter(name, p => { p.PermitLimit = 200; p.Window = TimeSpan.FromMinutes(5); }); });
        builder.Services.AddScoped<IModuleGateService, ModuleGateService>();
        builder.Services.AddScoped<IImpersonationAuditService, ImpersonationAuditService>();
        builder.Services.AddSingleton<ITimezoneService, TimezoneService>();
        builder.Services.AddScoped<PrecheckService>();
        builder.Services.AddSingleton<IEmailSender>(_email);
        builder.Services.AddSingleton<Hangfire.IBackgroundJobClient>(_jobs);
        builder.Services.AddSingleton<Hangfire.JobStorage>(new Part2FilesJobsNotificationTests.MemoryJobStorage());
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<INotificationChannel>(_channel);
        builder.Services.AddScoped<INotificationService, StubNotificationService>();
        builder.Services.AddSingleton<ICdciMedicineCatalogService>(new MockCdciMedicineCatalogService());
        _searchHttp = new(Search);
        var options = Options.Create(new TypesenseOptions { Host = "synthetic-search.invalid:8108", ApiKey = "synthetic" });
        builder.Services.AddSingleton<IOptions<TypesenseOptions>>(options);
        builder.Services.AddSingleton<ITypesenseClientFactory>(new TypesenseClientFactory(options, new Part2SearchReportSearchTests.ClientFactory(_searchHttp)));
        builder.Services.AddScoped<IPatientSearchService, PatientSearchService>();
        _providerHttp = new(_provider);
        builder.Services.AddScoped(sp => new RazorpayOrderClient(_providerHttp, sp.GetRequiredService<IConfiguration>()));
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseMiddleware<AccountSetupEnforcementMiddleware>();
        _app.UseMiddleware<ImpersonationEnforcementMiddleware>();
        _app.UseMiddleware<SubscriptionEnforcementMiddleware>();
        _app.UseMiddleware<ModuleGateMiddleware>();
        _app.UseAuthorization();
        _app.UseMiddleware<IdempotencyMiddleware>();
        _app.UseMiddleware<InactivityMiddleware>();
        _app.MapControllers();
        await _app.StartAsync();
        _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new(_app.Urls.Single()) };
        Evidence("host", new { address = Address, apiModule = typeof(PatientsController).Module.ModuleVersionId, testModule = GetType().Module.ModuleVersionId,
            controllers = "default MVC activation", authentication = "real RS256 validation, synthetic issuance", outbound = "recorded in-process" });
    }

    public string Token(int tenant = 0, UserRole role = UserRole.ClinicAdmin, IEnumerable<Claim>? extra = null, bool expired = false, bool missingTenant = false)
    {
        var claims = new List<Claim> { new("sub", (role == UserRole.PlatformAdmin ? Platform : Actors[(tenant, role)]).ToString()), new("role", role.ToString()) };
        if (!missingTenant) claims.AddRange([new("tenant_id", Tenants[tenant].ToString()), new("clinicId", Clinics[tenant].ToString())]);
        if (extra != null) claims.AddRange(extra);
        var jwt = new JwtSecurityToken("synthetic-isolation", "api", claims, DateTime.UtcNow.AddHours(-1),
            expired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(20), new SigningCredentials(_key.GetPrivateKey(), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public async Task<JsonElement> SendAsync(string method, string path, string? token, object? body, int expected, string? timezone = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        if (timezone != null) request.Headers.Add("X-Timezone", timezone);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _client!.SendAsync(request);
        if (path.StartsWith("/api/v1/queue-status/", StringComparison.Ordinal))
            Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        if (token != null) text = text.Replace(token, "[synthetic bearer redacted]");
        Evidence("http", new { method, path, timezone, expected, status = (int)response.StatusCode, body = text });
        Assert.True((int)response.StatusCode == expected, $"{method} {path}: expected {expected}, got {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : response.Content.Headers.ContentType?.MediaType?.Contains("json") == true
            ? JsonSerializer.Deserialize<JsonElement>(text) : JsonSerializer.SerializeToElement(text);
    }

    public void Evidence(string kind, object detail) => output.WriteLine(JsonSerializer.Serialize(new { kind, detail }));

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null) await _app.DisposeAsync();
        Evidence("outbound", new { provider = _provider.Attempts, email = _email.Messages.Count, channel = _channel.Messages.Count, jobs = _jobs.Jobs.Count,
            search = Search.Requests.Select(r => new { method = r.Method.Method, r.Uri, r.Body }) });
        _providerHttp?.Dispose();
        _searchHttp?.Dispose();
        _key.Dispose();
        Assert.Equal(0, _provider.Attempts);
        Assert.Empty(_email.Messages);
        Assert.Empty(_channel.Messages);
        Assert.Empty(_jobs.Jobs);
    }

    private sealed class BlockedProvider : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            throw new InvalidOperationException("Provider egress blocked.");
        }
    }
}
