using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// Invite and password-reset tokens are bearer secrets: the database keeps only a fast digest
/// so the lookup is one indexed read, and the raw value must never reach the logs.
/// </summary>
public class Section2AuthTokenTests
{
    private sealed class StubTenantContext : ITenantContext
    {
        public Guid? CurrentTenantId { get; set; }
        public bool IsPlatformAdmin { get; set; }

        public IDisposable PushTenant(Guid tenantId)
        {
            var prior = CurrentTenantId;
            CurrentTenantId = tenantId;
            return new Restore(() => CurrentTenantId = prior);
        }

        private sealed class Restore(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private sealed class CapturingLogger : ILogger<StaffController>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static HospitalCrmDbContext CreateInMemoryDb(ITenantContext? tenantContext = null) =>
        new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase($"Sec2Tokens_{Guid.NewGuid()}")
            .Options, tenantContext ?? TestMaintenanceTenantContext.Instance);

    private static T Json<T>(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    }

    [Fact]
    public async Task Invite_StoresADigestOnly_AndNeverLogsTheRawToken()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new StubTenantContext { CurrentTenantId = tenantId };
        var db = CreateInMemoryDb(tenantContext);
        var adminId = Guid.NewGuid();
        var admin = new User
        {
            Id = adminId,
            TenantId = tenantId,
            ClinicId = Guid.NewGuid(),
            Name = "Admin",
            Email = "admin@clinic.test",
            Role = UserRole.ClinicAdmin,
            PasswordHash = "x"
        };
        db.Users.Add(admin);
        db.Clinics.Add(new Clinic { Id = admin.ClinicId!.Value, TenantId = tenantId, Name = "Selected clinic" });
        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = adminId, TenantId = tenantId, ClinicId = admin.ClinicId,
            Role = UserRole.ClinicAdmin, Status = "active"
        });
        await db.SaveChangesAsync();

        var logger = new CapturingLogger();
        var controller = new StaffController(db, tenantContext, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", adminId.ToString()),
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim("clinicId", admin.ClinicId.Value.ToString()),
                        new Claim(ClaimTypes.Role, "ClinicAdmin")
                    ], "TestAuth"))
                }
            }
        };

        var result = await controller.Invite(
            new StaffInviteRequest("Dr New", "new.doctor@clinic.test", "Doctor"), CancellationToken.None);

        var body = Json<InviteResponse>(result);
        var invite = await db.StaffInvites.SingleAsync(i => i.Id == body.InviteId);
        Assert.Equal(body.InviteToken.HashToken(), invite.TokenHash);
        Assert.Equal(tenantId, invite.TenantId);
        Assert.Equal(admin.ClinicId, invite.ClinicId);
        Assert.DoesNotContain(body.InviteToken, invite.TokenHash);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(body.InviteToken));
    }

    [Fact]
    public async Task AcceptInvite_RejectsATokenThatDoesNotMatchTheStoredDigest()
    {
        var db = CreateInMemoryDb();
        var invite = new StaffInvite
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ClinicId = Guid.NewGuid(),
            Name = "Dr New",
            Email = "new.doctor@clinic.test",
            Role = UserRole.Doctor,
            TokenHash = "the-real-token".HashToken(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.StaffInvites.Add(invite);
        await db.SaveChangesAsync();

        var controller = new StaffController(db, new StubTenantContext(), new CapturingLogger());

        var res = await controller.AcceptInvite(
            new AcceptInviteRequest("a-different-token", "Passw0rd!123"), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(res);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task ConfirmReset_RevokesEveryLiveSession()
    {
        var db = CreateInMemoryDb();
        var rawToken = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Reset Victim",
            Email = "victim@clinic.test",
            Role = UserRole.Doctor,
            PasswordHash = "old-password".HashPassword()
        };
        db.Users.Add(user);
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = rawToken.HashToken(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        });
        for (var i = 0; i < 2; i++)
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TenantId = user.TenantId,
                Role = UserRole.Doctor,
                FamilyId = Guid.NewGuid(),
                TokenHash = $"session-{i}".HashToken(),
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            });
        }
        await db.SaveChangesAsync();

        var controller = new PasswordResetController(db,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PasswordResetController>());

        await controller.ConfirmReset(new ConfirmResetRequest(rawToken, "N3wPassw0rd!"), CancellationToken.None);

        Assert.Empty(await db.RefreshTokens.Where(t => t.RevokedAt == null).ToListAsync());
    }

    [Fact]
    public async Task ConfirmReset_FindsTheTokenByDigest_AndBurnsIt()
    {
        var db = CreateInMemoryDb();
        var rawToken = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Patient Zero",
            Email = "reset@clinic.test",
            Role = UserRole.Doctor,
            PasswordHash = "old-password".HashPassword()
        };
        db.Users.Add(user);
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = rawToken.HashToken(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        });
        await db.SaveChangesAsync();

        var controller = new PasswordResetController(db, new Microsoft.Extensions.Logging.Abstractions.NullLogger<PasswordResetController>());

        var bad = await controller.ConfirmReset(new ConfirmResetRequest(rawToken + "x", "N3wPassw0rd!"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(bad);

        var ok = await controller.ConfirmReset(new ConfirmResetRequest(rawToken, "N3wPassw0rd!"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);

        var token = await db.PasswordResetTokens.SingleAsync();
        Assert.NotNull(token.UsedAt);
        Assert.True("N3wPassw0rd!".VerifyPassword((await db.Users.SingleAsync()).PasswordHash));

        // A used token cannot be replayed.
        var replay = await controller.ConfirmReset(new ConfirmResetRequest(rawToken, "AnotherPass1!"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(replay);
    }

    private sealed record InviteResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("inviteId")] Guid InviteId,
        [property: System.Text.Json.Serialization.JsonPropertyName("inviteToken")] string InviteToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
}
