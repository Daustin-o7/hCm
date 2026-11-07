using System.Data.Common;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Xunit;
using Xunit.Abstractions;

namespace Hospital_CRM.Tests;

public sealed class Part2IdentityPostgresTheoryAttribute : TheoryAttribute
{
    public Part2IdentityPostgresTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PART3_IDENTITY_TEST_OWNER")))
            Skip = "Requires a disposable PostgreSQL cluster on 127.0.0.1:55443 (PART3_IDENTITY_TEST_OWNER).";
    }
}

public sealed class Part2IdentityPostgresTests(Part2IdentityPostgresFixture fixture, ITestOutputHelper output)
    : IClassFixture<Part2IdentityPostgresFixture>
{
    private const string Password = "LongPassword1!";
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Mfa:RequireEnrolment"] = "false" }).Build();

    [Part2IdentityPostgresTheory]
    [InlineData("logout", true)]
    [InlineData("logout", false)]
    [InlineData("reset", true)]
    [InlineData("reset", false)]
    [InlineData("revoke-all", true)]
    [InlineData("revoke-all", false)]
    [InlineData("oidc-logout", true)]
    [InlineData("oidc-logout", false)]
    [InlineData("password", true)]
    [InlineData("password", false)]
    [InlineData("mfa", true)]
    [InlineData("mfa", false)]
    [InlineData("inactivity", true)]
    [InlineData("inactivity", false)]
    [InlineData("suspend", true)]
    [InlineData("suspend", false)]
    [InlineData("cli-mfa", true)]
    [InlineData("cli-mfa", false)]
    public async Task RotationAndRevocationSerializeOnTheUser(string operation, bool rotationFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        var seed = await SeedAsync(operation == "cli-mfa");
        using var key = new Part2IdentitySigningKey();
        var holder = new LockGate(pause: true);
        var waiter = new LockGate(pause: false);
        var rotationContext = new TenantContext();
        var revokeHttp = Http(seed);
        if (operation == "suspend") revokeHttp.User = new(new ClaimsIdentity(
            [new Claim("sub", seed.User.ToString()), new Claim("role", "PlatformAdmin")], "test", "sub", "role"));
        var revokeContext = new TenantContext(new HttpContextAccessor { HttpContext = revokeHttp });
        await using var rotationDb = fixture.AppDb(rotationContext, rotationFirst ? holder : waiter);
        await using var revokeDb = fixture.AppDb(revokeContext, rotationFirst ? waiter : holder);
        // Pretracked snapshots must not resurrect the old credentials after waiting for the lock.
        await rotationDb.RefreshTokens.Include(t => t.User).SingleAsync(t => t.UserId == seed.User, ct);
        var rotation = Auth(rotationDb, rotationContext, key, new DefaultHttpContext());
        var revoker = Auth(revokeDb, revokeContext, key, revokeHttp);

        Task<IActionResult> Rotate() => rotation.Refresh(new(seed.Refresh), ct);
        async Task<IActionResult> Revoke()
        {
            switch (operation)
            {
                case "logout": return await revoker.Logout(new(seed.Refresh), ct);
                case "reset": return await new PasswordResetController(revokeDb, NullLogger<PasswordResetController>.Instance)
                    { ControllerContext = new() { HttpContext = revokeHttp } }.ConfirmReset(new(seed.Reset, "ChangedPassword2!"), ct);
                case "revoke-all": return await revoker.RevokeAllSessions(ct);
                case "oidc-logout": return await Oidc(revokeDb, revokeContext, revokeHttp).LogOut();
                case "password": return await revoker.ChangePassword(new(Password, "ChangedPassword2!"), ct);
                case "mfa": return await revoker.EnableMfa(new(TotpService.ComputeCode(Secret, DateTimeOffset.UtcNow), "totp"), ct);
                case "cli-mfa":
                    // Invoke only the real maintenance branch, without starting Program's host/providers.
                    var command = typeof(AuthController).Assembly.GetType("Program")!.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                        .Single(m => m.Name.Contains("g__ResetPlatformAdminMfaAsync|", StringComparison.Ordinal));
                    var exit = await (Task<int>)command.Invoke(null, [revokeDb, new[] { "--reset-mfa", seed.User + "@synthetic.invalid" }])!;
                    Assert.Equal(0, exit);
                    return new OkObjectResult(exit);
                case "suspend": return await new PlatformAdminController(revokeDb, key, Config, null!)
                    { ControllerContext = new() { HttpContext = revokeHttp } }.UpdateTenant(seed.Tenant,
                        new(null, "Suspended", null, null), ct);
                case "inactivity":
                    await using (var services = new ServiceCollection().AddScoped(_ => revokeDb).BuildServiceProvider())
                    using (var cache = new MemoryCache(new MemoryCacheOptions()))
                    {
                        revokeHttp.RequestServices = services;
                        revokeHttp.Request.Path = "/api/v1/patients";
                        revokeHttp.RequestAborted = ct;
                        await new InactivityMiddleware(_ => Task.CompletedTask).InvokeAsync(revokeHttp, cache);
                        return new StatusCodeResult(revokeHttp.Response.StatusCode);
                    }
                default: throw new InvalidOperationException(operation);
            }
        }

        var first = rotationFirst ? Rotate() : Revoke();
        Task<IActionResult>? second = null;
        try
        {
            var holderPid = await holder.Held.Task.WaitAsync(ct);
            second = rotationFirst ? Revoke() : Rotate();
            var waiterPid = await waiter.Attempted.Task.WaitAsync(ct);
            await fixture.AssertBlockedAsync(holderPid, waiterPid, ct);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            output.WriteLine($"{operation}, rotationFirst={rotationFirst}: PostgreSQL PID {waiterPid} blocked by {holderPid} on Users row.");
        }
        finally { holder.Release.TrySetResult(); }
        var results = await Task.WhenAll(first, second!).WaitAsync(ct);
        var rotated = results[rotationFirst ? 0 : 1];
        var revoked = results[rotationFirst ? 1 : 0];
        if (rotationFirst) Assert.IsType<OkObjectResult>(rotated);
        else Assert.IsType<UnauthorizedObjectResult>(rotated);
        var activityPreserved = operation == "inactivity" && rotationFirst;
        if (operation == "inactivity") Assert.Equal(activityPreserved ? 200 : 401, Assert.IsType<StatusCodeResult>(revoked).StatusCode);
        else if (operation == "oidc-logout") Assert.IsType<SignOutResult>(revoked);
        else Assert.IsType<OkObjectResult>(revoked);

        await using var evidence = fixture.OwnerDb();
        var family = await evidence.RefreshTokens.Where(t => t.UserId == seed.User && t.FamilyId == seed.Family).ToListAsync(ct);
        Assert.Equal(rotationFirst ? 2 : 1, family.Count);
        if (activityPreserved) Assert.Single(family, t => t.RevokedAt is null);
        else Assert.All(family, token => Assert.NotNull(token.RevokedAt));
        output.WriteLine($"Persisted old-family rows={family.Count}, live={family.Count(t => t.RevokedAt is null)}; database={fixture.Database}.");
        if (operation == "cli-mfa")
        {
            var account = await evidence.Users.SingleAsync(u => u.Id == seed.User, ct);
            Assert.False(account.MfaEnabled);
            Assert.Null(account.MfaSecret);
            Assert.Null(account.MfaMethod);
        }

        var checkContext = new TenantContext();
        await using var check = fixture.AppDb(checkContext);
        if (!activityPreserved)
        {
            Assert.IsType<ForbidResult>(await Oidc(check, checkContext, Http(seed)).Exchange());
            Assert.IsType<UnauthorizedObjectResult>(await Auth(check, checkContext, key, new DefaultHttpContext()).Refresh(
                new(rotationFirst ? Cookie(rotation) : seed.Refresh), ct));
        }
        else Assert.IsType<SignInResult>(await Oidc(check, checkContext, Http(seed)).Exchange());

        if (operation is "password" or "mfa")
        {
            var fresh = Assert.Single(await evidence.RefreshTokens.Where(t => t.UserId == seed.User && t.RevokedAt == null).ToListAsync(ct));
            Assert.NotEqual(seed.Family, fresh.FamilyId);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(JsonSerializer.SerializeToElement(
                Assert.IsType<OkObjectResult>(revoked).Value).GetProperty("accessToken").GetString());
            var http = new DefaultHttpContext { User = new(new ClaimsIdentity(jwt.Claims, "test", "sub", "role")) };
            Assert.IsType<SignInResult>(await Oidc(check, checkContext, http).Exchange());
        }
    }

    [Part2IdentityPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousLoginAndRotationUseExplicitMembershipUnderClinicRls(bool platform)
    {
        var seed = await SeedAsync(platform);
        if (platform)
        {
            await using var owner = fixture.OwnerDb();
            var user = await owner.Users.SingleAsync(u => u.Id == seed.User);
            user.MfaEnabled = false;
            await owner.SaveChangesAsync();
        }
        var context = new TenantContext();
        await using var db = fixture.AppDb(context);
        Assert.Empty(await db.Clinics.IgnoreQueryFilters().ToListAsync());
        using var key = new Part2IdentitySigningKey();
        var controller = Auth(db, context, key, new DefaultHttpContext());
        var issued = Assert.IsType<OkObjectResult>(await controller.Login(new(seed.User + "@synthetic.invalid", Password), default));
        var body = JsonSerializer.SerializeToElement(issued.Value);
        Assert.Equal(platform ? null : seed.Clinic.ToString(), body.GetProperty("clinicId").GetString());
        Assert.IsType<OkObjectResult>(await controller.Refresh(new(Cookie(controller)), default));
        Assert.Null(context.CurrentTenantId);
        Assert.False(context.IsPlatformAdmin);
        Assert.Empty(await db.Clinics.IgnoreQueryFilters().ToListAsync());
    }

    private async Task<Seed> SeedAsync(bool platform = false)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), platform);
        await using var db = fixture.OwnerDb();
        db.AddRange(new Tenant { Id = seed.Tenant, SubscriptionStatus = SubscriptionStatus.Active },
            new Clinic { Id = seed.Clinic, TenantId = seed.Tenant },
            new User { Id = seed.User, TenantId = platform ? null : seed.Tenant, ClinicId = platform ? null : seed.Clinic, Email = seed.User + "@synthetic.invalid",
                Role = platform ? UserRole.PlatformAdmin : UserRole.Doctor, PasswordHash = Password.HashPassword(),
                MfaEnabled = platform, MfaMethod = platform ? "totp" : null, MfaSecret = new TestSecretProtector().Protect(Secret) },
            new TenantMembership { UserId = seed.User, TenantId = seed.Tenant, ClinicId = seed.Clinic, Role = UserRole.Doctor },
            new RefreshToken { UserId = seed.User, FamilyId = seed.Family, TenantId = platform ? null : seed.Tenant, ClinicId = platform ? null : seed.Clinic,
                Role = platform ? UserRole.PlatformAdmin : UserRole.Doctor, TokenHash = seed.Refresh.HashToken(), IssuedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastUsedAt = DateTimeOffset.UtcNow.AddHours(-1), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) },
            new PasswordResetToken { UserId = seed.User, TokenHash = seed.Reset.HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) });
        await db.SaveChangesAsync();
        return seed;
    }

    private sealed record Seed(Guid User, Guid Tenant, Guid Clinic, Guid Family, string Refresh, string Reset, bool Platform);
    private static DefaultHttpContext Http(Seed seed)
    {
        var claims = new List<Claim> { new("sub", seed.User.ToString()), new("session_id", seed.Family.ToString()),
            new("role", seed.Platform ? "PlatformAdmin" : "Doctor") };
        if (seed.Platform) claims.Add(new("mfa_enabled", "true"));
        else claims.AddRange([new("tenant_id", seed.Tenant.ToString()), new("clinicId", seed.Clinic.ToString())]);
        return new() { Response = { Body = new MemoryStream() }, User = new(new ClaimsIdentity(claims, "test", "sub", "role")) };
    }
    private static AuthController Auth(HospitalCrmDbContext db, TenantContext context, Part2IdentitySigningKey key, HttpContext http) =>
        new(db, Config, key, new Part2IdentityEnvironment(), new TestSecretProtector(), new Part2IdentityEmail(), context)
        { ControllerContext = new() { HttpContext = http } };
    private static AuthorizationController Oidc(HospitalCrmDbContext db, TenantContext context, HttpContext http)
    {
        http.RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(new Part2IdentityAuthentication(http.User)).BuildServiceProvider();
        http.Features.Set(new OpenIddictServerAspNetCoreFeature { Transaction = new OpenIddictServerTransaction
            { Request = new OpenIddictRequest { GrantType = "refresh_token" } } });
        return new(db, null!, null!, Config, context) { ControllerContext = new() { HttpContext = http } };
    }
    private static string Cookie(AuthController controller) => controller.Response.Headers.SetCookie
        .Last(h => h!.StartsWith("refreshToken="))!.Split(';')[0]["refreshToken=".Length..];

    private sealed class LockGate(bool pause) : DbCommandInterceptor
    {
        public readonly TaskCompletionSource<int> Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<int> Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE")) Attempted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE") && Held.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID) && pause)
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }
}

