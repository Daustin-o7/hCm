using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part2IdentityTests
{
    [Theory]
    [InlineData("handoff")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    public async Task ProvisionAndStaffInvites_UseConsumerDigest_AndTruthfulEmailOutcomes(string outcome)
    {
        await using var f = new Part2IdentityFixture();
        var email = new Part2IdentityEmail(outcome);
        var actor = new User { Id = Guid.NewGuid(), Role = UserRole.PlatformAdmin, Email = "platform@example.test" };
        f.Db.Users.Add(actor);
        await f.Db.SaveChangesAsync();
        var provision = new PlatformAdminController(f.Db, null!, f.Config, null!, email, dataProtection: new EphemeralDataProtectionProvider())
        { ControllerContext = f.Context(new(new ClaimsIdentity([new Claim("sub", actor.Id.ToString()), new Claim("role", "PlatformAdmin")], "test"))) };
        provision.Request.Headers["X-Idempotency-Key"] = Guid.NewGuid().ToString("N");
        var response = Part2IdentityFixture.Body(await provision.ProvisionTenant(
            new("Created clinic", null, "Invited Admin", "invited@example.test"), default));
        var token = response.GetProperty("inviteToken").GetString()!;
        var invite = await f.Db.StaffInvites.SingleAsync();
        Assert.Equal(token.HashToken(), invite.TokenHash);
        Assert.Equal(64, invite.TokenHash.Length);
        Assert.Equal(outcome is "cancelled" ? "unknown" : outcome, response.GetProperty("emailStatus").GetString());
        Assert.Contains($"https://clinic.example.test/accept-invite?token={token}", Assert.Single(email.Messages).Body);
        Assert.Equal("invited@example.test", email.Messages[0].To);
        Assert.InRange(invite.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromHours(71.99), TimeSpan.FromHours(72));

        var accept = f.Staff();
        Assert.IsType<OkObjectResult>(await accept.AcceptInvite(new(token, "LongPassword1!"), default));
        var member = await f.Db.TenantMemberships.SingleAsync(m => m.TenantId == invite.TenantId);
        Assert.Equal(invite.ClinicId, member.ClinicId);
        Assert.Equal(UserRole.ClinicAdmin, member.Role);
        Assert.Equal("active", member.Status);
        var user = await f.Db.Users.SingleAsync(u => u.Id == member.UserId);
        Assert.True("LongPassword1!".VerifyPassword(user.PasswordHash));
        Assert.StartsWith("$2", user.PasswordHash);
        Assert.IsType<UnauthorizedObjectResult>(await accept.AcceptInvite(new(token, "LongPassword1!"), default));
        Assert.Single(await f.Db.TenantMemberships.Where(m => m.TenantId == invite.TenantId).ToListAsync());

        await f.SeedAsync();
        var staff = f.Staff(email);
        var staffResponse = Part2IdentityFixture.Body(await staff.Invite(new("New Doctor", "doctor@example.test", "Doctor"), default));
        var staffToken = staffResponse.GetProperty("inviteToken").GetString()!;
        var staffInvite = await f.Db.StaffInvites.SingleAsync(i => i.Email == "doctor@example.test");
        Assert.Equal(f.TenantB.Id, staffInvite.TenantId);
        Assert.Equal(f.ClinicB.Id, staffInvite.ClinicId);
        Assert.Equal(staffToken.HashToken(), staffInvite.TokenHash);
        Assert.Equal(2, email.Messages.Count);
        Assert.IsType<ConflictObjectResult>(await staff.Invite(new("New Doctor", "doctor@example.test", "Doctor"), default));
        Assert.Equal(2, email.Messages.Count);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("tampered")]
    [InlineData("empty_tenant")]
    [InlineData("empty_clinic")]
    [InlineData("foreign_clinic")]
    [InlineData("platform_role")]
    public async Task AcceptanceRejectsInvalidLinksWithoutMembershipWrites(string failure)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var invite = new StaffInvite
        {
            Id = Guid.NewGuid(), TenantId = f.TenantB.Id, ClinicId = f.ClinicB.Id,
            Email = "invite@example.test", Name = "Invite", TokenHash = "raw-link".HashToken(),
            Role = UserRole.Doctor, ExpiresAt = DateTimeOffset.UtcNow.AddHours(72)
        };
        if (failure == "expired") invite.ExpiresAt = DateTimeOffset.UtcNow;
        if (failure == "used") invite.AcceptedAt = DateTimeOffset.UtcNow;
        if (failure == "empty_tenant") invite.TenantId = Guid.Empty;
        if (failure == "empty_clinic") invite.ClinicId = Guid.Empty;
        if (failure == "foreign_clinic") invite.ClinicId = f.ClinicA.Id;
        if (failure == "platform_role") invite.Role = UserRole.PlatformAdmin;
        f.Db.StaffInvites.Add(invite);
        await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedObjectResult>(await f.Staff().AcceptInvite(
            new(failure == "tampered" ? "wrong-link" : "raw-link", "LongPassword1!"), default));
        Assert.Equal(2, await f.Db.TenantMemberships.CountAsync());
        Assert.Single(await f.Db.Users.ToListAsync());
    }

    [Fact]
    public async Task SelectedClinic_ProfileCalendarAndStaffUseMembershipB_NotGlobalA()
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var clinic = new ClinicController(f.Db) { ControllerContext = f.Context() };
        var profile = Part2IdentityFixture.Body(await clinic.GetProfile(default));
        Assert.Equal(f.ClinicB.Id, profile.GetProperty("clinicId").GetGuid());
        Assert.IsType<OkObjectResult>(await clinic.UpdateProfile(new("Updated B"), default));
        Assert.Equal("Clinic A", f.ClinicA.Name);
        Assert.Equal("Updated B", f.ClinicB.Name);
        Assert.IsType<OkObjectResult>(await clinic.UpdateHours(new([new("monday", 0, "09:00", "17:00")]), default));
        Assert.Equal(f.ClinicB.Id, (await f.Db.ClinicHours.SingleAsync()).ClinicId);
        await clinic.AddHoliday(new("Closure", "2026-12-01", "2026-12-01", false, null), default);
        await clinic.AddSpecialHour(new("2026-12-02", "10:00", "12:00", null), default);
        Assert.Equal(f.ClinicB.Id, (await f.Db.ClinicHolidays.SingleAsync()).ClinicId);
        Assert.Equal(f.ClinicB.Id, (await f.Db.ClinicSpecialHours.SingleAsync()).ClinicId);
        var foreignHoliday = new ClinicHoliday { Id = Guid.NewGuid(), ClinicId = f.ClinicA.Id, Name = "Foreign", StartDate = "2026-12-03", EndDate = "2026-12-03" };
        f.Db.ClinicHolidays.Add(foreignHoliday);
        await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await clinic.DeleteHoliday(foreignHoliday.Id, default));
        Assert.IsType<OkObjectResult>(await clinic.DeleteSpecialHour((await f.Db.ClinicSpecialHours.SingleAsync()).Id, default));

        var staff = f.Staff(new Part2IdentityEmail());
        var list = Part2IdentityFixture.Body(await staff.ListStaff(default));
        Assert.Single(list.EnumerateArray());
        Assert.Equal("clinicadmin", list[0].GetProperty("role").GetString());
        Assert.Equal("active", list[0].GetProperty("status").GetString());
        var created = Part2IdentityFixture.Body(await staff.Invite(new("Nurse", "nurse@example.test", "Nurse"), default));
        var invites = Part2IdentityFixture.Body(await staff.ListInvites(default));
        Assert.Single(invites.EnumerateArray());
        Assert.IsType<OkObjectResult>(await staff.CancelInvite(created.GetProperty("inviteId").GetGuid(), default));
        Assert.Empty(f.Db.StaffInvites);
        Assert.IsType<BadRequestObjectResult>(await staff.Invite(new("Escalated", "bad@example.test", "ClinicAdmin"), default));

        f.MemberB.Status = "suspended";
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await clinic.GetProfile(default));
        Assert.IsType<ForbidResult>(await clinic.UpdateProfile(new("Must not write"), default));
        Assert.IsType<ForbidResult>(await staff.ListStaff(default));
        Assert.IsType<ForbidResult>(await staff.Invite(new("No invite", "denied@example.test", "Doctor"), default));
    }

    [Theory]
    [InlineData("authorization_code")]
    [InlineData("refresh_token")]
    public async Task ActiveOidc_RebuildsMembershipRoleAndCanonicalScope(string grant)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var source = f.Principal();
        source.SetScopes("openid", "api", "offline_access");
        source.SetResources("api");
        var controller = f.Oidc(source, grant);
        var authorize = Assert.IsType<SignInResult>(await controller.Authorize());
        Assert.Equal(f.ClinicB.Id.ToString(), authorize.Principal.GetClaim("clinicId"));
        Assert.Null(authorize.Principal.GetClaim("clinic_id"));
        f.MemberB.Role = UserRole.Receptionist;
        await f.Db.SaveChangesAsync();
        var exchange = Assert.IsType<SignInResult>(await controller.Exchange());
        Assert.Equal("receptionist", exchange.Principal.GetClaim("role"));
        Assert.Equal(f.TenantB.Id.ToString(), exchange.Principal.GetClaim("tenant_id"));
        Assert.Contains("offline_access", exchange.Principal.GetScopes());
        var info = Part2IdentityFixture.Body(await controller.Userinfo());
        Assert.Equal("receptionist", info.GetProperty("role").GetString());
        Assert.Equal(f.ClinicB.Id.ToString(), info.GetProperty("clinicId").GetString());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired_session")]
    [InlineData("removed_membership")]
    [InlineData("inactive_membership")]
    [InlineData("missing_scope")]
    [InlineData("missing_session")]
    [InlineData("wrong_family")]
    [InlineData("locked")]
    [InlineData("password_change")]
    [InlineData("mfa_required")]
    [InlineData("mfa_not_verified")]
    [InlineData("impersonation")]
    public async Task Oidc_DeniesIssuanceRefreshAndUserinfo_WhenCurrentAuthorityIsInvalid(string failure)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var source = f.Principal();
        var identity = (ClaimsIdentity)source.Identity!;
        switch (failure)
        {
            case "revoked": f.Session.RevokedAt = DateTimeOffset.UtcNow; break;
            case "expired_session": f.Session.ExpiresAt = DateTimeOffset.UtcNow; break;
            case "removed_membership": f.Db.TenantMemberships.Remove(f.MemberB); break;
            case "inactive_membership": f.MemberB.Status = "suspended"; break;
            case "missing_scope": identity.RemoveClaim(identity.FindFirst("clinicId")!); break;
            case "missing_session": identity.RemoveClaim(identity.FindFirst("session_id")!); break;
            case "wrong_family": identity.SetClaim("session_id", Guid.NewGuid().ToString()); break;
            case "locked": f.Account.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(10); break;
            case "password_change": f.Account.MustChangePassword = true; break;
            case "mfa_required": f.Config["Mfa:RequireEnrolment"] = "true"; break;
            case "mfa_not_verified": f.Account.MfaEnabled = true; break;
            case "impersonation": identity.AddClaim(new("impersonated_by", Guid.NewGuid().ToString())); break;
        }
        await f.Db.SaveChangesAsync();
        var controller = f.Oidc(source);
        Assert.IsType<ForbidResult>(await controller.Authorize());
        Assert.IsType<ForbidResult>(await controller.Exchange());
        Assert.IsType<ChallengeResult>(await controller.Userinfo());
    }

    [Fact]
    public async Task ResetRequest_QueuesIdenticalProtectedWork_ForKnownAndUnknown_WithoutSmtpWait()
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var jobs = new Part2IdentityJobs();
        var protection = new EphemeralDataProtectionProvider();
        var controller = new PasswordResetController(f.Db, NullLogger<PasswordResetController>.Instance, jobs, protection);
        var start = DateTimeOffset.UtcNow;
        var known = Part2IdentityFixture.Body(await controller.RequestReset(new(f.Account.Email), default));
        var unknown = Part2IdentityFixture.Body(await controller.RequestReset(new("unknown@example.test"), default));
        Assert.Equal(known.GetRawText(), unknown.GetRawText());
        Assert.Empty(await f.Db.PasswordResetTokens.ToListAsync());
        Assert.Equal(2, jobs.Items.Count);
        var persistedJob = Hangfire.Storage.InvocationData.SerializeJob(jobs.Items[0]);
        Assert.DoesNotContain(f.Account.Email, persistedJob.Arguments);
        var email = new Part2IdentityEmail();
        var worker = new PasswordResetEmailJob(f.Db, email, f.Config, protection, NullLogger<PasswordResetEmailJob>.Instance);
        foreach (var job in jobs.Items)
            await worker.SendAsync((Guid)job.Args[0], (string)job.Args[1], default);
        var sent = Assert.Single(email.Messages);
        Assert.Equal(f.Account.Email, sent.To);
        var raw = Regex.Match(sent.Body, @"token=([^\s]+)").Groups[1].Value;
        Assert.DoesNotContain(raw, persistedJob.Arguments);
        var record = await f.Db.PasswordResetTokens.SingleAsync();
        Assert.Equal(raw.HashToken(), record.TokenHash);
        Assert.InRange(record.ExpiresAt, start.AddMinutes(30), DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.Contains("30 minutes", sent.Body);
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.SendAsync((Guid)jobs.Items[0].Args[0], (string)jobs.Items[0].Args[1], default));
        Assert.Equal("password_reset_email_already_attempted", replay.Message);
        Assert.Single(email.Messages);

        Assert.IsType<BadRequestObjectResult>(await controller.ConfirmReset(new(raw, "short"), default));
        Assert.IsType<OkObjectResult>(await controller.ConfirmReset(new(raw, "NewPassword1!"), default));
        Assert.NotNull(f.Session.RevokedAt);
        Assert.IsType<ForbidResult>(await f.Oidc(f.Principal()).Exchange());
        Assert.IsType<BadRequestObjectResult>(await controller.ConfirmReset(new(raw, "NewPassword2!"), default));
    }

    [Theory]
    [InlineData("clinicId")]
    [InlineData("clinic_id")]
    [InlineData("tenant_id")]
    [InlineData("tenantId")]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData(ClaimTypes.Name)]
    [InlineData("session_id")]
    [InlineData("role")]
    [InlineData(ClaimTypes.Role)]
    [InlineData("mfa_enabled")]
    [InlineData("must_change_password")]
    [InlineData("mfa_enrolment_required")]
    public async Task Oidc_DeniesDuplicateOrConflictingAuthorityClaims(string claimType)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var source = f.Principal();
        var identity = (ClaimsIdentity)source.Identity!;
        if (claimType is "mfa_enabled" or "must_change_password" or "mfa_enrolment_required")
            identity.AddClaim(new(claimType, "false"));
        identity.AddClaim(new(claimType, Guid.NewGuid().ToString()));
        var controller = f.Oidc(source);
        Assert.IsType<ForbidResult>(await controller.Authorize());
        Assert.IsType<ForbidResult>(await controller.Exchange());
        Assert.IsType<ChallengeResult>(await controller.Userinfo());
        Assert.IsType<SignOutResult>(await controller.LogOut());
        Assert.Null(f.Session.RevokedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-tenant")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Oidc_PlatformAccessRequiresAnExplicitUnscopedSession(string? tenantClaim)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        f.Account.Role = UserRole.PlatformAdmin;
        f.Session.TenantId = null;
        f.Session.ClinicId = null;
        await f.Db.SaveChangesAsync();
        var source = f.Principal(UserRole.PlatformAdmin, scoped: false);
        if (tenantClaim is not null) source.SetClaim("tenant_id", tenantClaim);
        var controller = f.Oidc(source);
        if (tenantClaim is null)
        {
            var result = Assert.IsType<SignInResult>(await controller.Exchange());
            Assert.Equal("platformadmin", result.Principal.GetClaim("role"));
            Assert.Null(result.Principal.GetClaim("tenant_id"));
            Assert.Null(result.Principal.GetClaim("clinicId"));
        }
        else
        {
            Assert.IsType<ForbidResult>(await controller.Authorize());
            Assert.IsType<ForbidResult>(await controller.Exchange());
            Assert.IsType<ChallengeResult>(await controller.Userinfo());
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    public async Task ResetEmail_FailuresRemainObservable_AndDoNotReplayOrExposeSecrets(string outcome)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var protection = new EphemeralDataProtectionProvider();
        var email = new Part2IdentityEmail(outcome);
        var logger = new Part2IdentityLogger<PasswordResetEmailJob>();
        var worker = new PasswordResetEmailJob(f.Db, email, f.Config, protection, logger);
        var payload = PasswordResetEmailJob.ProtectRequest(protection, f.Account.Email);
        var requestId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SendAsync(requestId, payload, default));
        Assert.DoesNotContain(f.Account.Email, error.ToString());
        var token = Regex.Match(Assert.Single(email.Messages).Body, @"token=([^\s]+)").Groups[1].Value;
        Assert.DoesNotContain(logger.Messages, m => m.Contains(token) || m.Contains(f.Account.Email));
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SendAsync(requestId, payload, default));
        Assert.Equal("password_reset_email_already_attempted", replay.Message);
        Assert.Single(email.Messages);
        Assert.Single(await f.Db.PasswordResetTokens.ToListAsync());
    }

    [Fact]
    public async Task MissingSmtpInDevelopmentFailsWithoutLoggingBodyRecipientOrToken()
    {
        var logs = new Part2IdentityLogger<SmtpEmailSender>();
        var sender = new SmtpEmailSender(new ConfigurationBuilder().Build(), new Part2IdentityEnvironment(), logs);
        Assert.False(await sender.SendAsync("sensitive@example.test", "private subject", "secret-token private-body", default));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("sensitive") || m.Contains("secret-token") || m.Contains("private"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://unapproved.example.test")]
    [InlineData("https://user:password@clinic.example.test")]
    [InlineData("https://clinic.example.test?next=evil")]
    public async Task UnconfiguredOrUnsafePublicUrl_CreatesInviteWithRecoveryState_WithoutSending(string? url)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        f.Config["App:PublicBaseUrl"] = url;
        var email = new Part2IdentityEmail();
        var response = Part2IdentityFixture.Body(await f.Staff(email).Invite(new("Doctor", "doctor@example.test", "Doctor"), default));
        Assert.Equal("not_configured", response.GetProperty("emailStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("emailRetry").GetString()));
        Assert.Single(f.Db.StaffInvites);
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task SelectedMembership_DeniesMissingForeignAndDemotedScopes_AndPreservesAcceptedInvite()
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var staff = f.Staff();
        var created = Part2IdentityFixture.Body(await staff.Invite(new("Doctor", "doctor@example.test", "Doctor", "Cardiology"), default));
        Assert.IsType<OkObjectResult>(await staff.AcceptInvite(new(created.GetProperty("inviteToken").GetString()!, "LongPassword1!"), default));
        Assert.Equal("Cardiology", (await f.Db.TenantMemberships.SingleAsync(m => m.UserId != f.Account.Id)).Specialty);
        Assert.IsType<NotFoundObjectResult>(await staff.CancelInvite(created.GetProperty("inviteId").GetGuid(), default));
        Assert.NotNull((await f.Db.StaffInvites.SingleAsync()).AcceptedAt);

        f.MemberB.Role = UserRole.Nurse;
        await f.Db.SaveChangesAsync();
        var clinic = new ClinicController(f.Db) { ControllerContext = f.Context() };
        Assert.IsType<OkObjectResult>(await clinic.GetProfile(default));
        Assert.IsType<ForbidResult>(await clinic.UpdateHours(new([]), default));
        Assert.IsType<ForbidResult>(await staff.ListInvites(default));
        foreach (var clinicId in new[] { f.ClinicA.Id.ToString(), "", Guid.NewGuid().ToString() })
        {
            var principal = f.Principal();
            principal.SetClaim("clinicId", clinicId);
            clinic.ControllerContext = f.Context(principal);
            Assert.IsType<ForbidResult>(await clinic.GetProfile(default));
        }
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(30, true)]
    public async Task ResetExpiry_IsStrict_AndSuccessfulResetInvalidatesOtherLinks(int remainingSeconds, bool valid)
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var token = new PasswordResetToken { Id = Guid.NewGuid(), UserId = f.Account.Id, TokenHash = "reset-link".HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(remainingSeconds) };
        var other = new PasswordResetToken { Id = Guid.NewGuid(), UserId = f.Account.Id, TokenHash = "other-link".HashToken(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) };
        f.Db.AddRange(token, other);
        await f.Db.SaveChangesAsync();
        var reset = new PasswordResetController(f.Db, NullLogger<PasswordResetController>.Instance);
        var result = await reset.ConfirmReset(new("reset-link", "NewPassword1!"), default);
        if (valid)
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(token.UsedAt);
            Assert.NotNull(other.UsedAt);
            Assert.NotNull(f.Session.RevokedAt);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Null(token.UsedAt);
            Assert.Null(other.UsedAt);
            Assert.Null(f.Session.RevokedAt);
            Assert.Equal("old", f.Account.PasswordHash);
        }
    }

    [Fact]
    public async Task ResetJob_ExpiredPayloadDoesNotMintOrSend_AndHangfireResolvesExistingDependencies()
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var email = new Part2IdentityEmail();
        var protection = new EphemeralDataProtectionProvider();
        await using var provider = new ServiceCollection().AddLogging().AddSingleton(f.Db)
            .AddSingleton<IEmailSender>(email).AddSingleton<IConfiguration>(f.Config)
            .AddSingleton<IDataProtectionProvider>(protection).BuildServiceProvider();
        var activator = new Hangfire.AspNetCore.AspNetCoreJobActivator(provider.GetRequiredService<IServiceScopeFactory>());
        using var scope = activator.BeginScope((JobActivatorContext)null!);
        var worker = Assert.IsType<PasswordResetEmailJob>(scope.Resolve(typeof(PasswordResetEmailJob)));
        var payload = protection.CreateProtector("HospitalCrm.PasswordResetEmail.v1").Protect(JsonSerializer.Serialize(new
        { Email = f.Account.Email, Token = "expired-link", ExpiresAt = DateTimeOffset.UtcNow }));
        await worker.SendAsync(Guid.NewGuid(), payload, default);
        Assert.Empty(f.Db.PasswordResetTokens);
        Assert.Empty(email.Messages);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SendAsync(Guid.NewGuid(), "corrupt", default));
        Assert.Equal("password_reset_email_payload_invalid", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task QueueFailure_IsGenericForKnownAndUnknown_AndOnlySafeFailureTypeIsLogged()
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var logger = new Part2IdentityLogger<PasswordResetController>();
        var reset = new PasswordResetController(f.Db, logger, new Part2IdentityJobs(fail: true), new EphemeralDataProtectionProvider());
        var known = Part2IdentityFixture.Body(await reset.RequestReset(new(f.Account.Email), default));
        var unknown = Part2IdentityFixture.Body(await reset.RequestReset(new("unknown@example.test"), default));
        Assert.Equal(known.GetRawText(), unknown.GetRawText());
        Assert.Equal(2, logger.Messages.Count);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("secret-job-payload") || m.Contains(f.Account.Email));
        Assert.Empty(f.Db.PasswordResetTokens);
    }

    [Fact]
    public async Task OidcLogoutRevokesOnlyTheBoundFamily()
    {
        await using var f = new Part2IdentityFixture();
        await f.SeedAsync();
        var other = new RefreshToken { Id = Guid.NewGuid(), UserId = f.Account.Id, FamilyId = Guid.NewGuid(), TokenHash = "other", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        f.Db.RefreshTokens.Add(other);
        await f.Db.SaveChangesAsync();
        var controller = f.Oidc(f.Principal());
        Assert.IsType<SignOutResult>(await controller.LogOut());
        Assert.NotNull(f.Session.RevokedAt);
        Assert.Null(other.RevokedAt);
        Assert.IsType<ForbidResult>(await controller.Exchange());
    }
}

