using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using Hospital_CRM.Api;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
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

public sealed class Part3IdentityJourneyTests(ITestOutputHelper output)
{
    private const string Password = "SyntheticPassword1!";
    private const string ChangedPassword = "SyntheticChanged2!";

    [Part3IdentityJourneyFact]
    public async Task Migrated_HTTP_provision_accept_select_reset_and_fresh_host_receipt_replay()
    {
        var database = new Part3IdentityJourneyDatabase(output);
        await database.CreateAsync();
        var email = Guid.NewGuid() + "@synthetic.invalid";
        var key = "identity-provision-" + Guid.NewGuid();
        var provision = new ProvisionTenantRequest("Synthetic identity clinic", null, "Synthetic admin", email,
            SubscriptionStatus: "Active", ActivatedModules: ["core", "lab"]);
        JsonElement original;
        Guid tenant, clinic, invite, user;
        string inviteToken;
        await using (var host = await Part3IdentityJourneyHost.StartAsync(database))
        {
            var platform = (await host.SendAsync("POST", "/api/v1/auth/login", new { email = database.PlatformEmail, password = Password }, 200)).Body;
            var platformToken = platform.GetProperty("accessToken").GetString();
            await host.SendAsync("POST", "/api/v1/platform-admin/tenants", provision, 401, key: key);
            await host.SendAsync("POST", "/api/v1/platform-admin/tenants", provision, 400, platformToken);
            original = (await host.SendAsync("POST", "/api/v1/platform-admin/tenants", provision, 201, platformToken, key)).Body;
            tenant = original.GetProperty("tenantId").GetGuid();
            clinic = original.GetProperty("clinicId").GetGuid();
            invite = original.GetProperty("inviteId").GetGuid();
            inviteToken = ReceivedToken(Assert.Single(host.Email.Messages), email, "/accept-invite");
            Assert.Equal(original.GetProperty("inviteToken").GetString(), inviteToken);
            Assert.Equal("handoff", original.GetProperty("emailStatus").GetString());
            Assert.Null(original.GetProperty("emailRetry").GetString());
            var replay = await host.SendAsync("POST", "/api/v1/platform-admin/tenants", provision, 201, platformToken, key);
            Assert.True(replay.Replayed);
            Assert.Equal(original.GetRawText(), replay.Body.GetRawText());
            await host.SendAsync("POST", "/api/v1/platform-admin/tenants", provision with { Name = "Changed synthetic payload" }, 409, platformToken, key);
            await host.SendAsync("POST", "/api/v1/staff/accept-invite", new { inviteToken = "invalid-link", password = Password }, 401);
            user = (await host.SendAsync("POST", "/api/v1/staff/accept-invite", new { inviteToken, password = Password }, 200)).Body.GetProperty("userId").GetGuid();
            await host.SendAsync("POST", "/api/v1/staff/accept-invite", new { inviteToken, password = Password }, 401);

            await using (var db = database.AppDb(tenant))
            {
                var member = await db.TenantMemberships.SingleAsync(m => m.UserId == user);
                Assert.Equal(tenant, member.TenantId);
                Assert.Equal(clinic, member.ClinicId);
                Assert.Equal(UserRole.ClinicAdmin, member.Role);
                Assert.Equal("active", member.Status);
                var account = await db.Users.SingleAsync(u => u.Id == user);
                Assert.True(Password.VerifyPassword(account.PasswordHash));
                Assert.Equal(tenant, account.TenantId);
                Assert.Equal(clinic, account.ClinicId);
                Assert.NotNull((await db.StaffInvites.SingleAsync()).AcceptedAt);
            }
            await database.VerifyClinicRlsAsync(tenant, clinic);
            // A second synthetic membership exercises selection independently of the newly created account's primary role/scope.
            await using (var db = database.AppDb(database.OtherTenant))
            {
                db.TenantMemberships.Add(new TenantMembership { UserId = user, TenantId = database.OtherTenant,
                    ClinicId = database.OtherClinic, Role = UserRole.Nurse, Status = "active" });
                await db.SaveChangesAsync();
            }
            var challenge = (await host.SendAsync("POST", "/api/v1/auth/login", new { email, password = Password }, 200)).Body;
            Assert.True(challenge.GetProperty("requiresTenantSelection").GetBoolean());
            Assert.False(challenge.TryGetProperty("accessToken", out _));
            Assert.Equal(2, challenge.GetProperty("memberships").GetArrayLength());
            var selectionToken = challenge.GetProperty("selectionToken").GetString();
            await host.SendAsync("POST", "/api/v1/auth/select-tenant", new { tenantId = Guid.NewGuid(), selectionToken }, 403);
            var nurse = await host.SendAsync("POST", "/api/v1/auth/select-tenant", new { tenantId = database.OtherTenant, selectionToken }, 200);
            AssertSession(nurse.Body, user, database.OtherTenant, database.OtherClinic, "nurse");
            var nurseToken = nurse.Body.GetProperty("accessToken").GetString();
            var nurseProfile = (await host.SendAsync("GET", "/api/v1/clinic/profile", null, 200, nurseToken)).Body;
            Assert.Equal(database.OtherClinic, nurseProfile.GetProperty("clinicId").GetGuid());
            await host.SendAsync("PUT", "/api/v1/clinic/profile", new { name = "Forbidden change" }, 403, nurseToken);
            var selected = await host.SendAsync("POST", "/api/v1/auth/select-tenant", new { tenantId = tenant, clinicId = clinic }, 200, nurseToken);
            AssertSession(selected.Body, user, tenant, clinic, "clinicadmin");
            var selectedToken = selected.Body.GetProperty("accessToken").GetString();
            Assert.Equal(clinic, (await host.SendAsync("GET", "/api/v1/clinic/profile", null, 200, selectedToken)).Body.GetProperty("clinicId").GetGuid());
            await host.SendAsync("POST", "/api/v1/platform-admin/tenants", provision, 403, selectedToken, key);
            var rotated = await host.SendAsync("POST", "/api/v1/auth/refresh", new { refreshToken = selected.Refresh }, 200);
            AssertSession(rotated.Body, user, tenant, clinic, "clinicadmin");

            var known = await host.SendAsync("POST", "/api/v1/auth/request-password-reset", new { email }, 200);
            var unknown = await host.SendAsync("POST", "/api/v1/auth/request-password-reset", new { email = "unknown@synthetic.invalid" }, 200);
            Assert.Equal(known.Body.GetRawText(), unknown.Body.GetRawText());
            Assert.Single(host.Email.Messages);
            await using (var db = database.AppDb()) Assert.Empty(await db.PasswordResetTokens.ToArrayAsync());
            Assert.Equal(2, host.Jobs.Items.Count);
            foreach (var job in host.Jobs.Items)
            {
                Assert.Equal(typeof(PasswordResetEmailJob), job.Type);
                Assert.DoesNotContain(email, Hangfire.Storage.InvocationData.SerializeJob(job).Arguments);
            }
            await host.DispatchResetJobsAsync();
            Assert.Equal(2, host.Email.Messages.Count);
            var resetToken = ReceivedToken(host.Email.Messages[1], email, "/reset-password");
            await using (var db = database.AppDb())
            {
                var reset = await db.PasswordResetTokens.SingleAsync();
                Assert.Equal(resetToken.HashToken(), reset.TokenHash);
                Assert.Equal((Guid)host.Jobs.Items[0].Args[0], reset.Id);
                Assert.Null(reset.UsedAt);
            }
            await host.SendAsync("POST", "/api/v1/auth/confirm-password-reset", new { resetToken, newPassword = "short" }, 400);
            await host.SendAsync("POST", "/api/v1/auth/confirm-password-reset", new { resetToken, newPassword = ChangedPassword }, 200);
            await host.SendAsync("POST", "/api/v1/auth/confirm-password-reset", new { resetToken, newPassword = ChangedPassword }, 400);
            foreach (var refresh in new[] { nurse.Refresh, selected.Refresh, rotated.Refresh })
                await host.SendAsync("POST", "/api/v1/auth/refresh", new { refreshToken = refresh }, 401);
            await host.SendAsync("POST", "/api/v1/auth/select-tenant", new { tenantId = tenant }, 401, selectedToken);
            await host.SendAsync("POST", "/api/v1/auth/select-tenant", new { tenantId = tenant, selectionToken }, 401);
            await host.SendAsync("POST", "/api/v1/auth/login", new { email, password = Password, tenantId = tenant }, 401);
            await using (var db = database.AppDb())
            {
                var sessions = await db.RefreshTokens.Where(t => t.UserId == user).ToArrayAsync();
                Assert.Equal(3, sessions.Length);
                Assert.All(sessions, session => Assert.NotNull(session.RevokedAt));
                Assert.NotNull((await db.PasswordResetTokens.SingleAsync()).UsedAt);
                database.Evidence("reset-revocation", new { user, resetId = (Guid)host.Jobs.Items[0].Args[0],
                    sessions = sessions.Select(t => new { t.Id, t.FamilyId, t.TenantId, t.ClinicId, t.RevokedAt }), live = 0 });
            }
            AssertSession((await host.SendAsync("POST", "/api/v1/auth/login", new { email, password = ChangedPassword, tenantId = tenant }, 200)).Body,
                user, tenant, clinic, "clinicadmin");
        }

        // Independent DI/cache/signing-key/provider instances; only the DB and on-disk Data Protection key ring survive.
        await using (var fresh = await Part3IdentityJourneyHost.StartAsync(database))
        {
            var platform = (await fresh.SendAsync("POST", "/api/v1/auth/login", new { email = database.PlatformEmail, password = Password }, 200)).Body;
            var token = platform.GetProperty("accessToken").GetString();
            var replay = await fresh.SendAsync("POST", "/api/v1/platform-admin/tenants", provision, 201, token, key);
            Assert.True(replay.Replayed);
            Assert.Equal(original.GetRawText(), replay.Body.GetRawText());
            await fresh.SendAsync("POST", "/api/v1/platform-admin/tenants", provision with { ClinicName = "Changed" }, 409, token, key);
            await fresh.SendAsync("POST", "/api/v1/staff/accept-invite", new { inviteToken, password = Password }, 401);
            AssertSession((await fresh.SendAsync("POST", "/api/v1/auth/login", new { email, password = ChangedPassword, tenantId = tenant }, 200)).Body,
                user, tenant, clinic, "clinicadmin");
            Assert.Empty(fresh.Email.Messages);
            Assert.Empty(fresh.Jobs.Items);
        }
        TenantProvisioningReceipt receipt;
        await using (var admin = database.AppDb(maintenance: true))
            receipt = await admin.TenantProvisioningReceipts.SingleAsync();
        await using (var db = database.AppDb(tenant))
        {
            Assert.Empty(await db.TenantProvisioningReceipts.ToArrayAsync());
            Assert.Equal(database.Platform, receipt.ActorId);
            Assert.Equal(tenant, receipt.TenantId);
            Assert.Equal(key, receipt.IdempotencyKey);
            Assert.DoesNotContain(inviteToken, receipt.ProtectedResponse);
            Assert.Single(await db.Clinics.ToArrayAsync());
            Assert.Single(await db.TenantFeatureFlags.ToArrayAsync());
            var invitation = await db.StaffInvites.SingleAsync();
            Assert.Equal(invite, invitation.Id);
            Assert.Equal(inviteToken.HashToken(), invitation.TokenHash);
            Assert.NotNull(invitation.AcceptedAt);
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));
            Assert.Equal(2, await db.TenantMemberships.CountAsync(m => m.UserId == user));
            Assert.True(ChangedPassword.VerifyPassword((await db.Users.SingleAsync(u => u.Id == user)).PasswordHash));
            Assert.NotNull((await db.PasswordResetTokens.SingleAsync()).UsedAt);
            var sessions = await db.RefreshTokens.Where(t => t.UserId == user).ToArrayAsync();
            Assert.Equal(5, sessions.Length);
            Assert.Equal(2, sessions.Count(t => t.RevokedAt == null));
            database.Evidence("persisted", new { tenant, clinic, invite, user, receipt = receipt.Id,
                database.Platform, database.OtherTenant, database.OtherClinic, invitation.AcceptedAt,
                receipts = 1, invitations = 1, invitedUsers = 1, memberships = 2, resetTokens = 1,
                refreshRows = sessions.Length, liveRefreshRows = 2, revokedRefreshRows = 3,
                keys = Directory.GetFiles(database.Keys.FullName, "key-*.xml").Select(Path.GetFileName) });
        }
    }

    private static string ReceivedToken((string To, string Body) message, string email, string path)
    {
        Assert.Equal(email, message.To);
        var link = new Uri(Regex.Match(message.Body, @"https://[^\s]+").Value);
        Assert.Equal("synthetic.invalid", link.Host);
        Assert.Equal(path, link.AbsolutePath);
        return QueryHelpers.ParseQuery(link.Query)["token"].ToString();
    }

    private static void AssertSession(JsonElement body, Guid user, Guid tenant, Guid clinic, string role)
    {
        Assert.Equal(tenant, body.GetProperty("tenantId").GetGuid());
        Assert.Equal(clinic, body.GetProperty("clinicId").GetGuid());
        Assert.Equal(user, body.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(role, body.GetProperty("user").GetProperty("role").GetString());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.Equal(user.ToString(), jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal(tenant.ToString(), jwt.Claims.Single(c => c.Type == "tenant_id").Value);
        Assert.Equal(clinic.ToString(), jwt.Claims.Single(c => c.Type == "clinicId").Value);
        Assert.Equal(role, jwt.Claims.Single(c => c.Type == "role").Value, ignoreCase: true);
        Assert.True(Guid.TryParse(jwt.Claims.Single(c => c.Type == "session_id").Value, out _));
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "clinic_id");
    }
}