public sealed class Part2IdentityPostgresFixture : IAsyncLifetime
{
    private string _owner = "";
    private string _app = "";
    public string Database => new NpgsqlConnectionStringBuilder(_owner).Database!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("PART3_IDENTITY_TEST_OWNER");
        if (string.IsNullOrEmpty(connection)) return;
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (builder.Host != "127.0.0.1" || builder.Port != 55443 || builder.Username != "postgres" ||
            builder.Database?.StartsWith("part3_identity_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Use only a disposable part3_identity_* database on 127.0.0.1:55443 as postgres.");
        builder.Database = "part3_identity_" + Guid.NewGuid().ToString("N");
        builder.Pooling = false;
        _owner = builder.ConnectionString;
        builder.Username = "app_user";
        builder.Password = null;
        _app = builder.ConnectionString;
        await using var db = OwnerDb();
        Assert.True(await db.Database.EnsureCreatedAsync(), "Must create a fresh synthetic database.");
        await db.Database.ExecuteSqlRawAsync("""
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO app_user;
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_user;
            REVOKE UPDATE, DELETE ON "AuthAuditLogs" FROM app_user;
            """);
        foreach (var operation in new Hospital_CRM.Infrastructure.Migrations.EnableRowLevelSecurityForTenants().UpOperations.Cast<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
        var context = new TenantContext();
        await using var app = AppDb(context);
        Assert.Equal("app_user", await app.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.False(await app.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync());
        Assert.Empty(await app.Clinics.IgnoreQueryFilters().ToListAsync());
    }
    public HospitalCrmDbContext OwnerDb() => new(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_owner).Options,
        TestMaintenanceTenantContext.Instance);
    public HospitalCrmDbContext AppDb(TenantContext context, IInterceptor? gate = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseNpgsql(_app).AddInterceptors(new TenantConnectionInterceptor(context));
        if (gate is not null) options.AddInterceptors(gate);
        return new(options.Options, context);
    }
    public async Task AssertBlockedAsync(int holder, int waiter, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_owner);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", connection);
        command.Parameters.AddWithValue("holder", holder);
        command.Parameters.AddWithValue("waiter", waiter);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return;
            await Task.Delay(20, ct);
        }
        Assert.Fail($"PostgreSQL did not observe PID {waiter} waiting for {holder}; serialization is unproven.");
    }
    public Task DisposeAsync() => Task.CompletedTask; // Preserve isolated synthetic evidence; never drop a supplied database.
}
