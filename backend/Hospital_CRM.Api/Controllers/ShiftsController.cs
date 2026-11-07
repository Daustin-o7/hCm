using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/shifts")]
[RequireModule(ModuleRegistry.StaffRoster)]
public class ShiftsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<ShiftsController> _logger;

    public ShiftsController(HospitalCrmDbContext db, ILogger<ShiftsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Admin posts a shift; with nurseProfileId it lands straight on the roster, without it it's open for applications.
    [HttpPost]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateShiftRequest request, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (caller is null) return Forbid();

        if (request.Date < DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(new { error = "shift_date_in_past" });

        Guid? nurseProfileId = null;
        if (request.NurseProfileId.HasValue)
        {
            var conflict = await CheckAssignmentConflicts(caller, request.NurseProfileId.Value, request.Date, request.ShiftType, excludeShiftId: null, ct);
            if (conflict != null) return Conflict(new { error = conflict });
            nurseProfileId = request.NurseProfileId;
        }

        var shift = new NurseShift
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            ClinicId = caller.ClinicId!.Value,
            Date = request.Date,
            ShiftType = request.ShiftType,
            Status = nurseProfileId.HasValue ? ShiftStatus.Assigned : ShiftStatus.Open,
            NurseProfileId = nurseProfileId,
            Notes = request.Notes ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = caller.UserId
        };

        _db.NurseShifts.Add(shift);
        await _db.SaveChangesAsync(ct);
        await LogAudit(_db, "NurseShift", shift.Id, nurseProfileId, "created", null,
            $"{shift.Date:yyyy-MM-dd} {shift.ShiftType}" + (nurseProfileId.HasValue ? " assigned" : " open"), caller.UserId, caller.TenantId, ct);

        return Ok(new { shiftId = shift.Id, status = shift.Status.ToString() });
    }

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (caller is null) return Forbid();

        var until = to ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var since = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));

        var profiles = NursingScope.Profiles(_db, caller);
        var query = _db.NurseShifts
            .Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.Date >= since && s.Date <= until &&
                (s.NurseProfileId == null || profiles.Any(n => n.Id == s.NurseProfileId)));

        Guid? meProfileId = null;
        if (caller.Role == UserRole.Nurse)
        {
            meProfileId = await OwnProfileId(caller, ct);
            if (meProfileId is null) return Forbid();
            query = query.Where(s =>
                s.Status == ShiftStatus.Open ||
                (s.NurseProfileId != null && s.NurseProfileId == meProfileId));
        }

        var mine = meProfileId;
        var shifts = await query
            .OrderBy(s => s.Date)
            .ThenBy(s => s.ShiftType)
            .Select(s => new
            {
                id = s.Id,
                date = s.Date,
                shiftType = s.ShiftType.ToString(),
                status = s.Status.ToString(),
                notes = s.Notes,
                nurseProfileId = s.NurseProfileId,
                nurseName = s.Nurse != null ? s.Nurse.User.Name : null,
                nurseEmail = s.Nurse != null ? s.Nurse.User.Email : null,
                appliedCount = s.Applications.Count(a => a.TenantId == caller.TenantId && a.Status == ShiftApplicationStatus.Applied &&
                    profiles.Any(n => n.Id == a.NurseProfileId)),
                appliedByMe = mine != null && s.Applications.Any(a => a.TenantId == caller.TenantId && a.NurseProfileId == mine && a.Status == ShiftApplicationStatus.Applied)
            })
            .ToListAsync(ct);

        return Ok(shifts);
    }

    // Assign / reassign / reopen / cancel.
    [HttpPut("{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateShiftRequest request, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (caller is null) return Forbid();

        var shift = await _db.NurseShifts.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId, ct);
        if (shift is null) return NotFound(new { error = "shift_not_found" });
        if (shift.IsLocked) return Conflict(new { error = "shift_is_locked_in_finalized_roster" });

        var old = $"{shift.Status}/{shift.NurseProfileId}";

        if (request.Cancel == true)
        {
            shift.Status = ShiftStatus.Cancelled;
            shift.NurseProfileId = null;
        }
        else if (request.NurseProfileId.HasValue)
        {
            var conflict = await CheckAssignmentConflicts(caller, request.NurseProfileId.Value, shift.Date, shift.ShiftType, excludeShiftId: shift.Id, ct);
            if (conflict != null) return Conflict(new { error = conflict });
            shift.NurseProfileId = request.NurseProfileId;
            shift.Status = ShiftStatus.Assigned;
        }
        else
        {
            // Explicit null nurse = reopen as open shift
            shift.NurseProfileId = null;
            shift.Status = ShiftStatus.Open;
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
            shift.Notes = request.Notes;

        shift.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAudit(_db, "NurseShift", shift.Id, shift.NurseProfileId, "updated", old,
            $"{shift.Status}/{shift.NurseProfileId}", caller.UserId, caller.TenantId, ct);

        return Ok(new { shiftId = shift.Id, status = shift.Status.ToString(), nurseProfileId = shift.NurseProfileId });
    }

    // Nurse applies for an open shift.
    [HttpPost("{id:guid}/applications")]
    [AuthorizeRoles("Nurse")]
    public async Task<IActionResult> Apply(Guid id, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.Nurse);
        if (caller is null) return Forbid();

        var profileId = await OwnProfileId(caller, ct);
        if (profileId is null) return Forbid();

        var shift = await _db.NurseShifts.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId, ct);
        if (shift is null) return NotFound(new { error = "shift_not_found" });
        if (shift.Status != ShiftStatus.Open)
            return Conflict(new { error = "shift_not_open" });
        if (shift.IsLocked) return Conflict(new { error = "shift_is_locked_in_finalized_roster" });

        var conflict = await CheckAssignmentConflicts(caller, profileId.Value, shift.Date, shift.ShiftType, excludeShiftId: null, ct);
        if (conflict != null) return Conflict(new { error = conflict });

        var existing = await _db.NurseShiftApplications
            .FirstOrDefaultAsync(a => a.TenantId == caller.TenantId && a.ShiftId == id && a.NurseProfileId == profileId, ct);
        if (existing != null)
        {
            if (existing.Status == ShiftApplicationStatus.Withdrawn)
            {
                // Re-apply after withdraw: reactivate the same row (amend, not duplicate).
                existing.Status = ShiftApplicationStatus.Applied;
                existing.AppliedAt = DateTimeOffset.UtcNow;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await LogAudit(_db, "NurseShiftApplication", existing.Id, profileId, "reapplied", "Withdrawn", "Applied", caller.UserId, caller.TenantId, ct);
                return Ok(new { applicationId = existing.Id });
            }
            return Conflict(new { error = "already_applied" });
        }

        var application = new NurseShiftApplication
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            ShiftId = id,
            NurseProfileId = profileId.Value,
            Status = ShiftApplicationStatus.Applied,
            AppliedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.NurseShiftApplications.Add(application);
        await _db.SaveChangesAsync(ct);
        await LogAudit(_db, "NurseShiftApplication", application.Id, profileId, "created", null, $"applied:{id}", caller.UserId, caller.TenantId, ct);

        return Ok(new { applicationId = application.Id });
    }

    // Nurse withdraws their own pending application (re-appliable while the shift stays open).
    // A nurse has at most one application per shift, so it's resolved server-side.
    [HttpPost("{id:guid}/applications/withdraw")]
    [AuthorizeRoles("Nurse")]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.Nurse);
        if (caller is null) return Forbid();

        var profileId = await OwnProfileId(caller, ct);
        if (profileId is null) return Forbid();

        var application = await _db.NurseShiftApplications
            .FirstOrDefaultAsync(a => a.TenantId == caller.TenantId && a.ShiftId == id && a.NurseProfileId == profileId &&
                a.Shift.TenantId == caller.TenantId && a.Shift.ClinicId == caller.ClinicId, ct);
        if (application is null) return NotFound(new { error = "application_not_found" });
        if (application.Status != ShiftApplicationStatus.Applied)
            return Conflict(new { error = "application_not_withdrawable" });

        application.Status = ShiftApplicationStatus.Withdrawn;
        application.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAudit(_db, "NurseShiftApplication", application.Id, profileId, "withdrawn", "Applied", "Withdrawn", caller.UserId, caller.TenantId, ct);

        return Ok(new { applicationId = application.Id, status = "Withdrawn" });
    }

    [HttpGet("{id:guid}/applications")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListApplications(Guid id, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (caller is null) return Forbid();

        var profiles = NursingScope.Profiles(_db, caller);
        var apps = await _db.NurseShiftApplications
            .Where(a => a.TenantId == caller.TenantId && a.ShiftId == id && a.Shift.TenantId == caller.TenantId &&
                a.Shift.ClinicId == caller.ClinicId && profiles.Any(n => n.Id == a.NurseProfileId))
            .OrderByDescending(a => a.AppliedAt)
            .Select(a => new
            {
                id = a.Id,
                status = a.Status.ToString(),
                appliedAt = a.AppliedAt,
                nurseProfileId = a.NurseProfileId,
                nurseName = a.Nurse.User.Name,
                nurseEmail = a.Nurse.User.Email,
                designation = a.Nurse.Designation,
                specialization = a.Nurse.Specialization
            })
            .ToListAsync(ct);

        return Ok(apps);
    }

    // Admin picks an applicant: shift becomes assigned, others are rejected.
    [HttpPost("{id:guid}/applications/{applicationId:guid}/select")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Select(Guid id, Guid applicationId, CancellationToken ct)
    {
        var caller = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (caller is null) return Forbid();

        var shift = await _db.NurseShifts.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId, ct);
        if (shift is null) return NotFound(new { error = "shift_not_found" });
        if (shift.Status != ShiftStatus.Open)
            return Conflict(new { error = "shift_not_open" });
        if (shift.IsLocked) return Conflict(new { error = "shift_is_locked_in_finalized_roster" });

        var application = await _db.NurseShiftApplications
            .FirstOrDefaultAsync(a => a.TenantId == caller.TenantId && a.Id == applicationId && a.ShiftId == id && a.Status == ShiftApplicationStatus.Applied, ct);
        if (application is null) return NotFound(new { error = "application_not_found" });

        var conflict = await CheckAssignmentConflicts(caller, application.NurseProfileId, shift.Date, shift.ShiftType, excludeShiftId: shift.Id, ct);
        if (conflict != null) return Conflict(new { error = conflict });

        shift.NurseProfileId = application.NurseProfileId;
        shift.Status = ShiftStatus.Assigned;
        shift.UpdatedAt = DateTimeOffset.UtcNow;

        application.Status = ShiftApplicationStatus.Selected;
        application.UpdatedAt = DateTimeOffset.UtcNow;

        var others = await _db.NurseShiftApplications
            .Where(a => a.TenantId == caller.TenantId && a.ShiftId == id && a.Id != applicationId && a.Status == ShiftApplicationStatus.Applied)
            .ToListAsync(ct);
        foreach (var other in others)
        {
            other.Status = ShiftApplicationStatus.Rejected;
            other.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await LogAudit(_db, "NurseShift", shift.Id, shift.NurseProfileId, "assigned", null,
            $"application:{applicationId}", caller.UserId, caller.TenantId, ct);

        return Ok(new { shiftId = shift.Id, nurseProfileId = shift.NurseProfileId, rejected = others.Count });
    }

    private async Task<Guid?> OwnProfileId(TenantMembership caller, CancellationToken ct) =>
        await NursingScope.Profiles(_db, caller).Where(p => p.UserId == caller.UserId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);

    // Returns null when clean, otherwise the conflict error code to return as 409.
    private async Task<string?> CheckAssignmentConflicts(TenantMembership caller, Guid nurseProfileId, DateOnly date, ShiftType shiftType, Guid? excludeShiftId, CancellationToken ct)
    {
        if (!await NursingScope.Profiles(_db, caller).AnyAsync(p => p.Id == nurseProfileId, ct)) return "nurse_not_found";
        var duplicate = await _db.NurseShifts.AnyAsync(s =>
            s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.NurseProfileId == nurseProfileId &&
            s.Date == date &&
            s.ShiftType == shiftType &&
            s.Status == ShiftStatus.Assigned &&
            s.Id != excludeShiftId, ct);
        if (duplicate) return "nurse_already_assigned";

        var onLeave = await _db.LeaveRequests.AnyAsync(l =>
            l.TenantId == caller.TenantId && l.NurseProfileId == nurseProfileId &&
            l.Status == LeaveStatus.Approved &&
            l.StartDate <= date &&
            l.EndDate >= date, ct);
        if (onLeave) return "nurse_on_leave";

        return null;
    }

    internal static async Task LogAudit(HospitalCrmDbContext db, string entityType, Guid entityId, Guid? nurseProfileId,
        string action, string? oldValue, string newValue, Guid changedBy, Guid tenantId, CancellationToken ct)
    {
        db.NurseAuditLogs.Add(new NurseAuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntityType = entityType,
            EntityId = entityId,
            NurseProfileId = nurseProfileId,
            Action = action,
            OldValue = oldValue ?? string.Empty,
            NewValue = newValue ?? string.Empty,
            ChangedBy = changedBy,
            ChangedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}

public record CreateShiftRequest(
    DateOnly Date,
    ShiftType ShiftType,
    Guid? NurseProfileId,
    string? Notes);

public record UpdateShiftRequest(
    Guid? NurseProfileId,
    bool? Cancel,
    string? Notes);
