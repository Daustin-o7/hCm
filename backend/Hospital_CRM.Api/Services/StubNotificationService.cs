using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.Storage;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

// Retains the registered service name; outcomes now require a real provider acknowledgement.
public sealed class StubNotificationService(HospitalCrmDbContext db, ITenantContext tenant,
    IBackgroundJobClient jobs, JobStorage storage, INotificationChannel channel,
    IDataProtectionProvider protection, ITimezoneService timezones, IModuleGateService modules,
    ILogger<StubNotificationService> logger) : INotificationService
{
    public NotificationIntent CreateConfirmationIntent(Appointment appointment, string? precheckLink)
    {
        var tenantId = RequireTenant();
        if (appointment.TenantId != tenantId) throw new InvalidOperationException("notification_owner_mismatch");
        return new NotificationIntent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment.Id,
            AppointmentDate = appointment.Date,
            CreatedAt = DateTimeOffset.UtcNow,
            ProtectedPrecheckLink = string.IsNullOrEmpty(precheckLink) ? null : LinkProtector(tenantId, appointment.Id).Protect(precheckLink)
        };
    }

    public async Task HandoffConfirmationAsync(Guid intentId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        try
        {
            var intent = await db.Set<NotificationIntent>().Include(i => i.Appointment).SingleOrDefaultAsync(i =>
                i.Id == intentId && i.TenantId == tenantId && i.Appointment.TenantId == tenantId, ct);
            if (intent is null || intent.HandedOffAt.HasValue) return;
            await QueueProtectedAsync(intent.Appointment, null, NotificationRuleType.AppointmentConfirmation,
                intent.AppointmentDate, intent.ProtectedPrecheckLink, ct);
            intent.HandedOffAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The committed outbox survives cancellation, storage outages and a crash after enqueue.
            logger.LogWarning("Notification handoff pending for tenant {TenantId}, intent {IntentId}: {FailureType}",
                tenantId, intentId, ex.GetType().Name);
        }
    }

    public Task SendAppointmentConfirmationAsync(Guid appointmentId, string patientPhone, string clinicName,
        DateTime dateTime, string? precheckLink = null) =>
        QueueAppointmentAsync(appointmentId, null, NotificationRuleType.AppointmentConfirmation, precheckLink, CancellationToken.None);

    public Task SendAppointmentReminderAsync(Guid appointmentId, string patientPhone, string clinicName,
        DateTime dateTime, string? precheckLink = null) =>
        QueueAppointmentAsync(appointmentId, null, NotificationRuleType.AppointmentReminder, precheckLink, CancellationToken.None);

    public async Task SendRuleAsync(Guid patientId, Guid? appointmentId, Guid ruleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var rule = await db.NotificationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId && r.TenantId == tenantId, ct);
        if (rule is null || !rule.Active) return;
        if (rule.RuleType == NotificationRuleType.RemindIfNoVisitNMonths)
        {
            logger.LogWarning("Notification rule {RuleId} disabled: no_visit_policy_pending", rule.Id);
            return;
        }
        if (appointmentId is null || !await db.Appointments.AnyAsync(a => a.Id == appointmentId &&
                a.TenantId == tenantId && a.PatientId == patientId, ct))
            throw new InvalidOperationException("notification_owner_mismatch");
        await QueueAppointmentAsync(appointmentId.Value, ruleId, rule.RuleType, null, ct);
    }

    public async Task QueueAppointmentAsync(Guid appointmentId, Guid? ruleId, NotificationRuleType kind,
        string? precheckLink, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a =>
            a.Id == appointmentId && a.TenantId == tenantId, ct);
        if (appointment is null) throw new InvalidOperationException("notification_owner_mismatch");
        // Callers' phone/clinic/time are deliberately ignored; recipient data is resolved at dispatch.
        var protectedLink = string.IsNullOrEmpty(precheckLink) ? null :
            LinkProtector(tenantId, appointmentId).Protect(precheckLink);
        await QueueProtectedAsync(appointment, ruleId, kind, appointment.Date, protectedLink, ct);
    }

    private async Task QueueProtectedAsync(Appointment appointment, Guid? ruleId, NotificationRuleType kind,
        DateOnly appointmentDate, string? protectedLink, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var appointmentId = appointment.Id;
        ct.ThrowIfCancellationRequested();
        using var connection = storage.GetConnection();
        var eventKey = EventKey(tenantId, appointmentId, ruleId, kind, appointmentDate);
        using var queueLock = connection.AcquireDistributedLock(eventKey + ":queue", TimeSpan.FromSeconds(10));
        var state = connection.GetAllEntriesFromHash(eventKey);
        if (state?.ContainsKey("job_id") == true || state?.ContainsKey("outcome") == true) return;
        var precheckKey = $"notification:{tenantId:N}:precheck:{appointmentId:N}";
        if (protectedLink != null)
        {
            var expiresAt = await db.PrecheckSubmissions.Where(p => p.TenantId == tenantId && p.AppointmentId == appointmentId &&
                p.SubmittedAt == null && p.ExpiresAt > DateTimeOffset.UtcNow).Select(p => (DateTimeOffset?)p.ExpiresAt).FirstOrDefaultAsync(ct);
            if (expiresAt.HasValue && connection.GetAllEntriesFromHash(precheckKey) == null)
            {
                using var transaction = connection.CreateWriteTransaction();
                if (transaction is not JobStorageTransaction expiringTransaction)
                    throw new InvalidOperationException("notification_storage_expiry_required");
                transaction.SetRangeInHash(precheckKey, new Dictionary<string, string> { ["protected_link"] = protectedLink });
                expiringTransaction.ExpireHash(precheckKey, expiresAt.Value - DateTimeOffset.UtcNow);
                transaction.Commit();
            }
        }
        protectedLink = connection.GetAllEntriesFromHash(precheckKey)?.GetValueOrDefault("protected_link");
        ct.ThrowIfCancellationRequested();
        var jobId = jobs.Enqueue<StubNotificationService>(s => s.DeliverAsync(tenantId, appointment.PatientId,
            appointmentId, ruleId, kind, appointmentDate, protectedLink, CancellationToken.None));
        if (string.IsNullOrWhiteSpace(jobId)) throw new InvalidOperationException("notification_enqueue_failed");
        connection.SetRangeInHash(eventKey, new Dictionary<string, string> { ["job_id"] = jobId });
    }

    // Hangfire persists retry scheduling. The ledger also bounds manual requeue/restarts to three attempts.
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 120], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task DeliverAsync(Guid tenantId, Guid patientId, Guid appointmentId, Guid? ruleId,
        NotificationRuleType kind, DateOnly appointmentDate, string? protectedPrecheckLink, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || (tenant.CurrentTenantId.HasValue && tenant.CurrentTenantId != tenantId))
            throw new InvalidOperationException("notification_tenant_required");
        using var tenantScope = tenant.PushTenant(tenantId);
        var appointment = await db.Appointments.AsNoTracking().Include(a => a.Patient).Include(a => a.Clinic)
            .SingleOrDefaultAsync(a => a.Id == appointmentId && a.TenantId == tenantId &&
                a.PatientId == patientId && a.Patient.TenantId == tenantId && a.Clinic.TenantId == tenantId, ct);
        if (appointment is null) throw new InvalidOperationException("notification_owner_mismatch");
        using var connection = storage.GetConnection();
        // One lock serializes all occurrences/rules for this patient, including retries crossing midnight.
        using var sendLock = connection.AcquireDistributedLock($"notification:{tenantId:N}:{patientId:N}:send", TimeSpan.FromSeconds(30));
        var eventKey = EventKey(tenantId, appointmentId, ruleId, kind, appointmentDate);
        var state = connection.GetAllEntriesFromHash(eventKey) ?? new Dictionary<string, string>();
        var outcome = state.GetValueOrDefault("outcome");
        if (outcome is "accepted" or "failed" or "skipped" or "unknown") return;
        if (outcome == "sending")
        {
            connection.SetRangeInHash(eventKey, new Dictionary<string, string> { ["outcome"] = "unknown", ["failure"] = "interrupted_acceptance_unknown" });
            await RecordAsync(appointment, ruleId, kind.ToString(), NotificationStatus.Failed, "provider_acceptance_unknown", ct);
            logger.LogWarning("Notification acceptance requires reconciliation for tenant {TenantId}, appointment {AppointmentId}", tenantId, appointmentId);
            return;
        }
        var attempts = int.TryParse(state.GetValueOrDefault("attempts"), out var count) ? count : 0;
        if (attempts >= 3) return;
        if (DateTimeOffset.TryParse(state.GetValueOrDefault("next_due"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var nextDue) && nextDue > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("notification_retry_not_due");

        var localNow = timezones.GetLocalNow(clinicTimezone: appointment.Clinic.Timezone);
        var today = DateOnly.FromDateTime(localNow);
        var dayKey = $"notification:{tenantId:N}:{patientId:N}:day:{today:yyyyMMdd}";
        var day = connection.GetAllEntriesFromHash(dayKey);
        var dayStart = new DateTimeOffset(timezones.GetStartOfDayUtc(today, appointment.Clinic.Timezone));
        var dayEnd = new DateTimeOffset(timezones.GetStartOfDayUtc(today.AddDays(1), appointment.Clinic.Timezone));
        // Existing Phase 1 history must also block migration-era duplicate sends.
        var sentToday = await db.NotificationLogs.AnyAsync(n => n.Appointment.TenantId == tenantId &&
            n.Appointment.PatientId == patientId && (n.Status == NotificationStatus.Sent || n.Status == NotificationStatus.Delivered) &&
            ((n.SentAt >= dayStart && n.SentAt < dayEnd) ||
             (n.SentAt == null && n.DeliveredAt >= dayStart && n.DeliveredAt < dayEnd)), ct);
        if (sentToday || day?.GetValueOrDefault("outcome") is "accepted" or "unknown" or "sending")
        {
            connection.SetRangeInHash(eventKey, new Dictionary<string, string> { ["outcome"] = "skipped", ["failure"] = "patient_day_guard" });
            return;
        }

        var rule = ruleId.HasValue
            ? await db.NotificationRules.AsNoTracking().Include(r => r.Template).SingleOrDefaultAsync(r =>
                r.Id == ruleId && r.TenantId == tenantId, ct)
            : await db.NotificationRules.AsNoTracking().Include(r => r.Template).Where(r =>
                r.TenantId == tenantId && r.RuleType == kind).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).FirstOrDefaultAsync(ct);
        var templateName = kind == NotificationRuleType.AppointmentConfirmation ? "appointment_confirmation" : "appointment_reminder";
        var template = rule?.Template;
        if (template is null && !ruleId.HasValue && rule is null)
            template = await db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Name == templateName, ct);

        string? failure = null;
        if (appointment.Date != appointmentDate || appointment.Status != AppointmentStatus.Booked ||
            !TimeOnly.TryParse(appointment.TimeSlot, CultureInfo.InvariantCulture, DateTimeStyles.None, out var slot) ||
            appointment.Date.ToDateTime(slot) <= localNow) failure = "appointment_no_longer_eligible";
        else if (kind == NotificationRuleType.RemindIfNoVisitNMonths) failure = "no_visit_policy_pending";
        else if (kind is not (NotificationRuleType.AppointmentConfirmation or NotificationRuleType.AppointmentReminder or NotificationRuleType.RemindNDaysBefore)) failure = "rule_type_not_supported";
        else if (ruleId.HasValue && rule == null || rule is { Active: false } || rule != null && rule.RuleType != kind) failure = "rule_disabled";
        else if (kind == NotificationRuleType.AppointmentReminder && appointment.Date.DayNumber - today.DayNumber != 1 ||
                 kind == NotificationRuleType.RemindNDaysBefore &&
                 (rule is null || NotificationRulesWorker.GetDaysBefore(rule.TimingConfigJson) != appointment.Date.DayNumber - today.DayNumber))
            failure = "rule_timing_no_longer_eligible";
        else if (ruleId.HasValue && !await modules.IsModuleEnabledAsync(tenantId, ModuleRegistry.Notifications, ct)) failure = "notifications_module_disabled";
        else if (!await db.PatientConsents.AnyAsync(c => c.TenantId == tenantId && c.PatientId == patientId &&
                     c.Purpose == "whatsapp_opt_in" && c.WithdrawnAt == null, ct)) failure = "consent_missing";
        else if (template is null || template.TenantId != tenantId || template.ApprovalStatus != TemplateApprovalStatus.Approved) failure = "approved_template_required";

        string? precheckLink = null;
        if (failure is null && protectedPrecheckLink != null)
        {
            try
            {
                var candidate = LinkProtector(tenantId, appointmentId).Unprotect(protectedPrecheckLink);
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Segments.Length > 1)
                {
                    var token = Uri.UnescapeDataString(uri.Segments[^1]);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                    if (appointment.Type != AppointmentType.WalkIn && await db.PrecheckSubmissions.AnyAsync(p =>
                        p.TenantId == tenantId && p.AppointmentId == appointmentId && p.TokenHash == hash &&
                        p.SubmittedAt == null && p.ExpiresAt > DateTimeOffset.UtcNow, ct)) precheckLink = candidate;
                }
            }
            catch (CryptographicException) { failure = "precheck_material_unreadable"; }
        }

        var parameters = new List<string>();
        if (failure is null)
        {
            // Parameters follow their first occurrence in the approved template, never raw body logging.
            foreach (var placeholder in Regex.Matches(template!.Content, @"\{\{\s*([a-z_]+)\s*\}\}")
                         .Select(m => m.Groups[1].Value).Distinct())
            {
                var value = placeholder switch
                {
                    "patient_name" => appointment.Patient.Name,
                    "clinic_name" => appointment.Clinic.Name,
                    "date" => appointment.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "time" => appointment.TimeSlot,
                    "precheck_link" => precheckLink ?? "",
                    _ => null
                };
                if (value is null) { failure = "template_placeholder_unsupported"; break; }
                parameters.Add(value);
            }
            if (precheckLink != null && !Regex.IsMatch(template!.Content, @"\{\{\s*precheck_link\s*\}\}"))
                failure = "precheck_template_required";
        }
        if (failure != null)
        {
            connection.SetRangeInHash(eventKey, new Dictionary<string, string> { ["outcome"] = "failed", ["failure"] = failure });
            await RecordAsync(appointment, ruleId ?? rule?.Id, template?.Name ?? templateName, NotificationStatus.Failed, failure, ct,
                template?.Channel ?? NotificationChannel.WhatsApp);
            return;
        }

        // Persist the uncertain state before I/O. A crash never silently grants permission to resend.
        attempts++;
        connection.SetRangeInHash(eventKey, new Dictionary<string, string> { ["outcome"] = "sending", ["attempts"] = attempts.ToString(CultureInfo.InvariantCulture) });
        connection.SetRangeInHash(dayKey, new Dictionary<string, string> { ["outcome"] = "sending", ["event"] = eventKey });
        NotificationChannelResult result;
        try { result = await channel.SendAsync(new(tenantId, template!.Channel, appointment.Patient.Phone, template.Name, parameters), ct); }
        catch (Exception)
        {
            // An implementation exception may occur after handoff. Persist ambiguity without its PHI-bearing message.
            result = new(NotificationAcceptance.Unknown, FailureCode: "provider_acceptance_unknown");
        }
        if (result.Acceptance == NotificationAcceptance.Accepted && string.IsNullOrWhiteSpace(result.ProviderMessageId))
            result = new(NotificationAcceptance.Unknown, FailureCode: "provider_acceptance_unknown");
        var accepted = result.Acceptance == NotificationAcceptance.Accepted;
        var retry = result.Acceptance == NotificationAcceptance.Retryable && attempts < 3;
        var finalOutcome = accepted ? "accepted" : result.Acceptance == NotificationAcceptance.Unknown ? "unknown" : retry ? "retry" : "failed";
        var reason = result.Acceptance switch
        {
            NotificationAcceptance.Accepted => "",
            NotificationAcceptance.Unknown => "provider_acceptance_unknown",
            NotificationAcceptance.Retryable => attempts >= 3 ? "retry_exhausted" : "provider_retryable",
            _ => result.FailureCode is "channel_not_configured" or "channel_secret_unavailable" or "channel_payload_invalid"
                ? result.FailureCode : "provider_rejected"
        };
        connection.SetRangeInHash(eventKey, new Dictionary<string, string>
        {
            ["outcome"] = finalOutcome,
            ["failure"] = reason,
            ["provider_id"] = accepted ? result.ProviderMessageId! : "",
            ["next_due"] = retry ? DateTimeOffset.UtcNow.AddSeconds(60 * Math.Pow(2, attempts - 1)).ToString("O") : ""
        });
        connection.SetRangeInHash(dayKey, new Dictionary<string, string> { ["outcome"] = finalOutcome });
        await RecordAsync(appointment, ruleId ?? rule?.Id, template!.Name, accepted ? NotificationStatus.Sent : NotificationStatus.Failed,
            accepted ? null : reason, ct, template.Channel);
        logger.LogInformation("Notification attempt {Attempt} for tenant {TenantId}, appointment {AppointmentId}: {Outcome}",
            attempts, tenantId, appointmentId, finalOutcome);
        if (retry) throw new InvalidOperationException("notification_provider_retryable");
    }

    private async Task RecordAsync(Appointment appointment, Guid? ruleId, string template,
        NotificationStatus status, string? failure, CancellationToken ct, NotificationChannel channel = NotificationChannel.WhatsApp)
    {
        db.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Channel = channel,
            Template = template,
            RuleId = ruleId,
            Status = status,
            SentAt = status == NotificationStatus.Sent ? DateTimeOffset.UtcNow : null,
            FailedReason = failure
        });
        await db.SaveChangesAsync(ct);
    }

    private Guid RequireTenant() => tenant.CurrentTenantId is { } id && id != Guid.Empty
        ? id : throw new InvalidOperationException("notification_tenant_required");

    private IDataProtector LinkProtector(Guid tenantId, Guid appointmentId) => protection.CreateProtector(
        "HospitalCrm.NotificationPrecheck.v1", tenantId.ToString("N"), appointmentId.ToString("N"));

    private static string EventKey(Guid tenantId, Guid appointmentId, Guid? ruleId, NotificationRuleType kind, DateOnly date) =>
        $"notification:{tenantId:N}:event:{appointmentId:N}:{ruleId:N}:{kind}:{date:yyyyMMdd}";
}
