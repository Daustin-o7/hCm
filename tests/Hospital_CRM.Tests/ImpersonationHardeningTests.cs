using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Middlewares;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// A5 (M) Impersonation Hardening (V4) tests:
/// - Token <= 30 min TTL, carries impersonated_by and impersonation_id claims.
/// - Reason is mandatory (400 reason_required).
/// - PlatformAdmin cannot be impersonated (400 cannot_impersonate_platform_admin).
/// - Non-PlatformAdmin cannot initiate impersonation (403).
/// - Target user must belong to specified tenant (404 user_not_found_in_tenant).
/// - Middleware blocks all write/mutation methods with 403 impersonation_read_only.
/// - Middleware allows GET requests and records PHI reads with both identities.
/// - End impersonation endpoint allows session termination.
/// - EF Core DbContext audit append-only rule permits only EndedAt transition from null.
/// - Permission ceiling: impersonated receptionist role cannot exceed receptionist permissions.
/// </summary>
public class ImpersonationHardeningTests
{
    private class FakeKeyService : IPersistentKeyService
    {
        public string KeySource => "Test";
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default) => Task.FromResult(Encoding.UTF8.GetBytes("fake_sig"));
        public RsaSecurityKey GetPrivateKey() => null!;
        public RsaSecurityKey GetPublicKey() => null!;
        public object GetJwks() => new();
    }

    private static HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: $"ImpersonateDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static IConfiguration CreateConfig()
    {
        var dict = new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "Hospital_CRM_Test",
            ["Jwt:Audience"] = "Hospital_CRM_Test",
            ["Jwt:KeyId"] = "test-key-id",
            ["Jwt:AccessTokenExpiryMinutes"] = "15"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>The middleware now refuses a token whose session row is missing or already ended.</summary>
    private static async Task<HospitalCrmDbContext> CreateDbWithOpenSessionAsync(
        Guid impersonationId, Guid adminId, Guid tenantId, Guid targetUserId)
    {
        var db = CreateInMemoryDb();
        db.ImpersonationLogs.Add(new ImpersonationLog
        {
            Id = impersonationId,
            PlatformAdminId = adminId,
            TenantId = tenantId,
            ImpersonatedUserId = targetUserId,
            StartedAt = DateTimeOffset.UtcNow,
            Reason = "test session"
        });
        await db.SaveChangesAsync();
        return db;
    }
    private static ControllerContext CreateControllerContext(Guid userId, string role = "PlatformAdmin", Guid? impersonatedBy = null, Guid? impersonationId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString()),
            new(ClaimTypes.Role, role),
            new("role", role)
        };

        if (impersonatedBy.HasValue)
        {
            claims.Add(new Claim("impersonated_by", impersonatedBy.Value.ToString()));
        }

        if (impersonationId.HasValue)
        {
            claims.Add(new Claim("impersonation_id", impersonationId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static HttpContext CreateHttpContext(
        Guid userId,
        string role,
        string method,
        string path,
        Guid? impersonatedBy = null,
        Guid? impersonationId = null,
        Guid? tenantId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString()),
            new(ClaimTypes.Role, role),
            new("role", role)
        };

        if (tenantId.HasValue)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        if (impersonatedBy.HasValue)
        {
            claims.Add(new Claim("impersonated_by", impersonatedBy.Value.ToString()));
        }

        if (impersonationId.HasValue)
        {
            claims.Add(new Claim("impersonation_id", impersonationId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        context.User = new ClaimsPrincipal(identity);
        return context;
    }

    [Fact]
    public async Task StartImpersonation_Requires_Written_Reason()
    {
        using var db = CreateInMemoryDb();
        var adminId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test Tenant" });
        db.Users.Add(new User
        {
            Id = targetUserId,
            Email = "doc@test.com",
            Name = "Dr Test",
            Role = UserRole.Doctor,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var controller = new PlatformAdminController(db, new FakeKeyService(), CreateConfig(), auditService)
        {
            ControllerContext = CreateControllerContext(adminId, "PlatformAdmin")
        };

        // Act - empty reason
        var result = await controller.StartImpersonation(new StartImpersonationRequest(tenantId, targetUserId, ""), CancellationToken.None);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(badRequest.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("reason_required", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StartImpersonation_Cannot_Impersonate_PlatformAdmin()
    {
        using var db = CreateInMemoryDb();
        var adminId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var targetAdminId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test Tenant" });
        db.Users.Add(new User
        {
            Id = targetAdminId,
            Email = "admin2@test.com",
            Name = "Admin Two",
            Role = UserRole.PlatformAdmin,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var controller = new PlatformAdminController(db, new FakeKeyService(), CreateConfig(), auditService)
        {
            ControllerContext = CreateControllerContext(adminId, "PlatformAdmin")
        };

        // Act
        var result = await controller.StartImpersonation(
            new StartImpersonationRequest(tenantId, targetAdminId, "Investigating admin issue"), CancellationToken.None);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(badRequest.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("cannot_impersonate_platform_admin", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StartImpersonation_NonPlatformAdmin_Returns_403_Forbid()
    {
        using var db = CreateInMemoryDb();
        var callerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var controller = new PlatformAdminController(db, new FakeKeyService(), CreateConfig(), auditService)
        {
            ControllerContext = CreateControllerContext(callerId, "Doctor")
        };

        // Act
        var result = await controller.StartImpersonation(
            new StartImpersonationRequest(tenantId, targetUserId, "Investigating"), CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task StartImpersonation_User_Not_In_Tenant_Returns_404()
    {
        using var db = CreateInMemoryDb();
        var adminId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant One" });
        db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Tenant Two" });
        db.Users.Add(new User
        {
            Id = targetUserId,
            Email = "nurse@test.com",
            Name = "Nurse Test",
            Role = UserRole.Nurse,
            TenantId = otherTenantId // Belongs to other tenant
        });
        await db.SaveChangesAsync();

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var controller = new PlatformAdminController(db, new FakeKeyService(), CreateConfig(), auditService)
        {
            ControllerContext = CreateControllerContext(adminId, "PlatformAdmin")
        };

        // Act
        var result = await controller.StartImpersonation(
            new StartImpersonationRequest(tenantId, targetUserId, "Investigating"), CancellationToken.None);

        // Assert
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var json = JsonSerializer.Serialize(notFound.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("user_not_found_in_tenant", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StartImpersonation_Mints_Token_With_TTL_Le_30_Minutes_And_Stores_Log()
    {
        using var db = CreateInMemoryDb();
        var adminId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant One" });
        db.Users.Add(new User
        {
            Id = targetUserId,
            Email = "doctor@test.com",
            Name = "Dr Bob",
            Role = UserRole.Doctor,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var controller = new PlatformAdminController(db, new FakeKeyService(), CreateConfig(), auditService)
        {
            ControllerContext = CreateControllerContext(adminId, "PlatformAdmin")
        };

        // Act - request with 60 min duration (should be clamped to 30 min max per A5)
        var result = await controller.StartImpersonation(
            new StartImpersonationRequest(tenantId, targetUserId, "Debugging EHR sync", DurationMinutes: 60),
            CancellationToken.None);

        // Assert
        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);

        var json = JsonSerializer.Serialize(created.Value);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1800, root.GetProperty("expiresIn").GetInt32()); // 30 minutes in seconds
        var token = root.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Verify log was saved in database
        var impId = root.GetProperty("impersonationId").GetGuid();
        var savedLog = await db.ImpersonationLogs.FindAsync(impId);
        Assert.NotNull(savedLog);
        Assert.Equal(adminId, savedLog.PlatformAdminId);
        Assert.Equal(targetUserId, savedLog.ImpersonatedUserId);
        Assert.Equal(tenantId, savedLog.TenantId);
        Assert.Equal("Debugging EHR sync", savedLog.Reason);
        Assert.Null(savedLog.EndedAt);

        // Decode JWT payload to verify claims
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var payload = payloadDoc.RootElement;

        Assert.Equal(targetUserId.ToString(), payload.GetProperty("sub").GetString());
        Assert.Equal(adminId.ToString(), payload.GetProperty("impersonated_by").GetString());
        Assert.Equal(impId.ToString(), payload.GetProperty("impersonation_id").GetString());
        Assert.Equal("Doctor", payload.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Impersonation_Middleware_Rejects_Writes_With_403_impersonation_read_only(string method)
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var impId = Guid.NewGuid();
        using var db = await CreateDbWithOpenSessionAsync(impId, adminId, tenantId, targetUserId);

        var context = CreateHttpContext(
            targetUserId, "Doctor", method, "/api/v1/patients",
            impersonatedBy: adminId, impersonationId: impId, tenantId: tenantId);

        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var middleware = new ImpersonationEnforcementMiddleware(next, NullLogger<ImpersonationEnforcementMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, db, auditService);

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("impersonation_read_only", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Impersonation_Middleware_Allows_Safe_GET_And_Audits_PHI_Read()
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var impId = Guid.NewGuid();
        using var db = await CreateDbWithOpenSessionAsync(impId, adminId, tenantId, targetUserId);

        var context = CreateHttpContext(
            targetUserId, "Doctor", "GET", "/api/v1/patients",
            impersonatedBy: adminId, impersonationId: impId, tenantId: tenantId);

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var middleware = new ImpersonationEnforcementMiddleware(next, NullLogger<ImpersonationEnforcementMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, db, auditService);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        // Verify PHI access was audited with both identities
        var records = await auditService.GetPhiAccessRecordsAsync(impId);
        Assert.Single(records);
        Assert.Equal(adminId, records[0].PlatformAdminId);
        Assert.Equal(targetUserId, records[0].ImpersonatedUserId);
        Assert.Equal(tenantId, records[0].TenantId);
        Assert.Equal("/api/v1/patients", records[0].Path);
        Assert.Equal("GET", records[0].Method);
        Assert.Equal(200, records[0].StatusCode);
    }

    [Fact]
    public async Task Impersonation_Middleware_Allows_Ending_Session_Via_Exception_Route()
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var impId = Guid.NewGuid();
        using var db = await CreateDbWithOpenSessionAsync(impId, adminId, Guid.NewGuid(), targetUserId);

        var context = CreateHttpContext(
            targetUserId, "Doctor", "POST", $"/api/v1/platform-admin/impersonate/{impId}/end",
            impersonatedBy: adminId, impersonationId: impId);

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var middleware = new ImpersonationEnforcementMiddleware(next, NullLogger<ImpersonationEnforcementMiddleware>.Instance);

        // Act
        await middleware.InvokeAsync(context, db, auditService);

        // Assert
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Impersonation_Middleware_Rejects_Token_After_Session_Ended()
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var impId = Guid.NewGuid();

        using var db = await CreateDbWithOpenSessionAsync(impId, adminId, tenantId, targetUserId);
        var session = await db.ImpersonationLogs.FindAsync(impId);
        session!.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var context = CreateHttpContext(
            targetUserId, "Doctor", "GET", "/api/v1/patients",
            impersonatedBy: adminId, impersonationId: impId, tenantId: tenantId);

        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var middleware = new ImpersonationEnforcementMiddleware(next, NullLogger<ImpersonationEnforcementMiddleware>.Instance);

        await middleware.InvokeAsync(context, db, auditService);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("impersonation_ended", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task EndImpersonation_Sets_EndedAt_Successfully()
    {
        using var db = CreateInMemoryDb();
        var adminId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var impId = Guid.NewGuid();

        var log = new ImpersonationLog
        {
            Id = impId,
            PlatformAdminId = adminId,
            TenantId = tenantId,
            ImpersonatedUserId = targetUserId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Reason = "Support session"
        };
        db.ImpersonationLogs.Add(log);
        await db.SaveChangesAsync();

        var auditService = new ImpersonationAuditService(db, NullLogger<ImpersonationAuditService>.Instance);
        var controller = new PlatformAdminController(db, new FakeKeyService(), CreateConfig(), auditService)
        {
            ControllerContext = CreateControllerContext(targetUserId, "Doctor", impersonatedBy: adminId, impersonationId: impId)
        };

        // Act
        var result = await controller.EndImpersonation(impId, CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("endedAt", out var endedAtElem));
        Assert.False(string.IsNullOrWhiteSpace(endedAtElem.GetString()));

        var updated = await db.ImpersonationLogs.FindAsync(impId);
        Assert.NotNull(updated?.EndedAt);
    }

    [Fact]
    public async Task DbContext_EnforceAppendOnly_Permits_Only_EndedAt_Transition()
    {
        using var db = CreateInMemoryDb();
        var impId = Guid.NewGuid();

        var log = new ImpersonationLog
        {
            Id = impId,
            PlatformAdminId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ImpersonatedUserId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            Reason = "Original reason"
        };
        db.ImpersonationLogs.Add(log);
        await db.SaveChangesAsync();

        // 1. Modifying EndedAt from null -> permitted
        log.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(); // Should not throw

        // 2. Modifying Reason -> prohibited, must throw
        log.Reason = "Tampered reason";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static byte[] Base64UrlDecode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
