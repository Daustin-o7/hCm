using Hospital_CRM.Domain.Entities;

namespace Hospital_CRM.Api.Services;

public interface INotificationService
{
    NotificationIntent CreateConfirmationIntent(Appointment appointment, string? precheckLink) =>
        throw new NotSupportedException("notification_outbox_not_configured");
    Task HandoffConfirmationAsync(Guid intentId, CancellationToken ct = default) =>
        throw new NotSupportedException("notification_outbox_not_configured");

    Task SendAppointmentConfirmationAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null);
    Task SendAppointmentReminderAsync(Guid appointmentId, string patientPhone, string clinicName, DateTime dateTime, string? precheckLink = null);

    // AppointmentId is deliberately nullable: patient-only rules must never invent an appointment.
    Task SendRuleAsync(Guid patientId, Guid? appointmentId, Guid ruleId, CancellationToken ct = default) =>
        throw new NotSupportedException("notification_rule_delivery_not_configured");
}
