using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/queue-status")]
[EnableRateLimiting("queue-status")]
public class QueueStatusController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ITenantContext? _tenantContext;

    public QueueStatusController(HospitalCrmDbContext db, ITenantContext? tenantContext = null)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Section 1: Public queue tracker.
    /// Secured with an opaque, unguessable tracking token.
    /// Rate-limited to prevent brute-forcing.
    /// Returns only queue state and time remaining; terminal visits expose status and time remaining only.
    /// </summary>
    [HttpGet("{token}")]
    [PublicTokenEndpoint(PublicTokenPurpose.Queue)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetStatus(string token, CancellationToken ct)
    {
        if (_tenantContext is null || !PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Queue, token))
            return NotFound(new { error = "token_not_found" });

        // Opaque token is the only credential. A numeric queue number is guessable, so it is
        // not accepted here: it would let anyone enumerate other clinics' queues.
        var binding = await PublicTokenRouting.ResolveAsync(_db, PublicTokenPurpose.Queue, token, ct);
        if (binding is null) return NotFound(new { error = "token_not_found" });
        using var tenantScope = _tenantContext.PushTenant(binding.TenantId);
        var appointment = await _db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == binding.ResourceId && a.TenantId == binding.TenantId &&
                a.QueueTrackingToken == token && a.QueueToken != null, ct);

        var now = DateTimeOffset.UtcNow;
        if (appointment?.QueueTrackingExpiresAt is not { } expiresAt || expiresAt <= now)
            return NotFound(new { error = "token_not_found" });

        var expiresInSeconds = (expiresAt - now).TotalSeconds;
        var terminalStatus = appointment.Status switch
        {
            AppointmentStatus.Completed => "completed",
            AppointmentStatus.Cancelled => "cancelled",
            AppointmentStatus.NoShow => "no_show",
            _ => null
        };
        if (terminalStatus is not null)
            return Ok(new { status = terminalStatus, expiresInSeconds });

        var queue = _db.Appointments.Where(a => a.TenantId == appointment.TenantId &&
            a.ClinicId == appointment.ClinicId && a.DoctorId == appointment.DoctorId && a.Date == appointment.Date);

        // Position: number of patients ahead of this appointment in the doctor's queue today
        var aheadCount = await queue
            .Where(a => a.QueueToken != null &&
                        a.QueueToken < appointment.QueueToken &&
                        (a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.InNursing))
            .CountAsync(ct);

        var position = aheadCount + 1; // 1 means you are next
        var estimatedWaitMinutes = aheadCount * 15; // standard consultation benchmark: 15 min per patient ahead

        // Currently serving = smallest active token (InConsultation, InNursing, or CheckedIn)
        var currentlyServing = await queue
            .Where(a => a.QueueToken != null &&
                        (a.Status == AppointmentStatus.InConsultation ||
                         a.Status == AppointmentStatus.InNursing ||
                         a.Status == AppointmentStatus.CheckedIn))
            .OrderBy(a => a.QueueToken)
            .Select(a => a.QueueToken)
            .FirstOrDefaultAsync(ct);

        return Ok(new
        {
            yourToken = appointment.QueueToken,
            position = position,
            estimatedWaitMinutes = estimatedWaitMinutes,
            currentlyServing = currentlyServing ?? appointment.QueueToken ?? 0,
            expiresInSeconds,
            status = appointment.Status switch
            {
                AppointmentStatus.CheckedIn => "waiting",
                AppointmentStatus.InNursing => "in_nursing",
                AppointmentStatus.InConsultation => "in_consultation",
                AppointmentStatus.Parked => "parked",
                _ => "scheduled"
            }
        });
    }
}
