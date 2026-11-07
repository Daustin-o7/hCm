using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2IdentitySessionTests
{
    private const string Password = "LongPassword1!";
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginAndRotationBindJwtToPersistedFamily_AndOidc(bool platform)
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Account.PasswordHash = Password.HashPassword();
        if (platform) f.Account.Role = UserRole.PlatformAdmin;
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key);
        var issued = AssertSession(await controller.Login(new(f.Account.Email, Password, f.TenantB.Id), default));
        var familyId = Guid.Parse(issued.FindFirst("session_id")!.Value);
        var stored = await f.Db.RefreshTokens.SingleAsync(t => t.FamilyId == familyId);
        Assert.NotEqual(f.Session.FamilyId, familyId);
        Assert.Equal(platform ? null : f.TenantB.Id, stored.TenantId);
        Assert.Equal(platform ? null : f.ClinicB.Id, stored.ClinicId);
        Assert.Equal(platform ? UserRole.PlatformAdmin : UserRole.ClinicAdmin, stored.Role);
        Assert.Equal(platform ? null : f.ClinicB.Id.ToString(), issued.FindFirst("clinicId")?.Value);
        Assert.Equal(platform ? null : f.TenantB.Id.ToString(), issued.FindFirst("tenant_id")?.Value);
        Assert.IsType<SignInResult>(await f.Oidc(issued).Exchange());

        var raw = Cookie(controller);
        var rotated = AssertSession(await controller.Refresh(new(raw), default));
        Assert.Equal(familyId.ToString(), rotated.FindFirst("session_id")!.Value);
        Assert.Single(await f.Db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync());
        Assert.IsType<SignInResult>(await f.Oidc(rotated).Exchange());
        Assert.IsType<UnauthorizedObjectResult>(await controller.Refresh(new(raw), default));
        Assert.IsType<ForbidResult>(await f.Oidc(rotated).Exchange());
        Assert.Null(f.Session.RevokedAt);
    }

    [Fact]
    public async Task LegacyRotationPersistsFamilyOnParent_AndReuseDoesNotRevokeAnotherUsersNullFamily()
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Session.FamilyId = null;
        f.Session.TokenHash = "legacy".HashToken();
        var unrelated = new RefreshToken { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), TokenHash = "unrelated", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        f.Db.RefreshTokens.Add(unrelated);
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key, new ClaimsPrincipal());
        var issued = AssertSession(await controller.Refresh(new("legacy"), default));
        Assert.Equal(f.Session.FamilyId.ToString(), issued.FindFirst("session_id")!.Value);
        Assert.IsType<UnauthorizedObjectResult>(await controller.Refresh(new("legacy"), default));
        Assert.Null(unrelated.RevokedAt);
        Assert.Empty(await f.Db.RefreshTokens.Where(t => t.UserId == f.Account.Id && t.RevokedAt == null).ToListAsync());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("mfa")]
    public async Task SetupRebuildsMembershipRoleAndRevokesOldFamilies(string operation)
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Account.PasswordHash = Password.HashPassword();
        f.Account.MustChangePassword = operation == "password";
        f.Account.MfaSecret = new TestSecretProtector().Protect(Secret);
        f.MemberB.Role = UserRole.Doctor;
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key);
        var issued = AssertSession(operation == "password"
            ? await controller.ChangePassword(new(Password, "ChangedPassword2!"), default)
            : await controller.EnableMfa(new(TotpService.ComputeCode(Secret, DateTimeOffset.UtcNow), "totp"), default));
        Assert.NotNull(f.Session.RevokedAt);
        Assert.Equal("Doctor", issued.FindFirst("role")!.Value);
        Assert.Equal(f.TenantB.Id.ToString(), issued.FindFirst("tenant_id")!.Value);
        Assert.NotEqual(f.Session.FamilyId.ToString(), issued.FindFirst("session_id")!.Value);
        Assert.IsType<SignInResult>(await f.Oidc(issued).Exchange());
        Assert.IsType<ForbidResult>(await f.Oidc(f.Principal()).Exchange());
        var rotated = AssertSession(await controller.Refresh(new(Cookie(controller)), default));
        Assert.Equal(issued.FindFirst("session_id")!.Value, rotated.FindFirst("session_id")!.Value);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("locked")]
    [InlineData("membership")]
    [InlineData("missing_session")]
    [InlineData("impersonated")]
    public async Task SetupAndSelectionCannotResurrectInvalidAuthority(string failure)
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Account.PasswordHash = Password.HashPassword();
        var principal = f.Principal();
        switch (failure)
        {
            case "revoked": f.Session.RevokedAt = DateTimeOffset.UtcNow; break;
            case "locked": f.Account.LockedUntil = DateTimeOffset.UtcNow.AddHours(1); break;
            case "membership": f.MemberB.Status = "inactive"; break;
            case "missing_session": ((ClaimsIdentity)principal.Identity!).RemoveClaim(principal.FindFirst("session_id")!); break;
            case "impersonated": ((ClaimsIdentity)principal.Identity!).AddClaim(new("impersonated_by", Guid.NewGuid().ToString())); break;
        }
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key, principal);
        Assert.IsType<UnauthorizedObjectResult>(await controller.ChangePassword(new(Password, "ChangedPassword2!"), default));
        Assert.IsType<UnauthorizedObjectResult>(await controller.StartMfaSetup(new("totp"), default));
        Assert.IsType<UnauthorizedObjectResult>(await controller.EnableMfa(new("123456"), default));
        Assert.IsType<UnauthorizedObjectResult>(await controller.DisableMfa(new(Password), default));
        Assert.IsType<UnauthorizedObjectResult>(await controller.SelectTenant(new(f.TenantA.Id, f.ClinicA.Id), default));
        Assert.Single(f.Db.RefreshTokens);
        Assert.True(Password.VerifyPassword(f.Account.PasswordHash));
    }

    [Fact]
    public async Task InactivePrimaryMembershipCannotFallBackToGlobalUserRole()
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Account.PasswordHash = Password.HashPassword();
        var primary = await f.Db.TenantMemberships.SingleAsync(m => m.TenantId == f.TenantA.Id);
        primary.Status = "inactive";
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key);
        var login = Assert.IsType<ObjectResult>(await controller.Login(new(f.Account.Email, Password, f.TenantA.Id), default));
        Assert.Equal(403, login.StatusCode);
        var selection = Assert.IsType<ObjectResult>(await controller.SelectTenant(new(f.TenantA.Id, f.ClinicA.Id), default));
        Assert.Equal(403, selection.StatusCode);
        Assert.Single(f.Db.RefreshTokens);
    }

    [Fact]
    public async Task LogoutWithRotatedCookieRevokesFamily_AndRevokeAllRejectsOidc()
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Session.TokenHash = "old-cookie".HashToken();
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key);
        var issued = AssertSession(await controller.Refresh(new("old-cookie"), default));
        Assert.IsType<OkObjectResult>(await controller.Logout(new("old-cookie"), default));
        Assert.IsType<ForbidResult>(await f.Oidc(issued).Exchange());
        f.Session.RevokedAt = null;
        await f.Db.SaveChangesAsync();
        Assert.IsType<SignInResult>(await f.Oidc(f.Principal()).Exchange());
        Assert.IsType<OkObjectResult>(await controller.RevokeAllSessions(default));
        Assert.IsType<ForbidResult>(await f.Oidc(f.Principal()).Exchange());
    }

    [Fact]
    public async Task LockedAccountCannotRefreshOrClearItsLock()
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Session.TokenHash = "locked".HashToken();
        f.Account.LockedUntil = DateTimeOffset.UtcNow.AddHours(1);
        await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedObjectResult>(await Controller(f, key).Refresh(new("locked"), default));
        Assert.Single(f.Db.RefreshTokens);
        Assert.NotNull(f.Account.LockedUntil);
    }

    [Theory]
    [InlineData("password", false)]
    [InlineData("password", true)]
    [InlineData("mfa-secret", false)]
    [InlineData("mfa-secret", true)]
    [InlineData("mfa-enabled", false)]
    [InlineData("mfa-enabled", true)]
    public async Task CredentialChangesInvalidateOutstandingChallenges(string change, bool mfa)
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Account.PasswordHash = Password.HashPassword();
        f.Account.MfaEnabled = mfa;
        f.Account.MfaSecret = new TestSecretProtector().Protect(Secret);
        await f.Db.SaveChangesAsync();
        var controller = Controller(f, key, new ClaimsPrincipal());
        var body = Part2IdentityFixture.Body(await controller.Login(new(f.Account.Email, Password), default));
        var challenge = body.GetProperty(mfa ? "challengeToken" : "selectionToken").GetString()!;
        switch (change)
        {
            case "password": f.Account.PasswordHash = "ChangedPassword2!".HashPassword(); break;
            case "mfa-secret": f.Account.MfaSecret = new TestSecretProtector().Protect(TotpService.GenerateSecret()); break;
            case "mfa-enabled": f.Account.MfaEnabled = !mfa; break;
        }
        await f.Db.SaveChangesAsync();
        var result = mfa
            ? await controller.VerifyMfa(new(challenge, TotpService.ComputeCode(Secret, DateTimeOffset.UtcNow)), default)
            : await controller.SelectTenant(new(f.TenantB.Id, f.ClinicB.Id, challenge), default);
        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Single(f.Db.RefreshTokens);
    }

    [Theory]
    [InlineData("no-membership")]
    [InlineData("no-clinic")]
    [InlineData("foreign-clinic")]
    [InlineData("no-tenant")]
    public async Task LegacyAuthorityCannotBeReconstructedFromPrimaryUserFields(string failure)
    {
        await using var f = new Part2IdentityFixture();
        using var key = new Part2IdentitySigningKey();
        await f.SeedAsync();
        f.Session.TokenHash = "legacy-scope".HashToken();
        switch (failure)
        {
            case "no-membership": f.Db.TenantMemberships.Remove(f.MemberB); break;
            case "no-clinic": f.Session.ClinicId = null; break;
            case "foreign-clinic": f.MemberB.ClinicId = f.ClinicA.Id; f.Session.ClinicId = f.ClinicA.Id; break;
            case "no-tenant": f.Session.TenantId = null; break;
        }
        await f.Db.SaveChangesAsync();
        var rejected = Assert.IsType<ObjectResult>(await Controller(f, key).Refresh(new("legacy-scope"), default));
        Assert.Equal(403, rejected.StatusCode);
        Assert.NotNull(f.Session.RevokedAt);
        Assert.Single(f.Db.RefreshTokens);
        Assert.IsType<ForbidResult>(await f.Oidc(f.Principal()).Exchange());
    }

    private static AuthController Controller(Part2IdentityFixture f, Part2IdentitySigningKey key, ClaimsPrincipal? principal = null) =>
        new(f.Db, f.Config, key, new Part2IdentityEnvironment(), new TestSecretProtector(), new Part2IdentityEmail())
        { ControllerContext = f.Context(principal) };

    private static ClaimsPrincipal AssertSession(IActionResult result)
    {
        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.True(Guid.TryParse(jwt.Claims.Single(c => c.Type == "session_id").Value, out var id));
        Assert.NotEqual(Guid.Empty, id);
        return new(new ClaimsIdentity(jwt.Claims, "test", "sub", "role"));
    }

    private static string Cookie(AuthController controller) => controller.Response.Headers.SetCookie
        .Last(h => h!.StartsWith("refreshToken="))!.Split(';')[0]["refreshToken=".Length..];
}

internal sealed class Part2IdentitySigningKey : IPersistentKeyService, IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    public string KeySource => "ephemeral-test";
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default) =>
        Task.FromResult(_rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    public RsaSecurityKey GetPrivateKey() => new(_rsa) { KeyId = "part2-identity-login" };
    public RsaSecurityKey GetPublicKey() => new(_rsa.ExportParameters(false)) { KeyId = "part2-identity-login" };
    public object GetJwks() => new();
    public void Dispose() => _rsa.Dispose();
}
