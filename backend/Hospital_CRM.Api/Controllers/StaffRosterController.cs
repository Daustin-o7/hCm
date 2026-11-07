using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

public record CreateShiftTemplateRequest(
    string Name,
    string StartTime,
    string EndTime,
    bool CrossesMidnight,
    decimal DurationHours,
    int RequiredCoverage,
    string? Description);

public record CreateRosterRequest(
    string Title,
    DateOnly StartDate,
    DateOnly EndDate);

public record RequestShiftChangeRequest(
    ShiftChangeType RequestType,
    Guid RequesterShiftId,
    Guid? TargetNurseProfileId,
    Guid? TargetShiftId,
    string? Reason);

public record ApproveShiftChangeRequest(
    string? Note);

public record RejectShiftChangeRequest(
    string RejectionReason);

public record CreateAuthorityProxyRequest(
    Guid DelegatorUserId,
    Guid DesigneeUserId,
    string Permissions,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    string? Reason);

public record CreateAnnouncementRequest(
    Guid? TeamId,
    string Title,
    string Content,
    string Priority);

public record LeaveImpactRequest(
    Guid NurseProfileId,
    DateOnly StartDate,
    DateOnly EndDate);

/// <summary>
/// Section 2: Staff Roster optional workforce module (gated by "staff_roster").
/// Covers: shift templates, duty roster lifecycle (draft -> published -> locked),
/// shift swap/cover workflows, leave coverage gap alerts, authority proxies, and structured announcements.
/// NO unstructured chat: structured communication prevents PHI leakage.
/// Only writes are module-gated: if the tenant deactivates the module the existing
/// roster stays readable, but nothing can be changed.
/// </summary>
[ApiController]
[Route("api/v1/staff/roster")]
public class StaffRosterController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<StaffRosterController> _logger;

    public StaffRosterController(HospitalCrmDbContext db, ILogger<StaffRosterController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // 11 hours is the usual minimum rest between shifts; warn only, never block.
    private const double MinimumRestHours = 11;

    private static readonly HashSet<string> DelegatablePermissions = new(StringComparer.Ordinal)
    {
        "roster.approve", "leave.approve"
    };

    #region Shift Templates

    [HttpGet("templates")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ListTemplates(CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var templates = await _db.ShiftTemplates
            .AsNoTracking()
            .Where(t => t.ClinicId == caller.ClinicId && t.TenantId == caller.TenantId && t.IsActive)
            .OrderBy(t => t.StartTime)
            .ToListAsync(ct);

        return Ok(templates);
    }

    [HttpPost("templates")]
    [AuthorizeRoles("ClinicAdmin")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateShiftTemplateRequest request, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.ClinicAdmin);
        if (caller?.ClinicId is null) return Forbid();

        var template = new ShiftTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            ClinicId = caller.ClinicId.Value,
            Name = request.Name.Trim(),
            StartTime = request.StartTime.Trim(),
            EndTime = request.EndTime.Trim(),
            CrossesMidnight = request.CrossesMidnight,
            DurationHours = request.DurationHours > 0 ? request.DurationHours : 8.0m,
            RequiredCoverage = request.RequiredCoverage > 0 ? request.RequiredCoverage : 2,
            Description = request.Description,
            IsActive = true
        };

        _db.ShiftTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        return Ok(template);
    }

    #endregion

    #region Roster Lifecycle (Draft -> Published -> Locked)

    [HttpPost]
    [AuthorizeRoles("ClinicAdmin")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> CreateRoster([FromBody] CreateRosterRequest request, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.ClinicAdmin);
        if (caller?.ClinicId is null) return Forbid();

        if (request.StartDate > request.EndDate)
            return BadRequest(new { error = "start_date_must_be_before_end_date" });

        var roster = new StaffRoster
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            ClinicId = caller.ClinicId.Value,
            Title = request.Title.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Status = RosterStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = caller.UserId
        };

        _db.StaffRosters.Add(roster);
        await _db.SaveChangesAsync(ct);

        return Ok(roster);
    }

    [HttpPost("{id:guid}/publish")]
    [AuthorizeRoles("ClinicAdmin")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> PublishRoster(Guid id, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.ClinicAdmin);
        if (caller?.ClinicId is null) return Forbid();

        var roster = await _db.StaffRosters
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == caller.TenantId && r.ClinicId == caller.ClinicId, ct);

        if (roster is null) return NotFound(new { error = "roster_not_found" });
        if (roster.Status != RosterStatus.Draft)
            return Conflict(new { error = "roster_not_in_draft_state" });

        roster.Status = RosterStatus.Published;
        roster.PublishedAt = DateTimeOffset.UtcNow;
        roster.PublishedBy = caller.UserId;

        // Shifts posted through /shifts carry no RosterId; adopt this window's shifts so
        // publishing is what makes them part of the roster (and lockable later).
        var unrostered = await _db.NurseShifts
            .Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.RosterId == null &&
                s.Date >= roster.StartDate && s.Date <= roster.EndDate)
            .ToListAsync(ct);

        foreach (var shift in unrostered)
            shift.RosterId = roster.Id;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Roster {RosterId} published by {CallerId}", id, caller.UserId);
        return Ok(new { id = roster.Id, status = roster.Status.ToString(), publishedAt = roster.PublishedAt });
    }

    [HttpPost("{id:guid}/lock")]
    [AuthorizeRoles("ClinicAdmin")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> LockRoster(Guid id, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.ClinicAdmin);
        if (caller?.ClinicId is null) return Forbid();

        var roster = await _db.StaffRosters
            .Include(r => r.Shifts.Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId))
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == caller.TenantId && r.ClinicId == caller.ClinicId, ct);

        if (roster is null) return NotFound(new { error = "roster_not_found" });
        if (roster.Status != RosterStatus.Published)
            return Conflict(new { error = "only_published_rosters_can_be_locked" });

        roster.Status = RosterStatus.Locked;
        roster.LockedAt = DateTimeOffset.UtcNow;
        roster.LockedBy = caller.UserId;

        foreach (var shift in roster.Shifts.Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId))
        {
            shift.IsLocked = true;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { id = roster.Id, status = roster.Status.ToString(), lockedAt = roster.LockedAt });
    }

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ListRosters(CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var rosters = await _db.StaffRosters
            .AsNoTracking()
            .Where(r => r.TenantId == caller.TenantId && r.ClinicId == caller.ClinicId)
            .OrderByDescending(r => r.StartDate)
            .Select(r => new
            {
                id = r.Id,
                title = r.Title,
                startDate = r.StartDate,
                endDate = r.EndDate,
                status = r.Status.ToString(),
                shiftCount = r.Shifts.Count(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId),
                publishedAt = r.PublishedAt,
                lockedAt = r.LockedAt
            })
            .ToListAsync(ct);

        return Ok(rosters);
    }

    #endregion

    #region Shift Change Requests (Direct Swap + Open Cover)

    [HttpPost("swaps/request")]
    [AuthorizeRoles("Nurse")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> RequestShiftChange([FromBody] RequestShiftChangeRequest request, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.Nurse);
        if (caller?.ClinicId is null) return Forbid();

        var profiles = NursingScope.Profiles(_db, caller);
        var myProfile = await profiles.FirstOrDefaultAsync(p => p.UserId == caller.UserId, ct);
        if (myProfile is null) return Forbid();

        var myShift = await _db.NurseShifts
            .FirstOrDefaultAsync(s => s.Id == request.RequesterShiftId && s.TenantId == caller.TenantId &&
                s.ClinicId == caller.ClinicId && s.NurseProfileId == myProfile.Id, ct);
        if (myShift is null)
            return NotFound(new { error = "requester_shift_not_found" });

        if (myShift.IsLocked)
            return Conflict(new { error = "shift_is_locked_in_finalized_roster" });

        if (request.TargetNurseProfileId.HasValue && !await profiles.AnyAsync(p => p.Id == request.TargetNurseProfileId, ct))
            return NotFound(new { error = "nurse_not_found" });
        if (request.TargetShiftId.HasValue && !await _db.NurseShifts.AnyAsync(s => s.Id == request.TargetShiftId &&
            s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.NurseProfileId == request.TargetNurseProfileId, ct))
            return NotFound(new { error = "target_shift_not_found" });

        // If DirectSwap, validate target shift and target nurse
        if (request.RequestType == ShiftChangeType.DirectSwap)
        {
            if (!request.TargetNurseProfileId.HasValue || !request.TargetShiftId.HasValue)
                return BadRequest(new { error = "target_nurse_and_shift_required_for_direct_swap" });

            if (request.TargetNurseProfileId.Value == myProfile.Id)
                return BadRequest(new { error = "cannot_swap_with_yourself" });

            var targetShift = await _db.NurseShifts
                .FirstOrDefaultAsync(s => s.Id == request.TargetShiftId.Value && s.TenantId == caller.TenantId &&
                    s.ClinicId == caller.ClinicId && s.NurseProfileId == request.TargetNurseProfileId.Value, ct);
            if (targetShift is null)
                return NotFound(new { error = "target_shift_not_found" });
            if (targetShift.IsLocked)
                return Conflict(new { error = "shift_is_locked_in_finalized_roster" });
        }

        var changeRequest = new ShiftChangeRequest
        {
            Id = Guid.NewGuid(),
            TenantId = myShift.TenantId,
            ClinicId = caller.ClinicId.Value,
            RequestType = request.RequestType,
            Status = ShiftChangeStatus.Requested,
            RequesterNurseProfileId = myProfile.Id,
            RequesterShiftId = request.RequesterShiftId,
            TargetNurseProfileId = request.TargetNurseProfileId,
            TargetShiftId = request.TargetShiftId,
            Reason = request.Reason,
            RequestedAt = DateTimeOffset.UtcNow
        };

        _db.ShiftChangeRequests.Add(changeRequest);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = changeRequest.Id,
            status = changeRequest.Status.ToString(),
            requestType = changeRequest.RequestType.ToString()
        });
    }

    [HttpPost("swaps/{id:guid}/accept")]
    [AuthorizeRoles("Nurse")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> AcceptShiftChange(Guid id, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.Nurse);
        if (caller?.ClinicId is null) return Forbid();

        var myProfile = await NursingScope.Profiles(_db, caller)
            .FirstOrDefaultAsync(p => p.UserId == caller.UserId, ct);
        if (myProfile is null) return Forbid();

        var swap = await _db.ShiftChangeRequests
            .Include(s => s.RequesterShift)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId, ct);

        if (swap is null) return NotFound(new { error = "swap_request_not_found" });
        if (swap.Status != ShiftChangeStatus.Requested)
            return Conflict(new { error = "swap_not_in_requested_status" });

        if (!await ValidSwapPartiesAsync(swap, caller, ct)) return Forbid();

        // For open cover, target nurse claims the shift
        if (swap.RequestType == ShiftChangeType.OpenCover)
        {
            if (swap.RequesterNurseProfileId == myProfile.Id)
                return BadRequest(new { error = "cannot_claim_own_open_shift" });

            swap.TargetNurseProfileId = myProfile.Id;
        }
        else
        {
            // For direct swap, verify caller is the intended target nurse
            if (swap.TargetNurseProfileId != myProfile.Id)
                return Forbid();
        }

        // Re-validate against double-assignment!
        var hasConflict = await _db.NurseShifts
            .AnyAsync(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && s.NurseProfileId == myProfile.Id &&
                s.Date == swap.RequesterShift.Date &&
                s.ShiftType == swap.RequesterShift.ShiftType &&
                s.Status == ShiftStatus.Assigned, ct);

        if (hasConflict)
            return Conflict(new { error = "double_assignment_conflict_detected" });

        swap.Status = ShiftChangeStatus.Accepted;
        swap.AcceptedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(new { id = swap.Id, status = swap.Status.ToString() });
    }

    [HttpGet("swaps")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ListShiftChanges([FromQuery] string? status, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var profiles = NursingScope.Profiles(_db, caller);
        var query = _db.ShiftChangeRequests
            .AsNoTracking()
            .Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId &&
                profiles.Any(n => n.Id == s.RequesterNurseProfileId) &&
                (s.TargetNurseProfileId == null || profiles.Any(n => n.Id == s.TargetNurseProfileId)) &&
                s.RequesterShift.TenantId == caller.TenantId && s.RequesterShift.ClinicId == caller.ClinicId &&
                (s.TargetShift == null || (s.TargetShift.TenantId == caller.TenantId && s.TargetShift.ClinicId == caller.ClinicId)));

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ShiftChangeStatus>(status, true, out var parsed))
            query = query.Where(s => s.Status == parsed);

        var requests = await query
            .OrderByDescending(s => s.RequestedAt)
            .Take(100)
            .Select(s => new
            {
                id = s.Id,
                requestType = s.RequestType.ToString(),
                status = s.Status.ToString(),
                reason = s.Reason,
                requestedAt = s.RequestedAt,
                requesterNurseName = s.RequesterNurse.User.Name,
                requesterShiftId = s.RequesterShiftId,
                requesterShiftDate = s.RequesterShift.Date,
                requesterShiftType = s.RequesterShift.ShiftType.ToString(),
                targetNurseName = s.TargetNurse != null ? s.TargetNurse.User.Name : null,
                targetShiftDate = s.TargetShift != null ? s.TargetShift.Date : (DateOnly?)null,
                targetShiftType = s.TargetShift != null ? s.TargetShift.ShiftType.ToString() : null
            })
            .ToListAsync(ct);

        return Ok(requests);
    }

    [HttpPost("open-shifts/{id:guid}/claim")]
    [AuthorizeRoles("Nurse")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> ClaimOpenShift(Guid id, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.Nurse);
        if (caller?.ClinicId is null) return Forbid();

        var shift = await _db.NurseShifts
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId, ct);

        if (shift is null) return NotFound(new { error = "open_shift_not_found" });
        if (shift.Status != ShiftStatus.Open || shift.NurseProfileId is not null)
            return Conflict(new { error = "shift_not_open" });
        if (shift.IsLocked) return Conflict(new { error = "shift_is_locked_in_finalized_roster" });
        if (shift.Date < DateOnly.FromDateTime(DateTime.UtcNow))
            return Conflict(new { error = "shift_already_in_the_past" });

        var myProfile = await NursingScope.Profiles(_db, caller)
            .FirstOrDefaultAsync(p => p.UserId == caller.UserId, ct);
        if (myProfile is null) return Forbid();

        if (await HasDoubleAssignmentAsync(caller.TenantId, caller.ClinicId.Value, myProfile.Id, shift.Date, shift.ShiftType, shift.Id, ct))
            return Conflict(new { error = "double_assignment_conflict_detected" });

        // A claim still needs head-nurse/admin approval, so it enters the workflow as Accepted.
        var claim = new ShiftChangeRequest
        {
            Id = Guid.NewGuid(),
            TenantId = shift.TenantId,
            ClinicId = caller.ClinicId.Value,
            RequestType = ShiftChangeType.OpenCover,
            Status = ShiftChangeStatus.Accepted,
            RequesterNurseProfileId = myProfile.Id,
            RequesterShiftId = shift.Id,
            TargetNurseProfileId = myProfile.Id,
            RequestedAt = DateTimeOffset.UtcNow,
            AcceptedAt = DateTimeOffset.UtcNow,
            Reason = "Open-shift claim"
        };

        _db.ShiftChangeRequests.Add(claim);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = claim.Id, status = claim.Status.ToString() });
    }

    [HttpPost("swaps/{id:guid}/approve")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> ApproveShiftChange(
        Guid id,
        [FromBody] ApproveShiftChangeRequest? request,
        CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var swap = await _db.ShiftChangeRequests
            .Include(s => s.RequesterShift)
            .Include(s => s.TargetShift)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId, ct);

        if (swap is null) return NotFound(new { error = "swap_request_not_found" });
        if (swap.Status != ShiftChangeStatus.Accepted)
            return Conflict(new { error = "swap_must_be_accepted_by_nurse_before_approval" });

        // Check authority proxy or admin rights
        Guid? approvedOnBehalfOf = null;

        var myProfileId = await _db.NurseProfiles.Where(p => p.TenantId == caller.TenantId && p.UserId == caller.UserId)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (IsSwapParty(swap, myProfileId))
            return BadRequest(new { error = "cannot_approve_own_swap_request" });

        if (caller.Role == UserRole.Nurse)
        {
            // Must have active authority proxy delegation with roster.approve permission
            var activeProxy = await NursingScope.ActiveProxyAsync(_db, caller, "roster.approve", ct);

            if (activeProxy is null)
                return Forbid();

            // Neither the acting approver nor the person they act for may decide their own swap.
            var delegatorProfile = await _db.NurseProfiles
                .FirstOrDefaultAsync(p => p.TenantId == caller.TenantId && p.UserId == activeProxy.DelegatorUserId, ct);

            if (IsSwapParty(swap, delegatorProfile?.Id))
                return BadRequest(new { error = "cannot_approve_on_behalf_of_swap_participant" });

            approvedOnBehalfOf = activeProxy.DelegatorUserId;
        }

        if (!await ValidSwapPartiesAsync(swap, caller, ct)) return Forbid();

        // Re-validate at approval: a shift can be taken by either nurse between accept and
        // approve, so the accept-time check alone would silently create a double assignment.
        if (await ApproveWouldDoubleAssignAsync(swap, ct))
            return Conflict(new { error = "double_assignment_conflict_detected" });

        var restGapWarnings = await RestGapWarningsAsync(swap, ct);

        // Atomic DB transaction to apply shift change
        await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (swap.RequestType == ShiftChangeType.DirectSwap)
            {
                if (swap.TargetShift is null || !swap.TargetNurseProfileId.HasValue)
                    return BadRequest(new { error = "incomplete_swap_details" });

                // Swap nurse assignments
                var tempNurse = swap.RequesterShift.NurseProfileId;
                swap.RequesterShift.NurseProfileId = swap.TargetNurseProfileId;
                swap.RequesterShift.UpdatedAt = DateTimeOffset.UtcNow;

                swap.TargetShift.NurseProfileId = tempNurse;
                swap.TargetShift.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else // OpenCover
            {
                if (!swap.TargetNurseProfileId.HasValue)
                    return BadRequest(new { error = "no_nurse_claimed_open_cover" });

                swap.RequesterShift.NurseProfileId = swap.TargetNurseProfileId;
                swap.RequesterShift.Status = ShiftStatus.Assigned;
                swap.RequesterShift.UpdatedAt = DateTimeOffset.UtcNow;
            }

            swap.Status = ShiftChangeStatus.Applied;
            swap.ApprovedAt = DateTimeOffset.UtcNow;
            swap.AppliedAt = DateTimeOffset.UtcNow;
            swap.ApprovedByUserId = caller.UserId;
            swap.ApprovedOnBehalfOfUserId = approvedOnBehalfOf;

            await _db.SaveChangesAsync(ct);
            if (tx != null)
                await tx.CommitAsync(ct);

            _logger.LogInformation("ShiftChangeRequest {SwapId} approved & applied by {CallerId} (OnBehalfOf: {OnBehalfOf})",
                swap.Id, caller.UserId, approvedOnBehalfOf);

            return Ok(new
            {
                id = swap.Id,
                status = swap.Status.ToString(),
                appliedAt = swap.AppliedAt,
                approvedBy = caller.User.Name,
                approvedOnBehalfOf = approvedOnBehalfOf,
                restGapWarnings
            });
        }
        catch
        {
            if (tx != null)
                await tx.RollbackAsync(ct);
            throw;
        }
    }

    [HttpGet("open-shifts")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ListOpenShifts(CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var openShifts = await _db.NurseShifts
            .AsNoTracking()
            .Include(s => s.ShiftTemplate)
            .Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId &&
                s.Date >= DateOnly.FromDateTime(DateTime.UtcNow) &&
                s.Status == ShiftStatus.Open && s.NurseProfileId == null && !s.IsLocked)
            .OrderBy(s => s.Date)
            .Select(s => new
            {
                id = s.Id,
                date = s.Date,
                shiftType = s.ShiftType.ToString(),
                stationOrWard = s.StationOrWard,
                templateName = s.ShiftTemplate != null && s.ShiftTemplate.TenantId == caller.TenantId &&
                    s.ShiftTemplate.ClinicId == caller.ClinicId ? s.ShiftTemplate.Name : null,
                notes = s.Notes,
                durationHours = s.DurationHours
            })
            .ToListAsync(ct);

        return Ok(openShifts);
    }

    #endregion

    #region Leave Impact Check

    [HttpPost("leave-impact")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> CheckLeaveImpact([FromBody] LeaveImpactRequest request, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        if (request.StartDate > request.EndDate)
            return BadRequest(new { error = "start_date_must_be_before_end_date" });

        if (!await NursingScope.Profiles(_db, caller).AnyAsync(p => p.Id == request.NurseProfileId &&
            (caller.Role != UserRole.Nurse || p.UserId == caller.UserId), ct))
            return NotFound(new { error = "nurse_not_found" });

        var assignedShifts = await _db.NurseShifts
            .AsNoTracking()
            .Where(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId &&
                s.NurseProfileId == request.NurseProfileId &&
                s.Date >= request.StartDate &&
                s.Date <= request.EndDate &&
                s.Status == ShiftStatus.Assigned)
            .OrderBy(s => s.Date)
            .ToListAsync(ct);

        // Coverage minimum is per template, not a fixed 2: a 4-nurse ward and a
        // single-nurse OPD triage station do not have the same gap.
        var requiredCoverage = await _db.ShiftTemplates
            .AsNoTracking()
            .Where(t => t.TenantId == caller.TenantId && t.ClinicId == caller.ClinicId)
            .ToDictionaryAsync(t => t.Id, t => t.RequiredCoverage, ct);

        var impacts = new List<object>();

        foreach (var shift in assignedShifts)
        {
            var totalAssignedOnShift = await _db.NurseShifts
                .CountAsync(s => s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId &&
                    s.Date == shift.Date &&
                    s.ShiftType == shift.ShiftType &&
                    s.Status == ShiftStatus.Assigned, ct);

            var required = shift.ShiftTemplateId is not null &&
                requiredCoverage.TryGetValue(shift.ShiftTemplateId.Value, out var templateMinimum)
                    ? templateMinimum
                    : 2;

            impacts.Add(new
            {
                date = shift.Date,
                shiftType = shift.ShiftType.ToString(),
                stationOrWard = shift.StationOrWard,
                requiredCoverage = required,
                coverageRemaining = Math.Max(0, totalAssignedOnShift - 1),
                hasCoverageGap = (totalAssignedOnShift - 1) < required
            });
        }

        return Ok(new
        {
            conflictsCount = assignedShifts.Count,
            affectedShiftsCount = assignedShifts.Count,
            details = impacts
        });
    }

    #endregion

    #region Authority Proxies (Time-Boxed Delegation)

    [HttpPost("proxies")]
    [AuthorizeRoles("ClinicAdmin")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> CreateAuthorityProxy(
        [FromBody] CreateAuthorityProxyRequest request,
        CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.ClinicAdmin);
        if (caller?.ClinicId is null) return Forbid();

        // Delegation is an admin act: a nurse naming themselves delegator was a
        // self-service way to mint approval rights.
        if (caller.Role != UserRole.ClinicAdmin) return Forbid();

        if (request.ValidUntil <= request.ValidFrom)
            return BadRequest(new { error = "valid_until_must_be_after_valid_from" });

        if (request.ValidUntil <= DateTimeOffset.UtcNow)
            return BadRequest(new { error = "valid_until_must_be_in_the_future" });

        if (request.DesigneeUserId == request.DelegatorUserId)
            return BadRequest(new { error = "cannot_delegate_to_yourself" });

        var parties = await NursingScope.ProxyParties(_db, caller)
            .Where(m => m.UserId == request.DelegatorUserId || m.UserId == request.DesigneeUserId)
            .Select(m => m.UserId).Distinct()
            .ToListAsync(ct);

        if (parties.Count != 2)
            return BadRequest(new { error = "proxy_parties_must_be_roster_staff_in_this_clinic" });

        var permissions = NormalizeProxyPermissions(request.Permissions);
        if (permissions is null)
            return BadRequest(new
            {
                error = "invalid_proxy_permission",
                allowed = DelegatablePermissions
            });

        var proxy = new AuthorityProxy
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            ClinicId = caller.ClinicId.Value,
            DelegatorUserId = request.DelegatorUserId,
            DesigneeUserId = request.DesigneeUserId,
            Permissions = permissions,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            Reason = request.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = caller.UserId
        };

        _db.AuthorityProxies.Add(proxy);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Authority proxy {ProxyId} created by {CallerId}: {Delegator} -> {Designee} ({Permissions})",
            proxy.Id, caller.UserId, proxy.DelegatorUserId, proxy.DesigneeUserId, proxy.Permissions);

        return Ok(proxy);
    }

    [HttpPost("proxies/{id:guid}/revoke")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> RevokeAuthorityProxy(Guid id, CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var proxy = await _db.AuthorityProxies
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == caller.TenantId && p.ClinicId == caller.ClinicId, ct);

        if (proxy is null) return NotFound(new { error = "proxy_not_found" });

        // The clinic admin owns delegation; the delegator can always take their own back.
        if (caller.Role is not (UserRole.ClinicAdmin or UserRole.Owner) && caller.UserId != proxy.DelegatorUserId)
            return Forbid();

        proxy.IsRevoked = true;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Authority proxy {ProxyId} revoked by {CallerId}", proxy.Id, caller.UserId);
        return Ok(new { id = proxy.Id, isRevoked = proxy.IsRevoked });
    }

    [HttpGet("proxies")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ListAuthorityProxies(CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        var parties = NursingScope.ProxyParties(_db, caller);
        var proxies = await _db.AuthorityProxies
            .AsNoTracking()
            .Include(p => p.DelegatorUser)
            .Include(p => p.DesigneeUser)
            .Where(p => p.TenantId == caller.TenantId && p.ClinicId == caller.ClinicId && !p.IsRevoked)
            .OrderByDescending(p => p.ValidUntil)
            .Select(p => new
            {
                id = p.Id,
                delegatorName = parties.Any(m => m.UserId == p.DelegatorUserId) ? p.DelegatorUser.Name : null,
                delegatorId = p.DelegatorUserId,
                designeeName = parties.Any(m => m.UserId == p.DesigneeUserId) ? p.DesigneeUser.Name : null,
                designeeId = p.DesigneeUserId,
                permissions = p.Permissions,
                validFrom = p.ValidFrom,
                validUntil = p.ValidUntil,
                isActive = p.ValidFrom <= DateTimeOffset.UtcNow && p.ValidUntil >= DateTimeOffset.UtcNow &&
                    p.DelegatorUserId != p.DesigneeUserId && parties.Any(m => m.UserId == p.DelegatorUserId) &&
                    parties.Any(m => m.UserId == p.DesigneeUserId),
                reason = p.Reason
            })
            .ToListAsync(ct);

        return Ok(proxies);
    }

    #endregion

    #region Teams & Structured Announcements (NO Chat)

    [HttpGet("announcements")]
    [AuthorizeRoles("ClinicAdmin", "Nurse", "Doctor")]
    public async Task<IActionResult> ListAnnouncements(CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct, UserRole.ClinicAdmin, UserRole.Nurse, UserRole.Doctor);
        if (caller?.ClinicId is null) return Forbid();

        var announcements = await _db.StaffAnnouncements
            .AsNoTracking()
            .Include(a => a.AuthorUser)
            .Where(a => a.TenantId == caller.TenantId && a.ClinicId == caller.ClinicId && (a.ExpiresAt == null || a.ExpiresAt > DateTimeOffset.UtcNow))
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .Select(a => new
            {
                id = a.Id,
                title = a.Title,
                content = a.Content,
                priority = a.Priority,
                authorName = _db.TenantMemberships.Any(m => m.UserId == a.AuthorUserId && m.TenantId == caller.TenantId &&
                    m.ClinicId == caller.ClinicId) ? a.AuthorUser.Name : null,
                createdAt = a.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(announcements);
    }

    [HttpPost("announcements")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    [RequireModule(ModuleRegistry.StaffRoster)]
    public async Task<IActionResult> CreateAnnouncement(
        [FromBody] CreateAnnouncementRequest request,
        CancellationToken ct)
    {
        var caller = await GetCallerAsync(ct);
        if (caller?.ClinicId is null) return Forbid();

        if (request.TeamId.HasValue && !await _db.StaffTeams.AnyAsync(t => t.Id == request.TeamId &&
            t.TenantId == caller.TenantId && t.ClinicId == caller.ClinicId, ct))
            return NotFound(new { error = "team_not_found" });

        var announcement = new StaffAnnouncement
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            ClinicId = caller.ClinicId.Value,
            TeamId = request.TeamId,
            Title = request.Title.Trim(),
            Content = request.Content.Trim(),
            Priority = string.Equals(request.Priority, "urgent", StringComparison.OrdinalIgnoreCase) ? "urgent" : "normal",
            AuthorUserId = caller.UserId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.StaffAnnouncements.Add(announcement);
        await _db.SaveChangesAsync(ct);

        return Ok(announcement);
    }

    #endregion

    /// <summary>Null when any requested permission falls outside the delegatable set.</summary>
    private static string? NormalizeProxyPermissions(string? stored)
    {
        var requested = (stored ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0 || requested.Any(p => !DelegatablePermissions.Contains(p)))
            return null;

        return string.Join(',', requested);
    }

    private static bool IsSwapParty(ShiftChangeRequest swap, Guid? nurseProfileId) =>
        nurseProfileId.HasValue &&
        (swap.RequesterNurseProfileId == nurseProfileId.Value ||
         swap.TargetNurseProfileId == nurseProfileId.Value);

    private async Task<bool> ApproveWouldDoubleAssignAsync(ShiftChangeRequest swap, CancellationToken ct)
    {
        if (!swap.TargetNurseProfileId.HasValue) return false;

        if (await HasDoubleAssignmentAsync(swap.TenantId, swap.ClinicId, swap.TargetNurseProfileId.Value,
                swap.RequesterShift.Date, swap.RequesterShift.ShiftType, swap.RequesterShift.Id, ct))
            return true;

        if (swap.RequestType != ShiftChangeType.DirectSwap || swap.TargetShift is null)
            return false;

        return await HasDoubleAssignmentAsync(swap.TenantId, swap.ClinicId, swap.RequesterNurseProfileId,
            swap.TargetShift.Date, swap.TargetShift.ShiftType, swap.TargetShift.Id, ct);
    }

    private Task<bool> HasDoubleAssignmentAsync(
        Guid tenantId, Guid clinicId, Guid nurseProfileId, DateOnly date, ShiftType shiftType, Guid excludeShiftId, CancellationToken ct) =>
        _db.NurseShifts.AnyAsync(s => s.TenantId == tenantId && s.ClinicId == clinicId && s.NurseProfileId == nurseProfileId &&
            s.Date == date &&
            s.ShiftType == shiftType &&
            s.Status == ShiftStatus.Assigned &&
            s.Id != excludeShiftId, ct);

    /// <summary>
    /// Warning only, never a block: rest between the shift being taken and the adjacent
    /// day's shift. A shift without a template has no known start/end, so it is skipped.
    /// </summary>
    private async Task<List<object>> RestGapWarningsAsync(ShiftChangeRequest swap, CancellationToken ct)
    {
        var warnings = new List<object>();
        var templates = await _db.ShiftTemplates
            .AsNoTracking()
            .Where(t => t.TenantId == swap.TenantId && t.ClinicId == swap.ClinicId)
            .ToDictionaryAsync(t => t.Id, ct);

        async Task CheckAsync(Guid nurseProfileId, NurseShift shift, Guid excludeShiftId)
        {
            if (shift.ShiftTemplateId is null ||
                !templates.TryGetValue(shift.ShiftTemplateId.Value, out var template) ||
                !TimeOnly.TryParse(template.StartTime, out var startTime))
                return;

            var start = shift.Date.ToDateTime(startTime);
            var end = start.AddHours((double)shift.DurationHours);

            var neighbours = await _db.NurseShifts
                .AsNoTracking()
                .Where(s => s.TenantId == swap.TenantId && s.ClinicId == swap.ClinicId && s.NurseProfileId == nurseProfileId &&
                    s.Status == ShiftStatus.Assigned &&
                    s.Id != excludeShiftId &&
                    (s.Date == shift.Date.AddDays(-1) || s.Date == shift.Date.AddDays(1)))
                .ToListAsync(ct);

            foreach (var neighbour in neighbours)
            {
                if (neighbour.ShiftTemplateId is null ||
                    !templates.TryGetValue(neighbour.ShiftTemplateId.Value, out var other) ||
                    !TimeOnly.TryParse(other.StartTime, out var otherStartTime))
                    continue;

                var otherStart = neighbour.Date.ToDateTime(otherStartTime);
                var otherEnd = otherStart.AddHours((double)neighbour.DurationHours);

                var gap = otherEnd <= start
                    ? (start - otherEnd).TotalHours
                    : (otherStart - end).TotalHours;

                if (gap < MinimumRestHours)
                {
                    warnings.Add(new
                    {
                        nurseProfileId,
                        date = shift.Date,
                        hoursRest = Math.Round(Math.Max(0, gap), 1),
                        minimumRestHours = MinimumRestHours,
                        adjacentShiftId = neighbour.Id
                    });
                }
            }
        }

        if (swap.TargetNurseProfileId.HasValue)
            await CheckAsync(swap.TargetNurseProfileId.Value, swap.RequesterShift, swap.RequesterShift.Id);

        if (swap.RequestType == ShiftChangeType.DirectSwap &&
            swap.TargetShift is not null &&
            swap.TargetNurseProfileId.HasValue)
            await CheckAsync(swap.RequesterNurseProfileId, swap.TargetShift, swap.TargetShift.Id);

        return warnings;
    }

    private async Task<bool> ValidSwapPartiesAsync(ShiftChangeRequest swap, TenantMembership caller, CancellationToken ct)
    {
        var profiles = NursingScope.Profiles(_db, caller);
        return await profiles.AnyAsync(p => p.Id == swap.RequesterNurseProfileId, ct) &&
            (!swap.TargetNurseProfileId.HasValue || await profiles.AnyAsync(p => p.Id == swap.TargetNurseProfileId, ct)) &&
            await _db.NurseShifts.AnyAsync(s => s.Id == swap.RequesterShiftId && s.TenantId == caller.TenantId &&
                s.ClinicId == caller.ClinicId && !s.IsLocked &&
                ((s.NurseProfileId == swap.RequesterNurseProfileId && s.Status == ShiftStatus.Assigned) ||
                 (swap.RequestType == ShiftChangeType.OpenCover && swap.RequesterNurseProfileId == swap.TargetNurseProfileId &&
                  s.NurseProfileId == null && s.Status == ShiftStatus.Open)), ct) &&
            (!swap.TargetShiftId.HasValue || await _db.NurseShifts.AnyAsync(s => s.Id == swap.TargetShiftId &&
                s.TenantId == caller.TenantId && s.ClinicId == caller.ClinicId && !s.IsLocked &&
                s.NurseProfileId == swap.TargetNurseProfileId && s.Status == ShiftStatus.Assigned, ct));
    }

    private Task<TenantMembership?> GetCallerAsync(CancellationToken ct, params UserRole[] roles) =>
        NursingScope.SelectedAsync(_db, User, ct, roles.Length == 0 ? [UserRole.ClinicAdmin, UserRole.Nurse] : roles);
}
