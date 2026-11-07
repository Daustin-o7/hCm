using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Hospital_CRM.Tests.Part2FilesJobsNotificationTests;

namespace Hospital_CRM.Tests;

public sealed class Part2FilesJobsWorkerTests
{
    [Fact]
    public async Task Rules_and_reminders_use_separate_explicit_tenant_contexts_and_respect_consent()
    {
        var database = Guid.NewGuid().ToString();
        var observed = new List<(Guid Tenant, Guid Appointment, bool Platform)>();
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext());
        services.AddScoped<HospitalCrmDbContext>(s => new Part2FilesJobsIntentDbContext(
            new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(database).Options,
            s.GetRequiredService<ITenantContext>()));
        services.AddSingleton<ITimezoneService, TimezoneService>();
        services.AddScoped<INotificationService>(s => new ScopedNotifier(s.GetRequiredService<ITenantContext>(), observed));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var expected = new List<(Guid Tenant, Guid Appointment, bool Platform)>();
        using (var seed = provider.CreateScope())
        {
            var tenant = seed.ServiceProvider.GetRequiredService<ITenantContext>();
            using var maintenance = tenant.PushMaintenance();
            var db = seed.ServiceProvider.GetRequiredService<HospitalCrmDbContext>();
            for (var index = 0; index < 2; index++)
            {
                var tenantId = Guid.NewGuid();
                db.Tenants.Add(new Tenant { Id = tenantId });
                var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Timezone = "Asia/Kolkata" };
                var template = new MessageTemplate { Id = Guid.NewGuid(), TenantId = tenantId, Name = "synthetic", Content = "{{date}}" };
                db.NotificationRules.Add(new NotificationRule { Id = Guid.NewGuid(), TenantId = tenantId, Template = template,
                    RuleType = NotificationRuleType.RemindNDaysBefore, TimingConfigJson = "{\"daysBefore\":1}" });
                db.NotificationRules.Add(new NotificationRule { Id = Guid.NewGuid(), TenantId = tenantId, Template = template,
                    RuleType = NotificationRuleType.RemindIfNoVisitNMonths, TimingConfigJson = "{\"months\":3}" });
                for (var optedIn = 0; optedIn < 2; optedIn++)
                {
                    var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Phone = "+919876543210" };
                    var appointment = new Appointment { Id = Guid.NewGuid(), TenantId = tenantId, Clinic = clinic, Patient = patient,
                        Date = new TimezoneService().GetClinicToday(clinic.Timezone).AddDays(1), TimeSlot = "10:00", Status = AppointmentStatus.Booked };
                    db.Appointments.Add(appointment);
                    if (optedIn == 1)
                    {
                        db.PatientConsents.Add(new PatientConsent { Id = Guid.NewGuid(), TenantId = tenantId, Patient = patient, Purpose = "whatsapp_opt_in" });
                        expected.Add((tenantId, appointment.Id, false));
                    }
                }
            }
            await db.SaveChangesAsync();
        }
        var ruleLog = new CapturedLogger<NotificationRulesWorker>();
        using var rules = new NotificationRulesWorker(provider.GetRequiredService<IServiceScopeFactory>(), ruleLog);
        await rules.EvaluateAsync(default);
        Assert.Equal(expected.OrderBy(x => x.Tenant), observed.OrderBy(x => x.Tenant));
        Assert.Equal(2, ruleLog.Messages.Count(m => m.Contains("no_visit_policy_pending")));
        observed.Clear();
        using var reminders = new ReminderSchedulerService(provider, NullLogger<ReminderSchedulerService>.Instance);
        await reminders.SendRemindersAsync(default);
        Assert.Equal(expected.OrderBy(x => x.Tenant), observed.OrderBy(x => x.Tenant));
        using var fresh = provider.CreateScope();
        var freshTenant = fresh.ServiceProvider.GetRequiredService<ITenantContext>();
        Assert.Null(freshTenant.CurrentTenantId);
        Assert.False(freshTenant.IsPlatformAdmin);
    }

    private sealed class ScopedNotifier(ITenantContext context, List<(Guid, Guid, bool)> calls) : INotificationService
    {
        public Task SendAppointmentConfirmationAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null) => throw new NotSupportedException();
        public Task SendAppointmentReminderAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null)
        { calls.Add((context.CurrentTenantId!.Value, appointmentId, context.IsPlatformAdmin)); return Task.CompletedTask; }
        public Task SendRuleAsync(Guid patientId, Guid? appointmentId, Guid ruleId, CancellationToken ct = default)
        { calls.Add((context.CurrentTenantId!.Value, appointmentId!.Value, context.IsPlatformAdmin)); return Task.CompletedTask; }
    }
}