public sealed class Part3IdentityJourneyFactAttribute : FactAttribute
{
    public Part3IdentityJourneyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PART3_IDENTITY_JOURNEY_PORT") != "55449")
            Skip = "Opt-in: identified isolated PG16 on port 55449 required.";
    }
}

internal sealed class Part3IdentityJourneyDatabase(ITestOutputHelper output)
{
    public Guid Platform { get; } = Guid.NewGuid();
    public string PlatformEmail => Platform + "@synthetic.invalid";
    public Guid OtherTenant { get; } = Guid.NewGuid();
    public Guid OtherClinic { get; } = Guid.NewGuid();
    public string AppConnection { get; private set; } = "";
    public DirectoryInfo Keys { get; private set; } = null!;

    public async Task CreateAsync()
    {
        var owner = new NpgsqlConnectionStringBuilder(Required("OWNER"));
        ValidateTarget(owner);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "part3-completion-identity");
        var data = Path.GetFullPath(Required("DATA"));
        Assert.StartsWith(root + Path.DirectorySeparatorChar + "pg-", data, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.DirectorySeparatorChar + "data", data, StringComparison.OrdinalIgnoreCase);
        owner.Pooling = false;
        await using (var connection = new NpgsqlConnection(owner.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::int, current_setting('data_directory'), system_identifier::text, inet_server_port() FROM pg_control_system()", connection);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.InRange(reader.GetInt32(0), 160000, 169999);
                Assert.Equal(data, Path.GetFullPath(reader.GetString(1)), ignoreCase: true);
                Assert.Equal(Required("SYSTEM_ID"), reader.GetString(2));
                Assert.Equal(55449, reader.GetInt32(3));
                Evidence("postgres", new { version = reader.GetInt32(0), data, systemId = reader.GetString(2), port = 55449 });
            }
            await using var role = new NpgsqlCommand("DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_user') THEN CREATE ROLE app_user LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION; END IF; END $$;", connection);
            await role.ExecuteNonQueryAsync();
            owner.Database = "part3_identity_journey_" + Guid.NewGuid().ToString("N");
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{owner.Database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        Keys = new DirectoryInfo(Path.Combine(root, "keys", owner.Database));
        Keys.Create();
        await using var db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseNpgsql(owner.ConnectionString).AddInterceptors(new TenantConnectionInterceptor(TestMaintenanceTenantContext.Instance)).Options,
            TestMaintenanceTenantContext.Instance);
        owner.Username = "app_user";
        owner.Password = null;
        AppConnection = owner.ConnectionString;
        Evidence("database", new { database = owner.Database, runtime = owner.Username, keyDirectory = Keys.FullName });
        Assert.Equal(Part3ClinicJourneyDatabase.LastMigration, db.Database.GetMigrations().Last());
        await db.Database.MigrateAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(56, applied.Length);
        Assert.Equal(Part3ClinicJourneyDatabase.LastMigration, applied.Last());
        Assert.False(db.Database.HasPendingModelChanges());
        var grants = await File.ReadAllTextAsync(Path.Combine(Required("SOURCE"), "scripts", "setup-least-privilege-db.sql"));
        const string connect = "SELECT format('GRANT CONNECT ON DATABASE %I TO app_user', current_database()) \\gexec";
        Assert.Contains(connect, grants);
        var sql = grants.Replace("\\set ON_ERROR_STOP on", "").Replace(connect, $"GRANT CONNECT ON DATABASE \"{owner.Database}\" TO app_user;");
        Assert.DoesNotContain("\\", sql);
        await db.Database.ExecuteSqlRawAsync(sql);
        Evidence("migration-chain", new { applied, grantsSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grants))) });
        db.AddRange(new User { Id = Platform, Email = PlatformEmail, Name = "Synthetic platform", Role = UserRole.PlatformAdmin,
                PasswordHash = "SyntheticPassword1!".HashPassword() },
            new Tenant { Id = OtherTenant, Name = "Synthetic secondary", SubscriptionStatus = SubscriptionStatus.Active },
            new Clinic { Id = OtherClinic, TenantId = OtherTenant, Name = "Synthetic secondary", SubscriptionStatus = SubscriptionStatus.Active });
        await db.SaveChangesAsync();
        await using var runtime = AppDb();
        Assert.Equal("app_user", await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await runtime.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        foreach (var table in new[] { "AuthAuditLogs", "TenantProvisioningReceipts" })
        {
            var relation = "public.\"" + table + "\"";
            Assert.False(await runtime.Database.SqlQuery<bool>($"SELECT has_table_privilege(current_user, {relation}, 'UPDATE,DELETE,TRUNCATE') AS \"Value\"").SingleAsync());
        }
        Assert.True(await runtime.Database.SqlQueryRaw<bool>("SELECT has_column_privilege(current_user, 'public.\"TenantProvisioningReceipts\"', 'ProtectedResponse', 'UPDATE') AS \"Value\"").SingleAsync());
        Evidence("runtime-privileges", new { role = "app_user", privileged = false, authAuditAppendOnly = true, receiptUpdateColumn = "ProtectedResponse" });
    }

    public async Task VerifyClinicRlsAsync(Guid tenant, Guid clinic)
    {
        foreach (var scope in new Guid?[] { null, OtherTenant, tenant })
        {
            await using var db = AppDb(scope);
            var count = await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.\"Clinics\" WHERE \"Id\" = {clinic}").SingleAsync();
            Assert.Equal(scope == tenant ? 1L : 0L, count);
            Evidence("rls", new { scope, clinic, visible = count });
        }
    }

    public HospitalCrmDbContext AppDb(Guid? scope = null, bool maintenance = false)
    {
        var tenant = new TenantContext();
        if (scope.HasValue) tenant.PushTenant(scope.Value);
        if (maintenance) tenant.PushMaintenance();
        return new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant)).Options, tenant);
    }
    internal static void ValidateTarget(NpgsqlConnectionStringBuilder target)
    {
        if (target.Host != "127.0.0.1" || target.Port != 55449 || target.Database != "postgres" || target.Username != "postgres" || !string.IsNullOrEmpty(target.Options))
            throw new InvalidOperationException("Only the identified isolated postgres cluster on 127.0.0.1:55449 is allowed.");
    }
    private static string Required(string key) => Environment.GetEnvironmentVariable("PART3_IDENTITY_JOURNEY_" + key)
        is { Length: > 0 } value ? value : throw new InvalidOperationException(key + " is required.");
    public void Evidence(string kind, object detail) => output.WriteLine(JsonSerializer.Serialize(new { kind, detail }));
}