internal sealed class Part2IdentityFixture : IAsyncDisposable
{
    public readonly string DatabaseName = $"Part2Identity_{Guid.NewGuid()}";
    public readonly InMemoryDatabaseRoot DatabaseRoot = new();
    public readonly HospitalCrmDbContext Db;
    public readonly IConfigurationRoot Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["App:PublicBaseUrl"] = "https://clinic.example.test", ["Mfa:RequireEnrolment"] = "false" }).Build();
    public readonly Tenant TenantA = new() { Id = Guid.NewGuid(), Name = "A", SubscriptionStatus = SubscriptionStatus.Active };
    public readonly Tenant TenantB = new() { Id = Guid.NewGuid(), Name = "B", SubscriptionStatus = SubscriptionStatus.Active };
    public readonly Clinic ClinicA;
    public readonly Clinic ClinicB;
    public readonly User Account;
    public readonly TenantMembership MemberB;
    public readonly RefreshToken Session;
    public Part2IdentityFixture()
    {
        Db = new(new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(DatabaseName, DatabaseRoot)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options, TestMaintenanceTenantContext.Instance);
        ClinicA = new() { Id = Guid.NewGuid(), TenantId = TenantA.Id, Name = "Clinic A" };
        ClinicB = new() { Id = Guid.NewGuid(), TenantId = TenantB.Id, Name = "Clinic B" };
        Account = new() { Id = Guid.NewGuid(), Name = "Multi member", Email = "member@example.test", TenantId = TenantA.Id,
            ClinicId = ClinicA.Id, Role = UserRole.Nurse, PasswordHash = "old", UpdatedAt = DateTimeOffset.UtcNow };
        MemberB = new() { UserId = Account.Id, TenantId = TenantB.Id, ClinicId = ClinicB.Id, Role = UserRole.ClinicAdmin };
        Session = new() { Id = Guid.NewGuid(), UserId = Account.Id, FamilyId = Guid.NewGuid(), TenantId = TenantB.Id,
            ClinicId = ClinicB.Id, Role = UserRole.ClinicAdmin, TokenHash = "session", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
    }
    public async Task SeedAsync()
    {
        Db.AddRange(TenantA, TenantB, ClinicA, ClinicB, Account, MemberB, Session,
            new TenantMembership { UserId = Account.Id, TenantId = TenantA.Id, ClinicId = ClinicA.Id, Role = UserRole.Nurse });
        await Db.SaveChangesAsync();
    }
    public ClaimsPrincipal Principal(UserRole role = UserRole.ClinicAdmin, bool scoped = true)
    {
        var claims = new List<Claim> { new("sub", Account.Id.ToString()), new("role", role.ToString()), new("session_id", Session.FamilyId!.Value.ToString()) };
        if (scoped) claims.AddRange([new("tenant_id", TenantB.Id.ToString()), new("clinicId", ClinicB.Id.ToString())]);
        return new(new ClaimsIdentity(claims, "test", "name", "role"));
    }
    public ControllerContext Context(ClaimsPrincipal? principal = null) => new() { HttpContext = new DefaultHttpContext { User = principal ?? Principal() } };
    public StaffController Staff(IEmailSender? email = null) => new(Db, new TenantContext(), NullLogger<StaffController>.Instance, email, Config) { ControllerContext = Context() };
    public AuthorizationController Oidc(ClaimsPrincipal principal, string grant = "refresh_token")
    {
        var context = Context(principal);
        context.HttpContext.RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(new Part2IdentityAuthentication(principal)).BuildServiceProvider();
        context.HttpContext.Features.Set(new OpenIddictServerAspNetCoreFeature { Transaction = new OpenIddictServerTransaction
        { Request = new OpenIddictRequest { GrantType = grant, Scope = "openid api offline_access" } } });
        return new(Db, null!, null!, Config) { ControllerContext = context };
    }
    public static JsonElement Body(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value);
    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

internal sealed class Part2IdentityEmail(string outcome = "handoff") : IEmailSender
{
    public List<(string To, string Body)> Messages { get; } = [];
    public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        Messages.Add((to, body));
        return outcome switch
        {
            "unknown" => Task.FromException<bool>(new InvalidOperationException($"provider payload {to} {body}")),
            "cancelled" => Task.FromException<bool>(new OperationCanceledException()),
            _ => Task.FromResult(outcome == "handoff")
        };
    }
}

internal sealed class Part2IdentityJobs(bool fail = false) : IBackgroundJobClient
{
    public List<Job> Items { get; } = [];
    public string Create(Job job, IState state)
    {
        if (fail) throw new InvalidOperationException("secret-job-payload");
        Items.Add(job);
        return Items.Count.ToString();
    }
    public bool ChangeState(string jobId, IState state, string expectedState) => throw new NotSupportedException();
}

internal sealed class Part2IdentityAuthentication(ClaimsPrincipal principal) : IAuthenticationService
{
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, scheme!)));
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
}

internal sealed class Part2IdentityLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
}

internal sealed class Part2IdentityEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Part2Identity";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
