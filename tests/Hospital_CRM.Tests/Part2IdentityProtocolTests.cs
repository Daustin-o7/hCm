using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Hospital_CRM.Tests;

public class Part2IdentityProtocolTests
{
    [Fact]
    public async Task ApiLoginAndRotationBindRealPkceTokens_AndApiLogoutRevokesThem()
    {
        await using var host = await Part2IdentityProtocolHost.StartAsync();
        host.Fixture.Account.PasswordHash = "LongPassword1!".HashPassword();
        await host.Fixture.Db.SaveChangesAsync();
        using var login = await host.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = host.Fixture.Account.Email, password = "LongPassword1!", tenantId = host.Fixture.TenantB.Id });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var apiToken = body.GetProperty("accessToken").GetString()!;
        var family = new JwtSecurityTokenHandler().ReadJwtToken(apiToken).Claims.Single(c => c.Type == "session_id").Value;
        var raw = login.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("refreshToken=")).Split(';')[0]["refreshToken=".Length..];
        var tokens = await host.ExchangeAsync(await host.AuthorizeAsync(apiToken));
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        Assert.Equal(family, new JwtSecurityTokenHandler().ReadJwtToken(tokens.Body.GetProperty("access_token").GetString())
            .Claims.Single(c => c.Type == "session_id").Value);
        using var rotation = await host.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = raw });
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        var rotatedBody = await rotation.Content.ReadFromJsonAsync<JsonElement>();
        var rotatedToken = rotatedBody.GetProperty("accessToken").GetString()!;
        Assert.Equal(family, new JwtSecurityTokenHandler().ReadJwtToken(rotatedToken).Claims.Single(c => c.Type == "session_id").Value);
        Assert.Equal(HttpStatusCode.OK, (await host.ExchangeAsync(await host.AuthorizeAsync(rotatedToken))).Status);
        using var logout = await host.Client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = raw });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        AssertError(await host.RefreshAsync(tokens.Body), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.UserinfoAsync(tokens.Body)).Status);
    }

    [Fact]
    public async Task RealPkce_CodeAndRefreshRebuildCurrentRole_AndRejectBadVerifierReplayAndPasswordGrant()
    {
        await using var host = await Part2IdentityProtocolHost.StartAsync();
        var code = await host.AuthorizeAsync();
        host.Fixture.MemberB.Role = UserRole.Receptionist;
        await host.Fixture.Db.SaveChangesAsync();
        var tokens = await host.ExchangeAsync(code);
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        host.AssertScope(tokens.Body, "receptionist");
        var info = await host.UserinfoAsync(tokens.Body);
        Assert.Equal(HttpStatusCode.OK, info.Status);
        Assert.Equal("receptionist", info.Body.GetProperty("role").GetString());
        Assert.Equal(host.Fixture.ClinicB.Id.ToString(), info.Body.GetProperty("clinicId").GetString());

        host.Fixture.MemberB.Role = UserRole.Nurse;
        await host.Fixture.Db.SaveChangesAsync();
        var refreshed = await host.RefreshAsync(tokens.Body);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        host.AssertScope(refreshed.Body, "nurse");
        AssertError(await host.ExchangeAsync(code), "invalid_grant");

        var badCode = await host.AuthorizeAsync();
        AssertError(await host.ExchangeAsync(badCode, new string('x', 64)), "invalid_grant");
        var password = await host.PostAsync("/connect/token", new()
        {
            ["grant_type"] = "password", ["client_id"] = "part2-identity",
            ["username"] = host.Fixture.Account.Email, ["password"] = "LongPassword1!"
        });
        AssertError(password, "unsupported_grant_type");
    }

    [Theory]
    [InlineData("removed_membership")]
    [InlineData("locked")]
    [InlineData("password_change")]
    [InlineData("revoked")]
    public async Task RealProtocol_RejectsStaleCodeRefreshAndUserinfo(string failure)
    {
        await using var host = await Part2IdentityProtocolHost.StartAsync();
        var tokens = await host.ExchangeAsync(await host.AuthorizeAsync());
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        var pendingCode = await host.AuthorizeAsync();
        var f = host.Fixture;
        switch (failure)
        {
            case "removed_membership": f.Db.TenantMemberships.Remove(f.MemberB); break;
            case "locked": f.Account.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10); break;
            case "password_change": f.Account.MustChangePassword = true; break;
            case "revoked": f.Session.RevokedAt = DateTimeOffset.UtcNow; break;
        }
        await f.Db.SaveChangesAsync();
        AssertError(await host.ExchangeAsync(pendingCode), "invalid_grant");
        AssertError(await host.RefreshAsync(tokens.Body), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.UserinfoAsync(tokens.Body)).Status);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("reset")]
    public async Task RealProtocol_LogoutAndPasswordResetRevokeBoundOidcRefresh(string operation)
    {
        await using var host = await Part2IdentityProtocolHost.StartAsync();
        var tokens = await host.ExchangeAsync(await host.AuthorizeAsync());
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        if (operation == "logout")
        {
            using var response = await host.Client.GetAsync(QueryHelpers.AddQueryString("/connect/logout",
                "id_token_hint", tokens.Body.GetProperty("id_token").GetString()!));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        else
        {
            host.Fixture.Db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = Guid.NewGuid(), UserId = host.Fixture.Account.Id,
                TokenHash = "reset-link".HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
            });
            await host.Fixture.Db.SaveChangesAsync();
            using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/confirm-password-reset",
                new { resetToken = "reset-link", newPassword = "NewPassword1!" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        AssertError(await host.RefreshAsync(tokens.Body), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.UserinfoAsync(tokens.Body)).Status);
    }

    private static void AssertError((HttpStatusCode Status, JsonElement Body) response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(error, response.Body.GetProperty("error").GetString());
        Assert.False(response.Body.TryGetProperty("access_token", out _));
        Assert.False(response.Body.TryGetProperty("refresh_token", out _));
    }
}