// Same minimal Kestrel/RLS pipeline as Part3ClinicJourneyHost; reuse existing identity fakes without changing that frozen host.
internal sealed class Part3IdentityJourneyHost(Part3IdentityJourneyDatabase database) : IAsyncDisposable
{
    public readonly Part2IdentityEmail Email = new();
    public readonly Part2IdentityJobs Jobs = new();
    private readonly Part2IdentitySigningKey _key = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public static async Task<Part3IdentityJourneyHost> StartAsync(Part3IdentityJourneyDatabase database)
    {
        var host = new Part3IdentityJourneyHost(database);
        try { await host.StartCoreAsync(); return host; }
        catch { await host.DisposeAsync(); throw; }
    }

    private async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(AuthController).Assembly.GetName().Name, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Jwt:Issuer"] = "synthetic-identity", ["Jwt:Audience"] = "api", ["Jwt:KeyId"] = _key.GetPublicKey().KeyId,
            ["App:PublicBaseUrl"] = "https://synthetic.invalid", ["Mfa:RequireEnrolment"] = "false" });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<TenantConnectionInterceptor>();
        builder.Services.AddDbContext<HospitalCrmDbContext>((sp, o) => o.UseNpgsql(database.AppConnection)
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IPersistentKeyService>(_key);
        builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerOptionsConfig>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = "synthetic-identity", ValidateAudience = true,
                ValidAudience = "api", ValidateLifetime = true, ValidateIssuerSigningKey = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.Zero, NameClaimType = "sub", RoleClaimType = "role" };
        });
        builder.Services.AddScoped<IModuleGateService, ModuleGateService>();
        builder.Services.AddScoped<IImpersonationAuditService, ImpersonationAuditService>();
        builder.Services.AddSingleton<ISecretProtector>(new TestSecretProtector());
        builder.Services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(database.Keys,
            o => o.SetApplicationName("Part3IdentityJourney")));
        builder.Services.AddSingleton<IEmailSender>(Email);
        builder.Services.AddSingleton<IBackgroundJobClient>(Jobs);
        builder.Services.AddScoped<PasswordResetEmailJob>();
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
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
        _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { BaseAddress = new(_app.Urls.Single()) };
        database.Evidence("http-host", new { address = _client.BaseAddress, runtime = "app_user", auth = "AuthController issuance and RS256 JwtBearer validation",
            apiModule = typeof(AuthController).Module.ModuleVersionId, testModule = GetType().Module.ModuleVersionId,
            signingKeyThumbprint = _key.GetPublicKey().ComputeJwkThumbprint(), dataProtection = database.Keys.FullName });
    }

    public async Task<(JsonElement Body, string? Refresh, bool Replayed)> SendAsync(string method, string path, object? body, int expected,
        string? token = null, string? key = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        if (key != null) request.Headers.Add("X-Idempotency-Key", key);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _client!.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var replayed = response.Headers.TryGetValues("X-Idempotency-Replay", out var replay) && replay.Contains("true");
        var cached = response.Headers.TryGetValues("X-Cache-Lookup", out var cache) && cache.Contains("HIT");
        database.Evidence("http", new { method, path, expected, status = (int)response.StatusCode, body = text, replayed, cached });
        Assert.True((int)response.StatusCode == expected, $"{method} {path}: expected {expected}, got {(int)response.StatusCode}: {text}");
        Assert.False(cached);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.SingleOrDefault(c => c.StartsWith("refreshToken=")) : null;
        var json = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("accessToken", out _)) Assert.NotNull(cookie);
        if (cookie != null)
        {
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        }
        return (json, cookie?.Split(';')[0]["refreshToken=".Length..], replayed);
    }

    public async Task DispatchResetJobsAsync()
    {
        foreach (var job in Jobs.Items)
        {
            await using var scope = _app!.Services.CreateAsyncScope();
            var worker = scope.ServiceProvider.GetRequiredService<PasswordResetEmailJob>();
            await worker.SendAsync((Guid)job.Args[0], (string)job.Args[1], default);
            database.Evidence("job-dispatch", new { requestId = job.Args[0], method = job.Method.Name, recordedEmails = Email.Messages.Count });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null) await _app.DisposeAsync();
        database.Evidence("outbound-capture", new { emails = Email.Messages.Count, jobs = Jobs.Items.Count,
            transport = "existing in-process identity fakes; no SMTP/provider client registered" });
        _key.Dispose();
    }
}

public sealed class Part3IdentityJourneyGuardTests
{
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Username=postgres;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55446;Username=postgres;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55448;Username=postgres;Database=postgres")]
    [InlineData("Host=localhost;Port=55449;Username=postgres;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55449;Username=app_user;Database=postgres")]
    [InlineData("Host=127.0.0.1;Port=55449;Username=postgres;Database=hospital_crm")]
    [InlineData("Host=127.0.0.1;Port=55449;Username=postgres;Database=postgres;Options=-c role=app_user")]
    public void Rejects_other_targets_before_connecting(string input) =>
        Assert.Throws<InvalidOperationException>(() => Part3IdentityJourneyDatabase.ValidateTarget(new(input)));
}
