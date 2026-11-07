using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Dashboard;
using Hangfire.States;
using Hangfire.Storage;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public sealed class Part2FilesJobsNotificationTests
{
    [Fact]
    public async Task Missing_tenant_and_cross_tenant_jobs_never_send()
    {
        await using var fixture = new Fixture();
        var service = fixture.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAppointmentConfirmationAsync(
            fixture.Appointment.Id, "forged-phone", "forged-clinic", DateTime.UtcNow));
        using var tenant = fixture.Context.PushTenant(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Deliver(service));
        Assert.Empty(fixture.Channel.Messages);
        Assert.Empty(fixture.Storage.Hashes);
    }

    [Fact]
    public async Task Provider_acceptance_requires_id_and_duplicate_jobs_do_not_resend()
    {
        await using var fixture = new Fixture();
        await fixture.Deliver();
        await fixture.Deliver(fixture.Service());
        Assert.Single(fixture.Channel.Messages);
        Assert.Equal(fixture.TenantId, fixture.Channel.Messages[0].TenantId);
        Assert.Equal(fixture.Patient.Phone, fixture.Channel.Messages[0].Phone);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        var log = await fixture.Db.NotificationLogs.SingleAsync();
        Assert.Equal(NotificationStatus.Sent, log.Status);
        Assert.NotNull(log.SentAt);
        Assert.Null(log.DeliveredAt);
        Assert.Contains(fixture.Storage.Hashes.Values, h => h.GetValueOrDefault("provider_id") == "wamid.synthetic");
        Assert.DoesNotContain(fixture.Patient.Phone, string.Join("\n", fixture.Log.Messages));
        Assert.DoesNotContain(fixture.Patient.Name, string.Join("\n", fixture.Log.Messages));
    }

    [Theory]
    [InlineData(NotificationAcceptance.Rejected, null, "failed")]
    [InlineData(NotificationAcceptance.Unknown, null, "unknown")]
    [InlineData(NotificationAcceptance.Accepted, null, "unknown")]
    public async Task Rejection_and_uncertain_handoff_never_record_sent(NotificationAcceptance acceptance, string? providerId, string outcome)
    {
        await using var fixture = new Fixture();
        fixture.Channel.Result = new(acceptance, providerId, "contains patient PHI and raw provider payload");
        await fixture.Deliver();
        await fixture.Deliver();
        Assert.Single(fixture.Channel.Messages);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        var log = await fixture.Db.NotificationLogs.SingleAsync();
        Assert.Equal(NotificationStatus.Failed, log.Status);
        Assert.Null(log.SentAt);
        Assert.Contains(fixture.Storage.Hashes.Values, h => h.GetValueOrDefault("outcome") == outcome);
        Assert.DoesNotContain("PHI", string.Join("\n", fixture.Log.Messages));
    }

    [Fact]
    public async Task Retry_budget_survives_new_service_instances_and_stops_at_three()
    {
        await using var fixture = new Fixture();
        fixture.Channel.Result = new(NotificationAcceptance.Retryable);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt < 3) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Deliver(fixture.Service()));
            else await fixture.Deliver(fixture.Service());
            var state = fixture.Storage.Hashes.Single(k => k.Key.Contains(":event:")).Value;
            Assert.Equal(attempt.ToString(), state["attempts"]);
            if (attempt < 3)
            {
                var seconds = (DateTimeOffset.Parse(state["next_due"]) - DateTimeOffset.UtcNow).TotalSeconds;
                Assert.InRange(seconds, (60 * Math.Pow(2, attempt - 1)) - 10, 60 * Math.Pow(2, attempt - 1));
                state["next_due"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O");
            }
        }
        await fixture.Deliver();
        Assert.Equal(3, fixture.Channel.Messages.Count);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        var logs = await fixture.Db.NotificationLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, l => Assert.Equal(NotificationStatus.Failed, l.Status));
        Assert.Contains(logs, l => l.FailedReason == "retry_exhausted");
    }

    [Fact]
    public async Task Two_conflicting_rules_share_patient_day_guard()
    {
        await using var fixture = new Fixture();
        var ruleA = fixture.AddRule(NotificationRuleType.RemindNDaysBefore);
        var ruleB = fixture.AddRule(NotificationRuleType.RemindNDaysBefore);
        await fixture.Deliver(ruleId: ruleA.Id, kind: NotificationRuleType.RemindNDaysBefore);
        await fixture.Deliver(ruleId: ruleB.Id, kind: NotificationRuleType.RemindNDaysBefore);
        Assert.Single(fixture.Channel.Messages);
        Assert.Contains(fixture.Storage.Hashes.Values, h => h.GetValueOrDefault("failure") == "patient_day_guard");
    }

    [Theory]
    [InlineData(0, NotificationStatus.Sent, false)]
    [InlineData(0, NotificationStatus.Delivered, false)]
    [InlineData(0, NotificationStatus.Failed, true)]
    [InlineData(-1, NotificationStatus.Sent, true)]
    public async Task Existing_history_respects_clinic_calendar_day_without_rewriting(int daysAgo, NotificationStatus status, bool shouldSend)
    {
        await using var fixture = new Fixture();
        using (fixture.Context.PushTenant(fixture.TenantId))
        {
            var today = new TimezoneService().GetClinicToday(fixture.Appointment.Clinic.Timezone);
            fixture.Db.NotificationLogs.Add(new NotificationLog { Id = Guid.NewGuid(), AppointmentId = fixture.Appointment.Id,
                Status = status, SentAt = new DateTimeOffset(new TimezoneService().GetStartOfDayUtc(today.AddDays(daysAgo), fixture.Appointment.Clinic.Timezone)) });
            await fixture.Db.SaveChangesAsync();
        }
        await fixture.Deliver();
        Assert.Equal(shouldSend ? 1 : 0, fixture.Channel.Messages.Count);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        Assert.Equal(shouldSend ? 2 : 1, await fixture.Db.NotificationLogs.CountAsync());
    }

    [Theory]
    [InlineData(NotificationRuleType.AppointmentConfirmation, -1, "{}")]
    [InlineData(NotificationRuleType.AppointmentReminder, 2, "{}")]
    [InlineData(NotificationRuleType.RemindNDaysBefore, 1, "{\"daysBefore\":2}")]
    [InlineData(NotificationRuleType.RemindNDaysBefore, 1, "{\"daysBefore\":\"one\"}")]
    [InlineData(NotificationRuleType.RemindNDaysBefore, 1, "[]")]
    [InlineData(NotificationRuleType.RemindNDaysBefore, 1, "not-json")]
    public async Task Delayed_jobs_recheck_appointment_and_rule_timing(NotificationRuleType kind, int appointmentOffset, string timing)
    {
        await using var fixture = new Fixture();
        var rule = fixture.AddRule(kind);
        using (fixture.Context.PushTenant(fixture.TenantId))
        {
            fixture.Appointment.Date = new TimezoneService().GetClinicToday(fixture.Appointment.Clinic.Timezone).AddDays(appointmentOffset);
            rule.TimingConfigJson = timing;
            await fixture.Db.SaveChangesAsync();
        }
        await fixture.Deliver(ruleId: rule.Id, kind: kind);
        Assert.Empty(fixture.Channel.Messages);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        Assert.Equal(NotificationStatus.Failed, (await fixture.Db.NotificationLogs.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("channel_not_configured")]
    [InlineData("channel_secret_unavailable")]
    [InlineData("channel_payload_invalid")]
    public async Task Known_failure_codes_remain_actionable(string failure)
    {
        await using var fixture = new Fixture();
        fixture.Channel.Result = new(NotificationAcceptance.Rejected, FailureCode: failure);
        await fixture.Deliver();
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        Assert.Equal(failure, (await fixture.Db.NotificationLogs.SingleAsync()).FailedReason);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("expired", false)]
    [InlineData("submitted", false)]
    [InlineData("walkin", false)]
    [InlineData("wrong_token", false)]
    public async Task Queued_precheck_survives_key_reload_and_is_revalidated_before_same_message(string state, bool includeLink)
    {
        var keys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "opencode", "part2-precheck-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var fixture = new Fixture();
            fixture.Protection = DataProtectionProvider.Create(new DirectoryInfo(keys));
            const string token = "synthetic-original-precheck-token";
            const string link = "https://clinic.example.test/intake/" + token;
            var submission = new PrecheckSubmission { Id = Guid.NewGuid(), TenantId = fixture.TenantId,
                AppointmentId = fixture.Appointment.Id, TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(4) };
            using (fixture.Context.PushTenant(fixture.TenantId))
            {
                fixture.Appointment.Type = AppointmentType.Scheduled;
                fixture.Template.Content += " {{precheck_link}}";
                fixture.Db.PrecheckSubmissions.Add(submission);
                await fixture.Db.SaveChangesAsync();
                await fixture.Service().QueueAppointmentAsync(fixture.Appointment.Id, null, NotificationRuleType.AppointmentConfirmation, link, default);
                await fixture.Service().QueueAppointmentAsync(fixture.Appointment.Id, null, NotificationRuleType.AppointmentConfirmation, link, default);
            }
            var queued = Assert.Single(fixture.Jobs.Jobs);
            var persistedJob = InvocationData.SerializeJob(queued).SerializePayload();
            Assert.DoesNotContain(token, persistedJob);
            queued = InvocationData.DeserializePayload(persistedJob).DeserializeJob();
            Assert.DoesNotContain(token, JsonSerializer.Serialize(fixture.Storage.Hashes));
            Assert.InRange(Assert.Single(fixture.Storage.Expirations).Value.TotalHours, 3.9, 4);

            fixture.Protection = DataProtectionProvider.Create(new DirectoryInfo(keys));
            using (fixture.Context.PushTenant(fixture.TenantId))
            {
                if (state == "expired") submission.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                if (state == "submitted") submission.SubmittedAt = DateTimeOffset.UtcNow;
                if (state == "walkin") fixture.Appointment.Type = AppointmentType.WalkIn;
                if (state == "wrong_token") submission.TokenHash = "another-token-hash";
                await fixture.Db.SaveChangesAsync();
                // A reminder without plaintext reuses the original protected material.
                await fixture.Service().QueueAppointmentAsync(fixture.Appointment.Id, null, NotificationRuleType.AppointmentReminder, null, default);
                Assert.Equal(queued.Args[6], fixture.Jobs.Jobs[1].Args[6]);
            }
            await (Task)queued.Method.Invoke(fixture.Service(), queued.Args.ToArray())!;
            var message = Assert.Single(fixture.Channel.Messages);
            Assert.Equal(includeLink ? link : "", message.Parameters[^1]);
            Assert.DoesNotContain(token, string.Join("\n", fixture.Log.Messages));
        }
        finally { if (Directory.Exists(keys)) Directory.Delete(keys, true); }
    }

    [Fact]
    public async Task Interrupted_attempt_blocks_automatic_resend()
    {
        await using var fixture = new Fixture();
        var key = $"notification:{fixture.TenantId:N}:event:{fixture.Appointment.Id:N}::AppointmentConfirmation:{fixture.Appointment.Date:yyyyMMdd}";
        fixture.Storage.Hashes[key] = new() { ["outcome"] = "sending", ["attempts"] = "1" };
        await fixture.Deliver();
        Assert.Empty(fixture.Channel.Messages);
        Assert.Equal("unknown", fixture.Storage.Hashes[key]["outcome"]);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        Assert.Equal("provider_acceptance_unknown", (await fixture.Db.NotificationLogs.SingleAsync()).FailedReason);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("template")]
    [InlineData("rule")]
    public async Task Consent_approved_template_and_active_rule_are_checked_at_dispatch(string missing)
    {
        await using var fixture = new Fixture();
        using (fixture.Context.PushTenant(fixture.TenantId))
        {
            if (missing == "consent") (await fixture.Db.PatientConsents.SingleAsync()).WithdrawnAt = DateTimeOffset.UtcNow;
            if (missing == "template") fixture.Template.ApprovalStatus = TemplateApprovalStatus.Pending;
            if (missing == "rule") fixture.AddRule(NotificationRuleType.AppointmentConfirmation).Active = false;
            await fixture.Db.SaveChangesAsync();
        }
        await fixture.Deliver();
        Assert.Empty(fixture.Channel.Messages);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        Assert.Equal(NotificationStatus.Failed, (await fixture.Db.NotificationLogs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Patient_only_no_visit_rule_is_visibly_disabled_without_fake_appointment()
    {
        await using var fixture = new Fixture();
        var rule = fixture.AddRule(NotificationRuleType.RemindIfNoVisitNMonths);
        using var tenant = fixture.Context.PushTenant(fixture.TenantId);
        await fixture.Service().SendRuleAsync(fixture.Patient.Id, null, rule.Id);
        Assert.Empty(fixture.Channel.Messages);
        Assert.Empty(await fixture.Db.NotificationLogs.ToListAsync());
        Assert.Contains(fixture.Log.Messages, m => m.Contains("no_visit_policy_pending"));
    }

    [Theory]
    [InlineData("ClinicAdmin", false, false)]
    [InlineData("Doctor", false, false)]
    [InlineData("Nurse", false, false)]
    [InlineData("Receptionist", false, false)]
    [InlineData("Owner", false, false)]
    [InlineData(null, false, false)]
    [InlineData("PlatformAdmin", true, false)]
    [InlineData("PlatformAdmin", false, true)]
    public void Dashboard_only_allows_non_impersonating_platform_operator(string? role, bool impersonation, bool expected)
    {
        var claims = new List<Claim>();
        if (role != null) claims.Add(new(ClaimTypes.Role, role));
        if (impersonation) claims.Add(new("impersonation_id", Guid.NewGuid().ToString()));
        using var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity(claims, role == null ? null : "test")) };
        var dashboard = new AspNetCoreDashboardContext(new MemoryJobStorage(), new DashboardOptions(), http);
        Assert.Equal(expected, new HangfireAdminAuthorizationFilter().Authorize(dashboard));
    }

    [Fact]
    public async Task Razorpay_worker_remains_disabled_even_with_keys_and_enabled_switch()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Razorpay:KeyId"] = "synthetic", ["Razorpay:KeySecret"] = "synthetic", ["Razorpay:ReconciliationEnabled"] = "true" }).Build();
        var log = new CapturedLogger<RazorpayReconciliationWorker>();
        using var worker = new RazorpayReconciliationWorker(log, config, services.GetRequiredService<IServiceScopeFactory>());
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
        Assert.Contains(log.Messages, m => m.Contains("reconciliation disabled"));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public TenantContext Context { get; } = new();
        public HospitalCrmDbContext Db { get; }
        public Patient Patient { get; }
        public Appointment Appointment { get; }
        public MessageTemplate Template { get; }
        public MemoryJobStorage Storage { get; } = new();
        public CaptureJobs Jobs { get; } = new();
        public TestChannel Channel { get; } = new();
        public CapturedLogger<StubNotificationService> Log { get; } = new();
        public IDataProtectionProvider Protection { get; set; } = new EphemeralDataProtectionProvider();
        public Fixture()
        {
            Db = new HospitalCrmDbContext(new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Context);
            using var tenant = Context.PushTenant(TenantId);
            Db.Tenants.Add(new Tenant { Id = TenantId, ActivatedModules = "core,notifications" });
            var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Synthetic clinic", Timezone = "Asia/Kolkata" };
            Patient = new Patient { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Synthetic patient private", Phone = "+919876543210" };
            Appointment = new Appointment { Id = Guid.NewGuid(), TenantId = TenantId, Clinic = clinic, Patient = Patient,
                Date = new TimezoneService().GetClinicToday(clinic.Timezone).AddDays(1), TimeSlot = "10:00", Status = AppointmentStatus.Booked };
            Template = new MessageTemplate { Id = Guid.NewGuid(), TenantId = TenantId, Name = "appointment_confirmation", Channel = NotificationChannel.WhatsApp,
                Content = "Hello {{patient_name}} at {{clinic_name}} on {{date}} {{time}}", ApprovalStatus = TemplateApprovalStatus.Approved };
            Db.Appointments.Add(Appointment);
            Db.MessageTemplates.Add(Template);
            Db.PatientConsents.Add(new PatientConsent { Id = Guid.NewGuid(), TenantId = TenantId, Patient = Patient, Purpose = "whatsapp_opt_in" });
            Db.SaveChanges();
        }
        public NotificationRule AddRule(NotificationRuleType kind)
        {
            using var tenant = Context.PushTenant(TenantId);
            var rule = new NotificationRule { Id = Guid.NewGuid(), TenantId = TenantId, Template = Template, RuleType = kind, TimingConfigJson = "{\"daysBefore\":1}", Active = true };
            Db.NotificationRules.Add(rule);
            Db.SaveChanges();
            return rule;
        }
        public StubNotificationService Service() => new(Db, Context, Jobs, Storage, Channel, Protection,
            new TimezoneService(), new ModuleGateService(Db), Log);
        public Task Deliver(StubNotificationService? service = null, Guid? ruleId = null, NotificationRuleType kind = NotificationRuleType.AppointmentConfirmation) =>
            (service ?? Service()).DeliverAsync(TenantId, Patient.Id, Appointment.Id, ruleId, kind, Appointment.Date, null, default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    internal sealed class TestChannel : INotificationChannel
    {
        public List<NotificationChannelMessage> Messages { get; } = [];
        public NotificationChannelResult Result { get; set; } = new(NotificationAcceptance.Accepted, "wamid.synthetic");
        public Task<NotificationChannelResult> SendAsync(NotificationChannelMessage message, CancellationToken ct)
        { Messages.Add(message); return Task.FromResult(Result); }
    }

    internal sealed class CaptureJobs : IBackgroundJobClient
    {
        public List<Job> Jobs { get; } = [];
        public Action? BeforeCreate { get; set; }
        public Action? AfterCreate { get; set; }
        public string Create(Job job, IState state)
        {
            BeforeCreate?.Invoke();
            Jobs.Add(job);
            AfterCreate?.Invoke();
            return Jobs.Count.ToString();
        }
        public bool ChangeState(string jobId, IState state, string? expectedState) => throw new NotSupportedException();
    }

    internal sealed class MemoryJobStorage : JobStorage
    {
        public Dictionary<string, Dictionary<string, string>> Hashes { get; } = [];
        public Dictionary<string, TimeSpan> Expirations { get; } = [];
        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
        public override IStorageConnection GetConnection()
        {
            var proxy = DispatchProxy.Create<IStorageConnection, StorageProxy>();
            ((StorageProxy)(object)proxy).Store = this;
            return proxy;
        }
    }

    public class StorageProxy : DispatchProxy
    {
        internal MemoryJobStorage Store = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "Dispose": return null;
                case "AcquireDistributedLock": return new NoopDisposable();
                case "CreateWriteTransaction": return new MemoryTransaction(Store);
                case "GetAllEntriesFromHash": return Store.Hashes.TryGetValue((string)args![0]!, out var hash) ? new Dictionary<string, string>(hash) : null;
                case "SetRangeInHash":
                    var key = (string)args![0]!;
                    if (!Store.Hashes.ContainsKey(key)) Store.Hashes[key] = [];
                    foreach (var value in (IEnumerable<KeyValuePair<string, string>>)args[1]!) Store.Hashes[key][value.Key] = value.Value;
                    return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    internal sealed class MemoryTransaction(MemoryJobStorage store) : JobStorageTransaction
    {
        private readonly Dictionary<string, Dictionary<string, string>> _writes = [];
        private readonly Dictionary<string, TimeSpan> _expirations = [];
        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> values) => _writes[key] = values.ToDictionary();
        public override void ExpireHash(string key, TimeSpan expireIn) => _expirations[key] = expireIn;
        public override void Commit()
        {
            foreach (var (key, value) in _writes) store.Hashes[key] = value;
            foreach (var (key, value) in _expirations) store.Expirations[key] = value;
        }
        public override void Dispose() { }
        public override void ExpireJob(string jobId, TimeSpan expireIn) => throw new NotSupportedException();
        public override void PersistJob(string jobId) => throw new NotSupportedException();
        public override void SetJobState(string jobId, IState state) => throw new NotSupportedException();
        public override void AddJobState(string jobId, IState state) => throw new NotSupportedException();
        public override void AddToQueue(string queue, string jobId) => throw new NotSupportedException();
        public override void IncrementCounter(string key) => throw new NotSupportedException();
        public override void IncrementCounter(string key, TimeSpan expireIn) => throw new NotSupportedException();
        public override void DecrementCounter(string key) => throw new NotSupportedException();
        public override void DecrementCounter(string key, TimeSpan expireIn) => throw new NotSupportedException();
        public override void AddToSet(string key, string value) => throw new NotSupportedException();
        public override void AddToSet(string key, string value, double score) => throw new NotSupportedException();
        public override void RemoveFromSet(string key, string value) => throw new NotSupportedException();
        public override void InsertToList(string key, string value) => throw new NotSupportedException();
        public override void RemoveFromList(string key, string value) => throw new NotSupportedException();
        public override void TrimList(string key, int keepStartingFrom, int keepEndingAt) => throw new NotSupportedException();
        public override void RemoveHash(string key) => throw new NotSupportedException();
    }

    internal sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    internal sealed class CapturedLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
