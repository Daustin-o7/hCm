using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

// Owns AppointmentReminder defaults; the rules worker owns only custom N-days rules.
public sealed class ReminderSchedulerService(IServiceProvider services, ILogger<ReminderSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SendRemindersAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError("Reminder cycle failed: {FailureType}", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task SendRemindersAsync(CancellationToken ct)
    {
        List<Guid> tenantIds;
        using (var inventory = services.CreateScope())
        {
            var context = inventory.ServiceProvider.GetRequiredService<ITenantContext>();
            using var maintenance = context.PushMaintenance();
            var db = inventory.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
            tenantIds = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);
        }
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            using var tenantScope = context.PushTenant(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
            var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var timezones = scope.ServiceProvider.GetRequiredService<ITimezoneService>();
            try
            {
                var clinics = await db.Clinics.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(ct);
                foreach (var clinic in clinics)
                {
                    var tomorrow = timezones.GetClinicToday(clinic.Timezone).AddDays(1);
                    var appointments = await db.Appointments.AsNoTracking().Include(a => a.Patient)
                        .Where(a => a.TenantId == tenantId && a.ClinicId == clinic.Id && a.Date == tomorrow &&
                            a.Status == AppointmentStatus.Booked && a.Patient.TenantId == tenantId &&
                            a.Patient.Consents.Any(c => c.TenantId == tenantId && c.Purpose == "whatsapp_opt_in" && c.WithdrawnAt == null))
                        .ToListAsync(ct);
                    foreach (var appointment in appointments)
                    {
                        ct.ThrowIfCancellationRequested();
                        await notifier.SendAppointmentReminderAsync(appointment.Id, appointment.Patient.Phone, clinic.Name,
                            appointment.Date.ToDateTime(TimeOnly.Parse(appointment.TimeSlot)));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError("Reminders failed for tenant {TenantId}: {FailureType}", tenantId, ex.GetType().Name); }
        }
    }
}
