using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class NurseCareController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<NurseCareController> _logger;

    public NurseCareController(HospitalCrmDbContext db, ILogger<NurseCareController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost("nurse-assignments")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Assign([FromBody] CreateAssignmentRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (caller is null) return Forbid();

        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Id == request.PatientId && p.TenantId == caller.TenantId, ct);
        if (patient is null) return NotFound(new { error = "patient_not_found" });

        var nurse = await NursingScope.Profiles(_db, caller)
            .FirstOrDefaultAsync(p => p.Id == request.NurseProfileId, ct);
        if (nurse is null) return NotFound(new { error = "nurse_not_found" });

        if (request.AppointmentId.HasValue && !await _db.Appointments.AnyAsync(a => a.Id == request.AppointmentId &&
            a.TenantId == caller.TenantId && a.ClinicId == caller.ClinicId && a.PatientId == patient.Id, ct))
            return NotFound(new { error = "appointment_not_found" });
        if (request.ShiftId.HasValue && !await _db.NurseShifts.AnyAsync(s => s.Id == request.ShiftId &&
            s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.NurseProfileId == nurse.Id, ct))
            return NotFound(new { error = "shift_not_found" });

        var alreadyActive = await _db.PatientNurseAssignments.AnyAsync(a =>
            a.TenantId == caller.TenantId && a.PatientId == request.PatientId &&
            a.NurseProfileId == request.NurseProfileId &&
            a.Status == NurseAssignmentStatus.Active, ct);
        if (alreadyActive) return Conflict(new { error = "assignment_already_active" });

        var assignment = new PatientNurseAssignment
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            PatientId = request.PatientId,
            NurseProfileId = request.NurseProfileId,
            AppointmentId = request.AppointmentId,
            ShiftId = request.ShiftId,
            Status = NurseAssignmentStatus.Active,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = callerId.Value
        };

        _db.PatientNurseAssignments.Add(assignment);
        await _db.SaveChangesAsync(ct);
        await ShiftsController.LogAudit(_db, "PatientNurseAssignment", assignment.Id, request.NurseProfileId,
            "created", null, $"patient:{request.PatientId}", callerId.Value, caller.TenantId, ct);

        return Ok(new { assignmentId = assignment.Id, status = assignment.Status.ToString() });
    }

    [HttpGet("nurse-assignments")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> List([FromQuery] Guid? patientId, [FromQuery] bool activeOnly, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (caller is null) return Forbid();

        var profiles = NursingScope.Profiles(_db, caller);
        var query = _db.PatientNurseAssignments.Where(a => a.TenantId == caller.TenantId &&
            profiles.Any(n => n.Id == a.NurseProfileId) && _db.Patients.Any(p => p.Id == a.PatientId && p.TenantId == caller.TenantId) &&
            (a.AppointmentId == null || _db.Appointments.Any(v => v.Id == a.AppointmentId && v.TenantId == caller.TenantId &&
                v.ClinicId == caller.ClinicId && v.PatientId == a.PatientId)) &&
            (a.ShiftId == null || _db.NurseShifts.Any(s => s.Id == a.ShiftId && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId)));

        if (caller.Role == UserRole.Nurse)
        {
            var own = await profiles.Where(p => p.UserId == callerId.Value).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (own is null) return Forbid();
            query = query.Where(a => a.NurseProfileId == own);
        }

        if (patientId.HasValue)
            query = query.Where(a => a.PatientId == patientId.Value);
        if (activeOnly)
            query = query.Where(a => a.Status == NurseAssignmentStatus.Active);

        var rows = await query
            .OrderByDescending(a => a.AssignedAt)
            .Take(200)
            .Select(a => new
            {
                id = a.Id,
                patientId = a.PatientId,
                nurseProfileId = a.NurseProfileId,
                nurseName = a.Nurse.User.Name,
                appointmentId = a.AppointmentId,
                status = a.Status.ToString(),
                assignedAt = a.AssignedAt
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost("nurse-assignments/{id:guid}/complete")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (caller is null) return Forbid();

        var profiles = NursingScope.Profiles(_db, caller);
        var assignment = await _db.PatientNurseAssignments
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == caller.TenantId &&
                profiles.Any(p => p.Id == a.NurseProfileId) && _db.Patients.Any(p => p.Id == a.PatientId && p.TenantId == caller.TenantId) &&
                (a.AppointmentId == null || _db.Appointments.Any(v => v.Id == a.AppointmentId && v.TenantId == caller.TenantId &&
                    v.ClinicId == caller.ClinicId && v.PatientId == a.PatientId)) &&
                (a.ShiftId == null || _db.NurseShifts.Any(s => s.Id == a.ShiftId && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId)), ct);
        if (assignment is null) return NotFound(new { error = "assignment_not_found" });

        if (caller.Role == UserRole.Nurse)
        {
            var own = await profiles.Where(p => p.UserId == callerId.Value).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (assignment.NurseProfileId != own) return Forbid();
        }

        assignment.Status = NurseAssignmentStatus.Completed;
        assignment.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await ShiftsController.LogAudit(_db, "PatientNurseAssignment", assignment.Id, assignment.NurseProfileId,
            "status", "Active", "Completed", callerId.Value, caller.TenantId, ct);

        return Ok(new { assignmentId = assignment.Id, status = assignment.Status.ToString() });
    }

    // Structured handover note - outgoing nurse -> incoming nurse.
    // Dual routes for both /api/v1/handovers and /api/v1/nurse-care/handovers for frontend parity
    [HttpPost("handovers")]
    [HttpPost("nurse-care/handovers")]
    [AuthorizeRoles("Nurse")]
    public async Task<IActionResult> CreateHandover([FromBody] CreateHandoverRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.Nurse);
        if (caller is null) return Forbid();

        var whatHappened = !string.IsNullOrWhiteSpace(request.WhatHappened)
            ? request.WhatHappened.Trim()
            : request.Note?.Trim() ?? string.Empty;

        var whatIsPending = !string.IsNullOrWhiteSpace(request.WhatIsPending)
            ? request.WhatIsPending.Trim()
            : "None reported";

        if (string.IsNullOrWhiteSpace(whatHappened))
            return BadRequest(new { error = "what_happened_or_note_required" });

        var profiles = NursingScope.Profiles(_db, caller);
        var own = await profiles.FirstOrDefaultAsync(p => p.UserId == callerId.Value, ct);
        if (own is null) return Forbid();

        if (request.ShiftId.HasValue && !await _db.NurseShifts.AnyAsync(s => s.Id == request.ShiftId &&
            s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.NurseProfileId == own.Id, ct))
            return NotFound(new { error = "shift_not_found" });

        // If ToNurseProfileId is provided, validate it; otherwise auto-select the other nurse or self
        Guid targetNurseId;
        if (request.ToNurseProfileId.HasValue && request.ToNurseProfileId.Value != Guid.Empty)
        {
            var toNurse = await profiles.FirstOrDefaultAsync(p => p.Id == request.ToNurseProfileId.Value, ct);
            if (toNurse is null) return NotFound(new { error = "nurse_not_found" });
            targetNurseId = toNurse.Id;
        }
        else
        {
            var otherNurse = await profiles.Where(p => p.Id != own.Id)
                .Select(p => p.Id)
                .FirstOrDefaultAsync(ct);
            targetNurseId = otherNurse != Guid.Empty ? otherNurse : own.Id;
        }

        // If PatientId is provided, validate patient belongs to clinic; otherwise resolve active patient
        Guid targetPatientId;
        if (request.PatientId.HasValue && request.PatientId.Value != Guid.Empty)
        {
            var patient = await _db.Patients
                .FirstOrDefaultAsync(p => p.Id == request.PatientId.Value && p.TenantId == caller.TenantId, ct);
            if (patient is null) return NotFound(new { error = "patient_not_found" });
            targetPatientId = patient.Id;
        }
        else
        {
            var activePatientId = await _db.PatientNurseAssignments
                .Where(a => a.TenantId == caller.TenantId && a.NurseProfileId == own.Id && a.Status == NurseAssignmentStatus.Active &&
                    _db.Patients.Any(p => p.Id == a.PatientId && p.TenantId == caller.TenantId))
                .Select(a => (Guid?)a.PatientId)
                .FirstOrDefaultAsync(ct);

            if (!activePatientId.HasValue)
            {
                activePatientId = await _db.Patients
                    .Where(p => p.TenantId == caller.TenantId)
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => (Guid?)p.Id)
                    .FirstOrDefaultAsync(ct);
            }

            if (!activePatientId.HasValue)
                return BadRequest(new { error = "patient_required_for_handover" });

            targetPatientId = activePatientId.Value;
        }

        var handover = new ShiftHandover
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            PatientId = targetPatientId,
            AuthorNurseProfileId = own.Id,
            ToNurseProfileId = targetNurseId,
            ShiftId = request.ShiftId,
            WhatHappened = whatHappened,
            WhatIsPending = whatIsPending,
            CriticalAlerts = request.CriticalAlerts?.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.ShiftHandovers.Add(handover);
        await _db.SaveChangesAsync(ct);
        await ShiftsController.LogAudit(_db, "ShiftHandover", handover.Id, own.Id, "created", null,
            $"patient:{targetPatientId}", callerId.Value, caller.TenantId, ct);

        return Ok(new { handoverId = handover.Id });
    }

    [HttpGet("handovers")]
    [HttpGet("nurse-care/handovers")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Nurse")]
    public async Task<IActionResult> ListHandovers([FromQuery] Guid? patientId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Doctor, UserRole.Nurse);
        if (caller is null) return Forbid();

        var profiles = NursingScope.Profiles(_db, caller);
        var query = _db.ShiftHandovers.Where(h => h.TenantId == caller.TenantId &&
            profiles.Any(n => n.Id == h.AuthorNurseProfileId) && profiles.Any(n => n.Id == h.ToNurseProfileId) &&
            _db.Patients.Any(p => p.Id == h.PatientId && p.TenantId == caller.TenantId) &&
            (h.ShiftId == null || _db.NurseShifts.Any(s => s.Id == h.ShiftId && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId)));

        if (patientId.HasValue && patientId.Value != Guid.Empty)
        {
            var patientExists = await _db.Patients
                .AnyAsync(p => p.Id == patientId.Value && p.TenantId == caller.TenantId, ct);
            if (!patientExists)
                return NotFound(new { error = "patient_not_found" });

            query = query.Where(h => h.PatientId == patientId.Value);
        }

        var rows = await query
            .OrderByDescending(h => h.CreatedAt)
            .Take(50)
            .Select(h => new
            {
                id = h.Id,
                patientId = h.PatientId,
                patientName = _db.Patients.Where(p => p.Id == h.PatientId).Select(p => p.Name).FirstOrDefault() ?? "General Ward",
                authorNurseProfileId = h.AuthorNurseProfileId,
                authorName = _db.NurseProfiles.Where(p => p.Id == h.AuthorNurseProfileId).Select(p => p.User.Name).FirstOrDefault() ?? "Staff Nurse",
                toNurseProfileId = h.ToNurseProfileId,
                toName = _db.NurseProfiles.Where(p => p.Id == h.ToNurseProfileId).Select(p => p.User.Name).FirstOrDefault() ?? "Duty Nurse",
                whatHappened = h.WhatHappened,
                whatIsPending = h.WhatIsPending,
                criticalAlerts = h.CriticalAlerts,
                note = h.Note,
                createdAt = h.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(rows);
    }
}

public record CreateAssignmentRequest(
    Guid PatientId,
    Guid NurseProfileId,
    Guid? AppointmentId,
    Guid? ShiftId);

public record CreateHandoverRequest(
    Guid? PatientId,
    Guid? ToNurseProfileId,
    string? Note,
    string? WhatHappened,
    string? WhatIsPending,
    string? CriticalAlerts,
    Guid? ShiftId);