// A disposable loopback server exercises installed OpenIddict/JWT middleware with ephemeral keys and synthetic data.
// This proves protocol behavior, not Program.cs deployment configuration or PostgreSQL/RLS concurrency.
internal sealed class Part2IdentityProtocolHost : IAsyncDisposable
{
    public readonly Part2IdentityFixture Fixture = new();
    private readonly Part2IdentitySigningKey _key = new();
    private WebApplication _app = null!;
    public HttpClient Client { get; private set; } = null!;
    private string _loginToken = "";
    private const string Callback = "https://client.example.test/callback";
    private const string Verifier = "part2-identity-pkce-verifier-with-at-least-forty-three-characters";

    public static async Task<Part2IdentityProtocolHost> StartAsync()
    {
        var host = new Part2IdentityProtocolHost();
        try { await host.StartCoreAsync(); return host; }
        catch { await host.DisposeAsync(); throw; }
    }

    private async Task StartCoreAsync()
    {
        await Fixture.SeedAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AuthorizationController).Assembly.GetName().Name,
            EnvironmentName = "Testing", Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(Fixture.Config.AsEnumerable());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Issuer"] = "part2-identity-login", ["Jwt:Audience"] = "api", ["Jwt:KeyId"] = "part2-identity-login" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddDbContext<HospitalCrmDbContext>(options =>
            options.UseInMemoryDatabase(Fixture.DatabaseName, Fixture.DatabaseRoot));
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthorizationController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IPersistentKeyService>(_key);
        builder.Services.AddSingleton<ISecretProtector>(new TestSecretProtector());
        builder.Services.AddSingleton<IEmailSender>(new Part2IdentityEmail());
        var key = _key.GetPrivateKey();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = "part2-identity-login", ValidateAudience = true, ValidAudience = "api",
                ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.Zero,
                NameClaimType = "sub", RoleClaimType = "role"
            };
        });
        builder.Services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<HospitalCrmDbContext>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/connect/authorize").SetTokenEndpointUris("/connect/token")
                    .SetUserInfoEndpointUris("/connect/userinfo").SetEndSessionEndpointUris("/connect/logout");
                options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange().AllowRefreshTokenFlow();
                options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.Roles, Scopes.OfflineAccess, "api");
                options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                options.DisableAccessTokenEncryption();
                options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough().EnableEndSessionEndpointPassthrough().DisableTransportSecurityRequirement();
            });
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "part2-identity", ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Implicit,
                RedirectUris = { new(Callback) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code, Permissions.Scopes.Profile, Permissions.Scopes.Email,
                    Permissions.Scopes.Roles, Permissions.Prefixes.Scope + "api"
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            });
        }
        await _app.StartAsync();
        Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new(_app.Urls.Single()) };
        var jwt = new JwtSecurityToken("part2-identity-login", "api", Fixture.Principal().Claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        _loginToken = new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public async Task<string> AuthorizeAsync(string? apiToken = null)
    {
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
        using var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "part2-identity", ["redirect_uri"] = Callback, ["response_type"] = "code",
            ["scope"] = "openid profile email roles api offline_access", ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["state"] = "part2-state", ["nonce"] = "part2-nonce"
        }));
        request.Headers.Authorization = new("Bearer", apiToken ?? _loginToken);
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.False(query.ContainsKey("error"), query.TryGetValue("error", out var error) ? error.ToString() : "");
        Assert.Equal("part2-state", query["state"]);
        return query["code"].ToString();
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(string code, string verifier = Verifier) =>
        PostAsync("/connect/token", new()
        {
            ["client_id"] = "part2-identity", ["redirect_uri"] = Callback, ["grant_type"] = "authorization_code",
            ["code"] = code, ["code_verifier"] = verifier
        });

    public Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(JsonElement tokens) => PostAsync("/connect/token", new()
    {
        ["client_id"] = "part2-identity", ["grant_type"] = "refresh_token",
        ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!
    });

    public async Task<(HttpStatusCode Status, JsonElement Body)> UserinfoAsync(JsonElement tokens)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        using var response = await Client.SendAsync(request);
        if (response.IsSuccessStatusCode)
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.ToString().Contains("invalid_token"));
        return (response.StatusCode, default);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, Dictionary<string, string> fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await Client.PostAsync(path, content);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    public void AssertScope(JsonElement response, string role)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(response.GetProperty("access_token").GetString());
        Assert.Equal(role, token.Claims.Single(c => c.Type == "role").Value);
        Assert.Equal(Fixture.TenantB.Id.ToString(), token.Claims.Single(c => c.Type == "tenant_id").Value);
        Assert.Equal(Fixture.ClinicB.Id.ToString(), token.Claims.Single(c => c.Type == "clinicId").Value);
        Assert.Equal(Fixture.Session.FamilyId.ToString(), token.Claims.Single(c => c.Type == "session_id").Value);
        Assert.DoesNotContain(token.Claims, c => c.Type == "clinic_id");
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _key.Dispose();
        await Fixture.DisposeAsync();
    }
}
