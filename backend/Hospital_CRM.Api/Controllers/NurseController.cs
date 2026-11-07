using System.ComponentModel.DataAnnotations;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

/// <summary>
/// MOD-26: Nurse Management Module.
/// Handles Nurse Profiles, Availabilities, Leave Requests, and Balances.
/// Route preserves "/api/v1/staff" prefix for seamless frontend compatibility.
/// </summary>
[ApiController]
[Route("api/v1/staff")]
public class NurseController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<NurseController> _logger;

    public NurseController(HospitalCrmDbContext db, ILogger<NurseController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("nurses")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist", "Doctor")]
    public async Task<IActionResult> ListNurses(CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Receptionist, UserRole.Doctor);
        if (scope is null)
            return Forbid();

        var nurses = await NursingScope.Profiles(_db, scope)
            .OrderBy(n => n.User.Name)
            .Select(n => new
            {
                id = n.Id,
                userId = n.UserId,
                name = n.User.Name,
                email = n.User.Email,
                designation = n.Designation,
                employmentType = n.EmploymentType.ToString(),
                status = n.Status.ToString(),
                licenseNumber = n.LicenseNumber,
                licenseExpiryDate = n.LicenseExpiryDate,
                specialization = n.Specialization,
                yearsExperience = n.YearsExperience,
                wardId = n.WardId,
                departmentId = n.DepartmentId,
                joiningDate = n.JoiningDate
            })
            .ToListAsync(ct);

        return Ok(nurses);
    }

    [HttpGet("nurses/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> GetNurse(Guid id, CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (scope is null)
            return Forbid();

        var nurse = await NursingScope.Profiles(_db, scope)
            .Where(n => n.Id == id && (scope.Role != UserRole.Nurse || n.UserId == scope.UserId))
            .Select(n => new
            {
                id = n.Id,
                userId = n.UserId,
                name = n.User.Name,
                email = n.User.Email,
                designation = n.Designation,
                employmentType = n.EmploymentType.ToString(),
                status = n.Status.ToString(),
                joiningDate = n.JoiningDate,
                licenseNumber = n.LicenseNumber,
                licenseAuthority = n.LicenseAuthority,
                licenseIssueDate = n.LicenseIssueDate,
                licenseExpiryDate = n.LicenseExpiryDate,
                nursingQualification = n.NursingQualification,
                institution = n.Institution,
                graduationYear = n.GraduationYear,
                specialization = n.Specialization,
                yearsExperience = n.YearsExperience,
                skills = n.Skills,
                languages = n.Languages,
                emergencyContactName = n.EmergencyContactName,
                emergencyContactPhone = n.EmergencyContactPhone,
                emergencyContactRelation = n.EmergencyContactRelation,
                departmentId = n.DepartmentId,
                wardId = n.WardId,
                supervisorId = n.SupervisorId,
                documentsJson = n.DocumentsJson,
                shiftPreferencesJson = n.ShiftPreferencesJson,
                workRestrictionsJson = n.WorkRestrictionsJson
            })
            .FirstOrDefaultAsync(ct);

        if (nurse == null)
            return NotFound(new { error = "nurse_not_found" });

        return Ok(nurse);
    }

    [HttpGet("nurses/peers")]
    [AuthorizeRoles("Nurse")]
    public async Task<IActionResult> ListPeers(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.Nurse);
        if (scope is null)
            return Forbid();

        var peers = await NursingScope.Profiles(_db, scope)
            .Where(n => n.UserId != userId.Value)
            .OrderBy(n => n.User.Name)
            .Select(n => new
            {
                id = n.Id,
                userId = n.UserId,
                name = n.User.Name,
                designation = n.Designation,
                wardId = n.WardId,
                departmentId = n.DepartmentId
            })
            .ToListAsync(ct);

        return Ok(peers);
    }

    [HttpPut("nurses/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> UpdateNurse(Guid id, [FromBody] UpdateNurseRequest request, CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (scope is null)
            return Forbid();

        var nurse = await NursingScope.Profiles(_db, scope).FirstOrDefaultAsync(n => n.Id == id, ct);

        if (nurse == null)
            return NotFound(new { error = "nurse_not_found" });

        if (request.DepartmentId.HasValue && !await _db.Departments.AnyAsync(d => d.Id == request.DepartmentId &&
            d.TenantId == scope.TenantId && d.ClinicId == scope.ClinicId, ct))
            return NotFound(new { error = "department_not_found" });
        if (request.SupervisorId.HasValue && !await NursingScope.ProxyParties(_db, scope)
            .AnyAsync(m => m.UserId == request.SupervisorId, ct))
            return NotFound(new { error = "supervisor_not_found" });

        if (!string.IsNullOrWhiteSpace(request.Designation))
            nurse.Designation = request.Designation;
        if (request.EmploymentType.HasValue)
            nurse.EmploymentType = request.EmploymentType.Value;
        if (request.Status.HasValue)
            nurse.Status = request.Status.Value;
        if (!string.IsNullOrWhiteSpace(request.LicenseNumber))
            nurse.LicenseNumber = request.LicenseNumber;
        if (!string.IsNullOrWhiteSpace(request.LicenseAuthority))
            nurse.LicenseAuthority = request.LicenseAuthority;
        if (request.LicenseIssueDate.HasValue)
            nurse.LicenseIssueDate = request.LicenseIssueDate.Value;
        if (request.LicenseExpiryDate.HasValue)
            nurse.LicenseExpiryDate = request.LicenseExpiryDate.Value;
        if (!string.IsNullOrWhiteSpace(request.NursingQualification))
            nurse.NursingQualification = request.NursingQualification;
        if (!string.IsNullOrWhiteSpace(request.Institution))
            nurse.Institution = request.Institution;
        if (request.GraduationYear.HasValue)
            nurse.GraduationYear = request.GraduationYear.Value;
        if (!string.IsNullOrWhiteSpace(request.Specialization))
            nurse.Specialization = request.Specialization;
        if (request.YearsExperience.HasValue)
            nurse.YearsExperience = request.YearsExperience.Value;
        if (!string.IsNullOrWhiteSpace(request.Skills))
            nurse.Skills = request.Skills;
        if (!string.IsNullOrWhiteSpace(request.Languages))
            nurse.Languages = request.Languages;
        if (!string.IsNullOrWhiteSpace(request.EmergencyContactName))
            nurse.EmergencyContactName = request.EmergencyContactName;
        if (!string.IsNullOrWhiteSpace(request.EmergencyContactPhone))
            nurse.EmergencyContactPhone = request.EmergencyContactPhone;
        if (!string.IsNullOrWhiteSpace(request.EmergencyContactRelation))
            nurse.EmergencyContactRelation = request.EmergencyContactRelation;
        if (!string.IsNullOrWhiteSpace(request.DocumentsJson))
            nurse.DocumentsJson = request.DocumentsJson;
        if (!string.IsNullOrWhiteSpace(request.ShiftPreferencesJson))
            nurse.ShiftPreferencesJson = request.ShiftPreferencesJson;
        if (!string.IsNullOrWhiteSpace(request.WorkRestrictionsJson))
            nurse.WorkRestrictionsJson = request.WorkRestrictionsJson;
        if (request.DepartmentId.HasValue)
            nurse.DepartmentId = request.DepartmentId;
        if (request.WardId.HasValue)
            nurse.WardId = request.WardId;
        if (request.SupervisorId.HasValue)
            nurse.SupervisorId = request.SupervisorId;

        nurse.UpdatedAt = DateTimeOffset.UtcNow;
        nurse.UpdatedBy = adminId.Value;

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "nurse_updated" });
    }

    [HttpGet("me/nurse-profile")]
    [AuthorizeRoles("Nurse")]
    public async Task<IActionResult> GetMyProfile(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.Nurse);
        if (scope is null) return Forbid();
        var nurse = await NursingScope.Profiles(_db, scope)
            .Where(n => n.UserId == userId.Value)
            .Select(n => new
            {
                id = n.Id,
                userId = n.UserId,
                designation = n.Designation,
                specialization = n.Specialization,
                wardId = n.WardId,
                departmentId = n.DepartmentId,
                status = n.Status.ToString()
            })
            .FirstOrDefaultAsync(ct);

        if (nurse == null)
            return NotFound(new { error = "nurse_profile_not_found" });

        return Ok(nurse);
    }

    [HttpGet("nurses/{id:guid}/availability")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> GetAvailability(Guid id, CancellationToken ct)
    {
        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (scope is null) return Forbid();
        if (!await CanAccessProfileAsync(scope, id, ct)) return NotFound(new { error = "nurse_not_found" });
        var availabilities = await _db.NurseAvailabilities
            .Where(a => a.NurseProfileId == id && a.TenantId == scope.TenantId)
            .OrderByDescending(a => a.StartDate)
            .Select(a => new
            {
                id = a.Id,
                startDate = a.StartDate,
                endDate = a.EndDate,
                preferredShift = a.PreferredShift.ToString(),
                preferredWardId = a.PreferredWardId,
                nightShiftWilling = a.NightShiftWilling,
                weekendWilling = a.WeekendWilling,
                overtimeWilling = a.OvertimeWilling,
                notes = a.Notes
            })
            .ToListAsync(ct);

        return Ok(availabilities);
    }

    [HttpPost("nurses/{id:guid}/availability")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> SetAvailability(Guid id, [FromBody] SetAvailabilityRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });
        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (scope is null) return Forbid();

        var nurse = await NursingScope.Profiles(_db, scope).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (nurse is null) return NotFound(new { error = "nurse_not_found" });

        if (scope.Role == UserRole.Nurse && nurse.UserId != userId.Value) return Forbid();

        if (request.StartDate > request.EndDate)
            return BadRequest(new { error = "start_date_must_be_before_end_date" });

        var availability = new NurseAvailability
        {
            Id = Guid.NewGuid(),
            NurseProfileId = id,
            TenantId = nurse.TenantId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            PreferredShift = request.PreferredShift,
            PreferredWardId = request.PreferredWardId,
            NightShiftWilling = request.NightShiftWilling,
            WeekendWilling = request.WeekendWilling,
            OvertimeWilling = request.OvertimeWilling,
            Notes = request.Notes ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.NurseAvailabilities.Add(availability);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "availability_set", id = availability.Id });
    }

    [HttpPost("nurses/{id:guid}/leave")]
    [AuthorizeRoles("Nurse")]
    public async Task<IActionResult> RequestLeave(Guid id, [FromBody] CreateLeaveRequest request, CancellationToken ct)
    {
        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.Nurse);
        if (scope is null) return Forbid();
        if (!await CanAccessProfileAsync(scope, id, ct)) return Forbid();

        if (request.StartDate > request.EndDate)
            return BadRequest(new { error = "start_date_must_be_before_end_date" });

        var leave = new LeaveRequest
        {
            Id = Guid.NewGuid(),
            NurseProfileId = id,
            TenantId = scope.TenantId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Type = request.Type,
            Reason = request.Reason ?? string.Empty,
            Status = LeaveStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.LeaveRequests.Add(leave);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "leave_requested", id = leave.Id });
    }

    [HttpGet("nurses/{id:guid}/leave")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ListLeaves(Guid id, CancellationToken ct)
    {
        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (scope is null) return Forbid();
        if (!await CanAccessProfileAsync(scope, id, ct)) return NotFound(new { error = "nurse_not_found" });
        var leaves = await _db.LeaveRequests
            .Where(l => l.NurseProfileId == id && l.TenantId == scope.TenantId)
            .OrderByDescending(l => l.StartDate)
            .Select(l => new
            {
                id = l.Id,
                startDate = l.StartDate,
                endDate = l.EndDate,
                type = l.Type.ToString(),
                reason = l.Reason,
                status = l.Status.ToString(),
                reviewNote = l.ReviewNote,
                reviewedAt = l.ReviewedBy.HasValue ? l.UpdatedAt : (DateTimeOffset?)null
            })
            .ToListAsync(ct);

        return Ok(leaves);
    }

    [HttpGet("leave-requests")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListAllLeaveRequests([FromQuery] string? status, CancellationToken ct)
    {
        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin);
        if (scope is null) return Forbid();
        var profiles = NursingScope.Profiles(_db, scope);
        var query = _db.LeaveRequests
            .Include(l => l.Nurse).ThenInclude(n => n.User)
            .Where(l => l.TenantId == scope.TenantId && profiles.Any(n => n.Id == l.NurseProfileId));

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LeaveStatus>(status, true, out var st))
            query = query.Where(l => l.Status == st);

        var list = await query
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                id = l.Id,
                nurseProfileId = l.NurseProfileId,
                nurseName = l.Nurse.User.Name,
                startDate = l.StartDate,
                endDate = l.EndDate,
                type = l.Type.ToString(),
                reason = l.Reason,
                status = l.Status.ToString(),
                createdAt = l.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpPost("leave-requests/{id:guid}/review")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> ReviewLeave(Guid id, [FromBody] ReviewLeaveRequest request, CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (scope is null)
            return Forbid();

        var profiles = NursingScope.Profiles(_db, scope);
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == scope.TenantId &&
            profiles.Any(n => n.Id == l.NurseProfileId), ct);
        if (leave == null)
            return NotFound(new { error = "leave_request_not_found" });

        if (!Enum.TryParse<LeaveStatus>(request.Status, true, out var newStatus))
            return BadRequest(new { error = "invalid_leave_status" });

        Guid? approvedOnBehalfOf = null;
        var requesterUserId = await profiles.Where(n => n.Id == leave.NurseProfileId).Select(n => n.UserId).SingleAsync(ct);
        if (requesterUserId == scope.UserId)
            return BadRequest(new { error = "cannot_approve_own_leave_request" });
        if (scope.Role == UserRole.Nurse)
        {
            var activeProxy = await NursingScope.ActiveProxyAsync(_db, scope, "leave.approve", ct);

            if (activeProxy is null)
                return Forbid();

            // Acting for the person whose leave is being decided is still self-approval.
            if (activeProxy.DelegatorUserId == requesterUserId)
                return BadRequest(new { error = "cannot_approve_on_behalf_of_leave_requester" });

            approvedOnBehalfOf = activeProxy.DelegatorUserId;
        }

        leave.Status = newStatus;
        leave.ReviewNote = request.Note ?? string.Empty;
        leave.ReviewedBy = adminId.Value;
        leave.ApprovedOnBehalfOfUserId = approvedOnBehalfOf;
        if (request.CertificateSeen.HasValue)
            leave.CertificateSeen = request.CertificateSeen.Value;
        leave.UpdatedAt = DateTimeOffset.UtcNow;

        List<string> rosterConflicts = new();
        if (newStatus == LeaveStatus.Approved)
        {
            var conflictingShifts = await _db.NurseShifts.Where(s =>
                s.TenantId == scope.TenantId && s.ClinicId == scope.ClinicId && s.NurseProfileId == leave.NurseProfileId &&
                s.Date >= leave.StartDate && s.Date <= leave.EndDate &&
                s.Status == ShiftStatus.Assigned).ToListAsync(ct);

            if (conflictingShifts.Count > 0)
            {
                rosterConflicts = conflictingShifts.Select(s => s.Id.ToString()).ToList();
                leave.CoverageGapAlertCreated = true;

                // Release assigned shift to open-shift board so colleagues can claim coverage
                foreach (var shift in conflictingShifts)
                {
                    shift.NurseProfileId = null;
                    shift.Status = ShiftStatus.Open;
                    shift.UpdatedAt = DateTimeOffset.UtcNow;
                    shift.Notes = string.IsNullOrWhiteSpace(shift.Notes)
                        ? "Coverage needed: Nurse on approved leave"
                        : $"{shift.Notes} | Coverage needed: Nurse on approved leave";
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            message = "leave_reviewed",
            status = leave.Status.ToString(),
            hasRosterConflict = rosterConflicts.Count > 0,
            coverageGapAlertCreated = leave.CoverageGapAlertCreated,
            rosterConflicts,
            approvedOnBehalfOf
        });
    }

    [HttpGet("leave-balances/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Nurse")]
    public async Task<IActionResult> GetLeaveBalance(Guid id, [FromQuery] int? year, CancellationToken ct)
    {
        var scope = await NursingScope.SelectedAsync(_db, User, ct, UserRole.ClinicAdmin, UserRole.Nurse);
        if (scope is null) return Forbid();
        if (!await CanAccessProfileAsync(scope, id, ct)) return NotFound(new { error = "nurse_not_found" });
        var targetYear = year ?? DateTime.UtcNow.Year;
        if (targetYear < 1 || targetYear > 9999) return BadRequest(new { error = "invalid_year" });
        var yearStart = new DateOnly(targetYear, 1, 1);
        var yearEnd = new DateOnly(targetYear, 12, 31);

        var approved = await _db.LeaveRequests
            .Where(l => l.TenantId == scope.TenantId && l.NurseProfileId == id && l.Status == LeaveStatus.Approved &&
                        l.StartDate <= yearEnd && l.EndDate >= yearStart)
            .Select(l => new { l.Type, l.StartDate, l.EndDate })
            .ToListAsync(ct);

        var byType = approved
            .GroupBy(l => l.Type.ToString())
            .ToDictionary(g => g.Key, g => g.Sum(l =>
            {
                var s = l.StartDate < yearStart ? yearStart : l.StartDate;
                var e = l.EndDate > yearEnd ? yearEnd : l.EndDate;
                return e.DayNumber - s.DayNumber + 1;
            }));

        return Ok(new { year = yearStart.Year, daysTakenByType = byType, totalDays = byType.Values.Sum() });
    }

    private Task<bool> CanAccessProfileAsync(TenantMembership scope, Guid id, CancellationToken ct) =>
        NursingScope.Profiles(_db, scope).AnyAsync(n => n.Id == id &&
            (scope.Role != UserRole.Nurse || n.UserId == scope.UserId), ct);
}

public record UpdateNurseRequest(
    string? Designation,
    EmploymentType? EmploymentType,
    EmploymentStatus? Status,
    string? LicenseNumber,
    string? LicenseAuthority,
    DateOnly? LicenseIssueDate,
    DateOnly? LicenseExpiryDate,
    string? NursingQualification,
    string? Institution,
    int? GraduationYear,
    string? Specialization,
    int? YearsExperience,
    string? Skills,
    string? Languages,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation,
    string? DocumentsJson,
    string? ShiftPreferencesJson,
    string? WorkRestrictionsJson,
    Guid? DepartmentId,
    Guid? WardId,
    Guid? SupervisorId);

public record SetAvailabilityRequest(
    [Required] DateOnly StartDate,
    [Required] DateOnly EndDate,
    [Required] ShiftPreference PreferredShift,
    Guid? PreferredWardId,
    bool NightShiftWilling,
    bool WeekendWilling,
    bool OvertimeWilling,
    string? Notes);

public record CreateLeaveRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    LeaveType Type,
    string? Reason);

public record ReviewLeaveRequest(
    [Required] string Status,
    string? Note,
    bool? CertificateSeen = null);

