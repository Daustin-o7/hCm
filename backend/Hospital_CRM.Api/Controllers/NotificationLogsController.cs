using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

/// <summary>
/// Read-only delivery history for MOD-13. NotificationLogs is append-only, so there is no write verb.
/// </summary>
[ApiController]
[Route("api/v1/notification-logs")]
public class NotificationLogsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;

    public NotificationLogsController(HospitalCrmDbContext db) => _db = db;

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;

        // NotificationLog carries no TenantId; ownership comes from the appointment it was sent for.
        var query = _db.NotificationLogs.AsNoTracking()
            .Where(n => n.Appointment.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<NotificationStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                return BadRequest(new { error = "invalid_status" });
            query = query.Where(n => n.Status == parsed);
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var total = await query.CountAsync(ct);
        var items = await query
            // Failed rows have no SentAt, so appointment date keeps failures with their day.
            .OrderByDescending(n => n.Appointment.Date)
            .ThenByDescending(n => n.SentAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                id = n.Id,
                appointmentId = n.AppointmentId,
                patientName = n.Appointment.Patient.Name,
                appointmentDate = n.Appointment.Date,
                channel = n.Channel.ToString().ToLower(),
                template = n.Template,
                status = n.Status.ToString().ToLower(),
                sentAt = n.SentAt,
                deliveredAt = n.DeliveredAt,
                failedReason = n.FailedReason,
                ruleId = n.RuleId
            })
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }
}
