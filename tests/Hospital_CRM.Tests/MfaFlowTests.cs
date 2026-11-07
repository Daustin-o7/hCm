using System.Text.Json;
using System.Text.RegularExpressions;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// The opt-in second factor: password alone must not issue a session, and both methods
/// (authenticator app, emailed code) must complete the challenge.
/// </summary>
public class MfaFlowTests
{
    // RFC 6238 test secret; the controller only ever sees it through the protector.
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Password = "Password123!";

    [Fact]
    public async Task Email_Method_Emails_A_Code_That_Completes_Login()
    {
        using var db = CreateDb();
        await AddUserAsync(db, "email", new TestSecretProtector());

        var email = new FakeEmailSender();
        var controller = NewController(db, email);

        var login = await controller.Login(new LoginRequest("owner@samstack.ai", Password), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(login);
        var body = JsonSerializer.SerializeToElement(ok.Value);

        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        Assert.Equal("email", body.GetProperty("method").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));

        var message = Assert.Single(email.Sent);
        Assert.Equal("owner@samstack.ai", message.To);
        var code = Regex.Match(message.Body, @"\b\d{6}\b").Value;
        Assert.Equal(6, code.Length);

        var verify = await controller.VerifyMfa(
            new MfaVerifyRequest(body.GetProperty("challengeToken").GetString(), code),
            CancellationToken.None);
        var session = Assert.IsType<OkObjectResult>(verify);
        Assert.False(string.IsNullOrWhiteSpace(
            JsonSerializer.SerializeToElement(session.Value).GetProperty("accessToken").GetString()));
    }

    [Fact]
    public async Task Authenticator_Method_Needs_The_App_Code_And_Sends_No_Email()
    {
        using var db = CreateDb();
        await AddUserAsync(db, "totp", new TestSecretProtector());

        var email = new FakeEmailSender();
        var controller = NewController(db, email);

        var login = await controller.Login(new LoginRequest("owner@samstack.ai", Password), CancellationToken.None);
        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(login).Value);

        Assert.Equal("totp", body.GetProperty("method").GetString());
        Assert.Empty(email.Sent);

        var wrong = await controller.VerifyMfa(
            new MfaVerifyRequest(body.GetProperty("challengeToken").GetString(), "000000"),
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(wrong);

        var code = TotpService.ComputeCode(Secret, DateTimeOffset.UtcNow);
        var verify = await controller.VerifyMfa(
            new MfaVerifyRequest(body.GetProperty("challengeToken").GetString(), code),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(verify);
    }

    [Fact]
    public async Task Email_Code_Outside_The_Five_Minute_Window_Is_Rejected()
    {
        using var db = CreateDb();
        await AddUserAsync(db, "email", new TestSecretProtector());

        var controller = NewController(db, new FakeEmailSender());
        var login = await controller.Login(new LoginRequest("owner@samstack.ai", Password), CancellationToken.None);
        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(login).Value);

        // Two 300 s steps back: outside the ±1 step skew an emailed code allows.
        var stale = TotpService.ComputeCode(
            Secret, DateTimeOffset.UtcNow.AddSeconds(-600), TotpService.EmailPeriodSeconds);

        var verify = await controller.VerifyMfa(
            new MfaVerifyRequest(body.GetProperty("challengeToken").GetString(), stale),
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(verify);
    }

    [Fact]
    public async Task VerifyMfa_Exceeding_Max_Attempts_Locks_Account()
    {
        using var db = CreateDb();
        await AddUserAsync(db, "totp", new TestSecretProtector());

        var controller = NewController(db, new FakeEmailSender());
        var login = await controller.Login(new LoginRequest("owner@samstack.ai", Password), CancellationToken.None);
        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(login).Value);
        var challengeToken = body.GetProperty("challengeToken").GetString();

        for (int i = 0; i < 5; i++)
        {
            var res = await controller.VerifyMfa(new MfaVerifyRequest(challengeToken, "000000"), CancellationToken.None);
            Assert.IsType<UnauthorizedObjectResult>(res);
        }

        var user = await db.Users.FirstAsync(u => u.Email == "owner@samstack.ai");
        Assert.NotNull(user.LockedUntil);
        Assert.True(user.LockedUntil > DateTimeOffset.UtcNow);

        var code = TotpService.ComputeCode(Secret, DateTimeOffset.UtcNow);
        var lockedRes = await controller.VerifyMfa(new MfaVerifyRequest(challengeToken, code), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(lockedRes);
    }

    private static async Task AddUserAsync(HospitalCrmDbContext db, string method, TestSecretProtector protector)
    {
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "owner@samstack.ai",
            Name = "Platform Owner",
            PasswordHash = Password.HashPassword(),
            Role = UserRole.PlatformAdmin,
            MfaEnabled = true,
            MfaMethod = method,
            MfaSecret = protector.Protect(Secret)
        });
        await db.SaveChangesAsync();
    }

    private static AuthController NewController(HospitalCrmDbContext db, FakeEmailSender email)
    {
        var controller = new AuthController(
            db,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            new FakeKeyService(),
            new FakeHostEnv(),
            new TestSecretProtector(),
            email);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static HospitalCrmDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase($"MfaFlowDb_{Guid.NewGuid()}")
            .Options, TestMaintenanceTenantContext.Instance);

    private sealed class FakeKeyService : IPersistentKeyService
    {
        public string KeySource => "Test";
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public RsaSecurityKey GetPrivateKey() => null!;
        public RsaSecurityKey GetPublicKey() => null!;
        public object GetJwks() => new();
    }

    private sealed class FakeHostEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Hospital_CRM";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
