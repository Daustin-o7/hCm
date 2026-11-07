using System.IO;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Middlewares;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Hospital_CRM.Tests;

public class SubscriptionEnforcementTests
{
    private class FakeKeyService : IPersistentKeyService
    {
        public string KeySource => "Test";
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public RsaSecurityKey GetPrivateKey() => null!;
        public RsaSecurityKey GetPublicKey() => null!;
        public object GetJwks() => new();
    }

    private class FakeHostEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Hospital_CRM";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: $"SubEnforceDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static HttpContext CreateHttpContext(
        Guid? tenantId,
        string role,
        string method,
        string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role)
        };

        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        return context;
    }

    [Fact]
    public async Task Suspended_Tenant_Denies_Existing_Session_On_Next_Request_With_403()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Suspended Hospital",
            SubscriptionStatus = SubscriptionStatus.Suspended,
            SubscriptionTier = SubscriptionTier.Starter
        });
        await db.SaveChangesAsync();

        var context = CreateHttpContext(tenantId, "Doctor", "GET", "/api/v1/patients");
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new SubscriptionEnforcementMiddleware(next, NullLogger<SubscriptionEnforcementMiddleware>.Instance);
        await middleware.InvokeAsync(context, db);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("tenant_suspended", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReadOnly_Tenant_Allows_GET_200_But_Blocks_POST_With_403_subscription_read_only()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        // Expired yesterday -> inside 3-day grace period = ReadOnly
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Grace Hospital",
            SubscriptionStatus = SubscriptionStatus.Active,
            SubscriptionTier = SubscriptionTier.Starter,
            SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        // 1. GET request -> allowed
        var getContext = CreateHttpContext(tenantId, "Receptionist", "GET", "/api/v1/patients");
        var getPassed = false;
        await new SubscriptionEnforcementMiddleware(_ => { getPassed = true; return Task.CompletedTask; }, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(getContext, db);

        Assert.True(getPassed);
        Assert.NotEqual(StatusCodes.Status403Forbidden, getContext.Response.StatusCode);

        // 2. POST request -> blocked with 403 subscription_read_only
        var postContext = CreateHttpContext(tenantId, "Receptionist", "POST", "/api/v1/patients");
        var postPassed = false;
        await new SubscriptionEnforcementMiddleware(_ => { postPassed = true; return Task.CompletedTask; }, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(postContext, db);

        Assert.False(postPassed);
        Assert.Equal(StatusCodes.Status403Forbidden, postContext.Response.StatusCode);

        postContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(postContext.Response.Body);
        Assert.Equal("subscription_read_only", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Reactivation_Restores_Full_Access_For_Tenant()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Reactivating Clinic",
            SubscriptionStatus = SubscriptionStatus.Suspended,
            SubscriptionTier = SubscriptionTier.Starter
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // While suspended: POST blocked
        var postContext1 = CreateHttpContext(tenantId, "Doctor", "POST", "/api/v1/appointments");
        await new SubscriptionEnforcementMiddleware(_ => Task.CompletedTask, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(postContext1, db);
        Assert.Equal(StatusCodes.Status403Forbidden, postContext1.Response.StatusCode);

        // Reactivate
        tenant.SubscriptionStatus = SubscriptionStatus.Active;
        tenant.SubscriptionEndsAt = DateTimeOffset.UtcNow.AddMonths(1);
        await db.SaveChangesAsync();

        // After reactivation: POST allowed
        var postContext2 = CreateHttpContext(tenantId, "Doctor", "POST", "/api/v1/appointments");
        var passed = false;
        await new SubscriptionEnforcementMiddleware(_ => { passed = true; return Task.CompletedTask; }, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(postContext2, db);

        Assert.True(passed);
        Assert.NotEqual(StatusCodes.Status403Forbidden, postContext2.Response.StatusCode);
    }

    [Fact]
    public async Task Trial_Expires_With_No_Background_Job_Dynamically_Enforcing_Blocked()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        // Trial expired 5 days ago (past 3-day grace period)
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Expired Trial Clinic",
            SubscriptionStatus = SubscriptionStatus.Trial,
            SubscriptionTier = SubscriptionTier.Starter,
            SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(-5)
        });
        await db.SaveChangesAsync();

        var context = CreateHttpContext(tenantId, "ClinicAdmin", "GET", "/api/v1/appointments");
        await new SubscriptionEnforcementMiddleware(_ => Task.CompletedTask, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(context, db);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("tenant_suspended", json.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/api/v1/platform-admin/tenants", "GET")]
    [InlineData("/api/v1/auth/login", "POST")]
    [InlineData("/api/v1/invoices/webhook", "POST")]
    [InlineData("/api/v1/reports/export", "GET")]
    [InlineData("/api/v1/reports/export", "POST")]
    public async Task Exempt_Routes_Pass_Through_Even_When_Tenant_Is_Suspended(string path, string method)
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Suspended But Exempt Clinic",
            SubscriptionStatus = SubscriptionStatus.Suspended,
            SubscriptionTier = SubscriptionTier.Starter
        });
        await db.SaveChangesAsync();

        var context = CreateHttpContext(tenantId, "ClinicAdmin", method, path);
        var passed = false;
        await new SubscriptionEnforcementMiddleware(_ => { passed = true; return Task.CompletedTask; }, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(context, db);

        Assert.True(passed);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_User_Is_Always_Exempt()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Suspended Hospital",
            SubscriptionStatus = SubscriptionStatus.Suspended,
            SubscriptionTier = SubscriptionTier.Starter
        });
        await db.SaveChangesAsync();

        var context = CreateHttpContext(tenantId, "PlatformAdmin", "POST", "/api/v1/patients");
        var passed = false;
        await new SubscriptionEnforcementMiddleware(_ => { passed = true; return Task.CompletedTask; }, NullLogger<SubscriptionEnforcementMiddleware>.Instance)
            .InvokeAsync(context, db);

        Assert.True(passed);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task AuthController_Login_Rejects_Suspended_Tenant_With_403()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Suspended Tenant",
            SubscriptionStatus = SubscriptionStatus.Suspended,
            SubscriptionTier = SubscriptionTier.Starter
        });

        db.Users.Add(new User
        {
            Id = userId,
            Email = "doctor@suspended.com",
            PasswordHash = "Password123!".HashPassword(),
            Name = "Dr Suspended",
            Role = UserRole.Doctor,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var keyService = new FakeKeyService();
        var env = new FakeHostEnv();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var controller = new AuthController(db, config, keyService, env, new TestSecretProtector(), new FakeEmailSender());
        var result = await controller.Login(new LoginRequest("doctor@suspended.com", "Password123!"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task AuthController_Refresh_Rejects_Suspended_Tenant_With_403_And_Revokes_Token()
    {
        using var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rawToken = "my-test-refresh-token-12345";

        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Suspended Tenant",
            SubscriptionStatus = SubscriptionStatus.Suspended,
            SubscriptionTier = SubscriptionTier.Starter
        });

        db.Users.Add(new User
        {
            Id = userId,
            Email = "doctor@suspended.com",
            PasswordHash = "Password123!".HashPassword(),
            Name = "Dr Suspended",
            Role = UserRole.Doctor,
            TenantId = tenantId
        });

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantId = tenantId,
            TokenHash = rawToken.HashToken(),
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        var keyService = new FakeKeyService();
        var env = new FakeHostEnv();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var controller = new AuthController(db, config, keyService, env, new TestSecretProtector(), new FakeEmailSender());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Refresh(new RefreshRequest(rawToken), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        // Verify token was revoked
        var updatedToken = await db.RefreshTokens.FindAsync(refreshToken.Id);
        Assert.NotNull(updatedToken?.RevokedAt);
    }
}
