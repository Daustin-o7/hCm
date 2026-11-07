using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Hospital_CRM.Tests;

public sealed class Part2ProvisioningPostgresTests(Part2IdentityPostgresFixture fixture, ITestOutputHelper output)
    : IClassFixture<Part2IdentityPostgresFixture>
{
    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "https://clinic.example.test" }).Build();

    [Part2IdentityPostgresTheory]
    [InlineData("before_commit")]
    [InlineData("after_commit")]
    [InlineData("after_send")]
    [InlineData("after_response")]
    public async Task CrashBoundariesReplayDurablyInFreshContexts(string crash)
    {
        var actor = await Actor();
        var request = Request(actor);
        var email = new Part2IdentityEmail();
        var keys = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "opencode", "provisioning-keys", actor.ToString("N")));
        JsonElement? original = null;
        IInterceptor? fault = crash switch
        {
            "before_commit" => new CommitCrash(afterCommit: false),
            "after_commit" => new CommitCrash(afterCommit: true),
            "after_send" => new OutcomeCrash(),
            _ => null
        };
        var http = Http(actor);
        await using (var db = fixture.AppDb(Context(http), fault))
        {
            var controller = Controller(db, http, DataProtectionProvider.Create(keys), email);
            if (crash == "after_response") original = Part2ProvisioningTests.Created(await controller.ProvisionTenant(request, default));
            else await Assert.ThrowsAsync<SimulatedCrash>(() => controller.ProvisionTenant(request, default));
        }

        await using (var evidence = fixture.OwnerDb())
        {
            var committed = crash != "before_commit";
            Assert.Equal(committed ? 1 : 0, await evidence.TenantProvisioningReceipts.CountAsync(r => r.ActorId == actor));
            Assert.Equal(committed ? 1 : 0, await evidence.Tenants.CountAsync(t => t.Name == request.Name));
            Assert.Equal(committed ? 1 : 0, await evidence.Clinics.CountAsync(c => c.Name == request.Name));
            Assert.Equal(committed ? 1 : 0, await evidence.StaffInvites.CountAsync(i => i.Email == request.AdminEmail));
        }
        Assert.Equal(crash is "after_send" or "after_response" ? 1 : 0, email.Messages.Count);

        var retryHttp = Http(actor);
        await using var fresh = fixture.AppDb(Context(retryHttp));
        var retry = Controller(fresh, retryHttp, DataProtectionProvider.Create(keys), email);
        var replay = Part2ProvisioningTests.Created(await retry.ProvisionTenant(request, default));
        Assert.Equal(crash is "after_commit" or "after_send" ? "unknown" : "handoff", replay.GetProperty("emailStatus").GetString());
        if (original.HasValue) Assert.Equal(original.Value.GetRawText(), replay.GetRawText());
        Assert.Equal(crash == "after_commit" ? 0 : 1, email.Messages.Count);
        Assert.Equal(crash != "before_commit", retry.Response.Headers.ContainsKey("X-Idempotency-Replay"));
        await AssertSingleProvision(actor, request, replay);
        output.WriteLine($"{crash}: durable replay outcome={replay.GetProperty("emailStatus")}, sends={email.Messages.Count}; database={fixture.Database}.");
    }

    [Part2IdentityPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRequestsBlockThenReplayOrConflictWithoutAnotherSend(bool changedPayload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        var actor = await Actor();
        var request = Request(actor);
        var protection = new EphemeralDataProtectionProvider();
        var email = new PausedEmail();
        var holder = new LockGate(pause: true);
        var waiter = new LockGate(pause: false);
        var http1 = Http(actor);
        var http2 = Http(actor);
        await using var db1 = fixture.AppDb(Context(http1), holder);
        await using var db2 = fixture.AppDb(Context(http2), waiter);
        var first = Controller(db1, http1, protection, email).ProvisionTenant(request, ct);
        Task<IActionResult>? second = null;
        try
        {
            var holderPid = await holder.Held.Task.WaitAsync(ct);
            second = Controller(db2, http2, protection, email).ProvisionTenant(changedPayload ? request with { Name = "Changed" } : request, ct);
            var waiterPid = await waiter.Attempted.Task.WaitAsync(ct);
            await fixture.AssertBlockedAsync(holderPid, waiterPid, ct);
            Assert.False(second.IsCompleted);
            output.WriteLine($"changedPayload={changedPayload}: PostgreSQL PID {waiterPid} blocked by {holderPid} on provisioning actor; database={fixture.Database}.");
        }
        finally { holder.Release.TrySetResult(); }
        JsonElement original;
        try
        {
            await email.Started.Task.WaitAsync(ct);
            var waitingResult = await second!.WaitAsync(ct);
            if (changedPayload) Assert.IsType<ConflictObjectResult>(waitingResult);
            else Assert.Equal("unknown", Part2ProvisioningTests.Created(waitingResult).GetProperty("emailStatus").GetString());
            Assert.Equal(1, email.Calls);
        }
        finally { email.Release.TrySetResult(); }
        original = Part2ProvisioningTests.Created(await first.WaitAsync(ct));
        var retryHttp = Http(actor);
        await using var fresh = fixture.AppDb(Context(retryHttp));
        Assert.Equal(original.GetRawText(), Part2ProvisioningTests.Created(
            await Controller(fresh, retryHttp, protection, email).ProvisionTenant(request, ct)).GetRawText());
        Assert.Equal(1, email.Calls);
        await AssertSingleProvision(actor, request, original);
    }

    [Part2IdentityPostgresTheory]
    [InlineData("same_key")]
    public async Task OtherAdministratorOwnsAnIndependentReceiptAndDatabaseEnforcesUniqueness(string key)
    {
        var actor1 = await Actor();
        var actor2 = await Actor();
        var request = Request(actor1);
        var protection = new EphemeralDataProtectionProvider();
        var email = new Part2IdentityEmail();
        var http1 = Http(actor1, key);
        var http2 = Http(actor2, key);
        await using var db1 = fixture.AppDb(Context(http1));
        await using var db2 = fixture.AppDb(Context(http2));
        var first = Part2ProvisioningTests.Created(await Controller(db1, http1, protection, email).ProvisionTenant(request, default));
        var other = Part2ProvisioningTests.Created(await Controller(db2, http2, protection, email).ProvisionTenant(request, default));
        Assert.NotEqual(first.GetProperty("tenantId").GetGuid(), other.GetProperty("tenantId").GetGuid());
        Assert.NotEqual(first.GetProperty("inviteToken").GetString(), other.GetProperty("inviteToken").GetString());
        Assert.Equal(first.GetRawText(), Part2ProvisioningTests.Created(
            await Controller(db1, http1, protection, email).ProvisionTenant(request, default)).GetRawText());
        Assert.Equal(2, email.Messages.Count);

        var receipt = await db1.TenantProvisioningReceipts.AsNoTracking().SingleAsync(r => r.ActorId == actor1);
        receipt.Id = Guid.NewGuid();
        db1.TenantProvisioningReceipts.Add(receipt);
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db1.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
    }

    [Part2IdentityPostgresTheory]
    [InlineData("accepted")]
    [InlineData("expired")]
    [InlineData("deleted")]
    public async Task HistoricalReplayNeverRepairsReissuesOrResends(string history)
    {
        var actor = await Actor();
        var request = Request(actor);
        var protection = new EphemeralDataProtectionProvider();
        var email = new Part2IdentityEmail();
        var http = Http(actor);
        JsonElement original;
        await using (var db = fixture.AppDb(Context(http)))
            original = Part2ProvisioningTests.Created(await Controller(db, http, protection, email).ProvisionTenant(request, default));
        StaffInvite snapshot;
        await using (var owner = fixture.OwnerDb())
        {
            snapshot = await owner.StaffInvites.SingleAsync(i => i.Id == original.GetProperty("inviteId").GetGuid());
            if (history == "deleted") owner.StaffInvites.Remove(snapshot);
            else if (history == "expired") snapshot.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            else
            {
                snapshot.AcceptedAt = DateTimeOffset.UtcNow;
                owner.Users.Add(new User { Id = Guid.NewGuid(), Email = request.AdminEmail!, Role = UserRole.ClinicAdmin,
                    TenantId = snapshot.TenantId, ClinicId = snapshot.ClinicId });
            }
            await owner.SaveChangesAsync();
            if (history != "deleted") await owner.Entry(snapshot).ReloadAsync();
        }
        var retryHttp = Http(actor);
        await using var fresh = fixture.AppDb(Context(retryHttp));
        Assert.Equal(original.GetRawText(), Part2ProvisioningTests.Created(
            await Controller(fresh, retryHttp, protection, email).ProvisionTenant(request, default)).GetRawText());
        Assert.Single(email.Messages);
        var invitations = await fresh.StaffInvites.Where(i => i.TenantId == snapshot.TenantId).ToListAsync();
        if (history == "deleted") Assert.Empty(invitations);
        else
        {
            var persisted = Assert.Single(invitations);
            Assert.Equal(snapshot.TokenHash, persisted.TokenHash);
            Assert.Equal(snapshot.ExpiresAt, persisted.ExpiresAt);
            Assert.Equal(snapshot.AcceptedAt, persisted.AcceptedAt);
        }
        Assert.Single(await fresh.TenantProvisioningReceipts.Where(r => r.ActorId == actor).ToListAsync());
    }

    private async Task<Guid> Actor()
    {
        var actor = Guid.NewGuid();
        await using var db = fixture.OwnerDb();
        db.Users.Add(new User { Id = actor, Role = UserRole.PlatformAdmin, Email = actor + "@synthetic.invalid" });
        await db.SaveChangesAsync();
        return actor;
    }

    private static ProvisionTenantRequest Request(Guid actor) => new(actor.ToString(), null, "Admin", actor + "-invite@synthetic.invalid",
        ActivatedModules: ["core", "lab"]);
    private static DefaultHttpContext Http(Guid actor, string key = "provision-key")
    {
        var http = new DefaultHttpContext { User = new(new ClaimsIdentity(
            [new Claim("sub", actor.ToString()), new Claim("role", "PlatformAdmin")], "test", "sub", "role")) };
        http.Request.Headers["X-Idempotency-Key"] = key;
        return http;
    }
    private static TenantContext Context(HttpContext http) => new(new HttpContextAccessor { HttpContext = http });
    private static PlatformAdminController Controller(HospitalCrmDbContext db, HttpContext http, IDataProtectionProvider protection, IEmailSender email) =>
        new(db, null!, Config, null!, email, dataProtection: protection) { ControllerContext = new() { HttpContext = http } };

    private async Task AssertSingleProvision(Guid actor, ProvisionTenantRequest request, JsonElement body)
    {
        await using var db = fixture.OwnerDb();
        var receipt = await db.TenantProvisioningReceipts.SingleAsync(r => r.ActorId == actor);
        var tenant = body.GetProperty("tenantId").GetGuid();
        Assert.Equal(tenant, receipt.TenantId);
        Assert.DoesNotContain(body.GetProperty("inviteToken").GetString()!, receipt.ProtectedResponse);
        Assert.Single(await db.Tenants.Where(t => t.Name == request.Name).ToListAsync());
        Assert.Single(await db.Clinics.Where(c => c.TenantId == tenant).ToListAsync());
        Assert.Single(await db.StaffInvites.Where(i => i.TenantId == tenant).ToListAsync());
        Assert.Single(await db.TenantFeatureFlags.Where(f => f.TenantId == tenant).ToListAsync());
    }

    private sealed class SimulatedCrash : Exception;
    private sealed class CommitCrash(bool afterCommit) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => afterCommit ? ValueTask.FromResult(result) : throw new SimulatedCrash();
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => afterCommit ? throw new SimulatedCrash() : Task.CompletedTask;
    }
    private sealed class OutcomeCrash : SaveChangesInterceptor
    {
        private int _saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => ++_saves == 2 ? throw new SimulatedCrash() : ValueTask.FromResult(result);
    }
    private sealed class PausedEmail : IEmailSender
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return true;
        }
    }
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
