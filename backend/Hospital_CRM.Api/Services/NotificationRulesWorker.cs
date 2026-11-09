using System.Text.Json;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public sealed class NotificationRulesWorker(
    IServiceScopeFactory scopes,
    ILogger<NotificationRulesWorker> logger,
    IConfiguration? configuration = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await EvaluateAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError("Notification rule cycle failed: {FailureType}", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task EvaluateAsync(CancellationToken ct)
    {
        List<Guid> tenantIds;
        using (var inventory = scopes.CreateScope())
        {
            var context = inventory.ServiceProvider.GetRequiredService<ITenantContext>();
            using var maintenance = context.PushMaintenance();
            var db = inventory.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
            tenantIds = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);
        }
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            using var tenantScope = context.PushTenant(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
            var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();
            // Confirmation intent is committed with booking, even if the request dies before enqueue.
            var pending = await db.Set<NotificationIntent>().AsNoTracking().Where(i =>
                i.TenantId == tenantId && i.HandedOffAt == null).OrderBy(i => i.CreatedAt)
                .Select(i => i.Id).ToListAsync(ct);
            foreach (var intentId in pending)
            {
                ct.ThrowIfCancellationRequested();
                await notifier.HandoffConfirmationAsync(intentId, ct);
            }
            var timezones = scope.ServiceProvider.GetRequiredService<ITimezoneService>();
            var rules = await db.NotificationRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.Active).ToListAsync(ct);
            foreach (var rule in rules)
            {
                if (rule.RuleType == NotificationRuleType.RemindIfNoVisitNMonths)
                {
                    var isPolicyApproved = configuration?.GetValue<bool>("NotificationRules:NoVisitPolicyApproved") ?? false;
                    if (!isPolicyApproved)
                    {
                        logger.LogWarning("Notification rule {RuleId} disabled: no_visit_policy_pending", rule.Id);
                        continue;
                    }

                    var months = GetMonths(rule.TimingConfigJson);
                    if (!months.HasValue || months.Value <= 0)
                    {
                        logger.LogWarning("Notification rule {RuleId} disabled: invalid_months", rule.Id);
                        continue;
                    }

                    var clinics = await db.Clinics.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(ct);
                    foreach (var clinic in clinics)
                    {
                        var today = timezones.GetClinicToday(clinic.Timezone);
                        var eligiblePatients = await db.Patients.AsNoTracking()
                            .Where(p => p.TenantId == tenantId &&
                                p.Consents.Any(c => c.TenantId == tenantId && c.Purpose == "whatsapp_opt_in" && c.WithdrawnAt == null) &&
                                p.Appointments.Any(a => a.TenantId == tenantId && a.ClinicId == clinic.Id))
                            .Include(p => p.Appointments)
                                .ThenInclude(a => a.Consultations)
                            .ToListAsync(ct);

                        foreach (var patient in eligiblePatients)
                        {
                            var clinicAppointments = patient.Appointments.Where(a => a.ClinicId == clinic.Id);
                            if (NoVisitPolicyEngine.QualifiesForNoVisitRecall(today, months.Value, clinicAppointments, out _))
                            {
                                await notifier.SendRuleAsync(patient.Id, null, rule.Id, ct);
                            }
                        }
                    }
                    continue;
                }
                // Booking owns confirmations, ReminderSchedulerService owns the default reminder.
                if (rule.RuleType != NotificationRuleType.RemindNDaysBefore) continue;
                try
                {
                    var daysBefore = GetDaysBefore(rule.TimingConfigJson);
                    if (!daysBefore.HasValue)
                    {
                        logger.LogWarning("Notification rule {RuleId} disabled: invalid_days_before", rule.Id);
                        continue;
                    }
                    var clinics = await db.Clinics.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(ct);
                    foreach (var clinic in clinics)
                    {
                        var today = timezones.GetClinicToday(clinic.Timezone);
                        if (daysBefore.Value > DateOnly.MaxValue.DayNumber - today.DayNumber) continue;
                        var targetDate = today.AddDays(daysBefore.Value);
                        var appointments = await db.Appointments.AsNoTracking().Where(a => a.TenantId == tenantId &&
                            a.ClinicId == clinic.Id && a.Date == targetDate && a.Status == AppointmentStatus.Booked &&
                            a.Patient.TenantId == tenantId && a.Patient.Consents.Any(c => c.TenantId == tenantId &&
                                c.Purpose == "whatsapp_opt_in" && c.WithdrawnAt == null))
                            .Select(a => new { a.Id, a.PatientId }).ToListAsync(ct);
                        foreach (var appointment in appointments)
                            await notifier.SendRuleAsync(appointment.PatientId, appointment.Id, rule.Id, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError("Notification rule {RuleId} for tenant {TenantId} failed: {FailureType}", rule.Id, tenantId, ex.GetType().Name); }
            }
        }
    }

    public static int? GetMonths(string timingConfig)
    {
        try
        {
            using var config = JsonDocument.Parse(timingConfig);
            return config.RootElement.ValueKind == JsonValueKind.Object &&
                config.RootElement.TryGetProperty("months", out var months) && months.ValueKind == JsonValueKind.Number &&
                months.TryGetInt32(out var value) && value > 0 ? value : null;
        }
        catch (JsonException) { return null; }
    }

    public static int? GetDaysBefore(string timingConfig)
    {
        try
        {
            using var config = JsonDocument.Parse(timingConfig);
            return config.RootElement.ValueKind == JsonValueKind.Object &&
                config.RootElement.TryGetProperty("daysBefore", out var days) && days.ValueKind == JsonValueKind.Number &&
                days.TryGetInt32(out var value) && value >= 0 ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
