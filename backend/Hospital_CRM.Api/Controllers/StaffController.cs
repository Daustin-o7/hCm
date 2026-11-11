using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class StaffController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<StaffController> _logger;
    private readonly IEmailSender? _email;
    private readonly IConfiguration? _config;

    public StaffController(HospitalCrmDbContext db, ITenantContext tenantContext, ILogger<StaffController> logger,
        IEmailSender? email = null, IConfiguration? config = null)
    {
        _db = db;
        _tenantContext = tenantContext;
        _logger = logger;
        _email = email;
        _config = config;
    }

    [HttpPost("invite")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Invite([FromBody] StaffInviteRequest request, CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedAdminMembershipAsync(ct);
        if (membership is null)
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "name_and_email_required" });

        if (!StaffRolePolicy.TryParseInvitableRole(request.Role, allowClinicAdmin: false, out var role))
            return BadRequest(new { error = "invalid_role" });

        if (!new EmailAddressAttribute().IsValid(request.Email) || request.Email.Length > 254)
            return BadRequest(new { error = "invalid_email" });

        var email = request.Email.Trim();
        var existingUser = await _db.Users.AnyAsync(u => u.Email == email, ct);
        if (existingUser)
            return Conflict(new { error = "email_already_registered" });

        if (await _db.StaffInvites.AnyAsync(i => i.TenantId == membership.TenantId
            && i.ClinicId == membership.ClinicId && i.Email == email
            && i.AcceptedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow, ct))
            return Conflict(new { error = "invite_already_pending", message = "Cancel the pending invitation before issuing a fresh link." });

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenHash = rawToken.HashToken();

        var invite = new StaffInvite
        {
            Id = Guid.NewGuid(),
            TenantId = membership.TenantId,
            ClinicId = membership.ClinicId!.Value,
            Name = request.Name.Trim(),
            Email = email,
            Role = role,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(72) // FR-04
        };

        if (request.Role.Equals("Doctor", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(request.Specialty))
        {
            invite.Specialization = request.Specialty ?? string.Empty;
        }

        if (request.Role.Equals("Nurse", StringComparison.OrdinalIgnoreCase))
        {
            invite.LicenseNumber = request.LicenseNumber ?? string.Empty;
            invite.LicenseAuthority = request.LicenseAuthority ?? string.Empty;
            invite.LicenseIssueDate = request.LicenseIssueDate;
            invite.LicenseExpiryDate = request.LicenseExpiryDate;
            invite.NursingQualification = request.NursingQualification ?? string.Empty;
            invite.Institution = request.Institution ?? string.Empty;
            invite.GraduationYear = request.GraduationYear ?? 0;
            invite.Specialization = request.Specialization ?? string.Empty;
            invite.YearsExperience = request.YearsExperience ?? 0;
            invite.Skills = request.Skills ?? string.Empty;
            invite.Languages = request.Languages ?? string.Empty;
            invite.EmergencyContactName = request.EmergencyContactName ?? string.Empty;
            invite.EmergencyContactPhone = request.EmergencyContactPhone ?? string.Empty;
            invite.EmergencyContactRelation = request.EmergencyContactRelation ?? string.Empty;
            invite.ShiftPreferencesJson = request.ShiftPreferencesJson ?? string.Empty;
            invite.WorkRestrictionsJson = request.WorkRestrictionsJson ?? string.Empty;
            invite.DepartmentId = request.DepartmentId;
            invite.WardId = request.WardId;
            invite.Designation = request.Designation ?? string.Empty;
            invite.EmploymentType = request.EmploymentType ?? EmploymentType.FullTime;
            invite.EmploymentStatus = request.EmploymentStatus ?? EmploymentStatus.Active;
        }

        _db.StaffInvites.Add(invite);
        await _db.SaveChangesAsync(ct);

        var emailStatus = await IdentityEmail.InviteAsync(_email, _config, invite.Email, rawToken, invite.ExpiresAt, ct);
        _logger.LogInformation("Invitation {InviteId} email transport outcome: {Outcome}", invite.Id, emailStatus);

        return Ok(new
        {
            inviteId = invite.Id,
            inviteToken = rawToken,
            expiresAt = invite.ExpiresAt,
            emailStatus,
            emailRetry = emailStatus == "handoff" ? null : "Share the generated link, or cancel this invite and issue a fresh invitation."
        });
    }

    [HttpPost("accept-invite")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> AcceptInvite([FromBody] AcceptInviteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InviteToken) || request.InviteToken.Length > 512)
            return Unauthorized(new { error = "invalid_or_expired_invite" });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8 || request.Password.Length > 72)
            return BadRequest(new { error = "invalid_password_length" });

        // Digest lookup: one indexed read, and a stolen database row cannot be replayed.
        var tokenHash = request.InviteToken.HashToken();
        var invite = await _db.StaffInvites
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash
                                      && i.ExpiresAt > DateTimeOffset.UtcNow
                                      && i.AcceptedAt == null, ct);

        if (invite is null || invite.TenantId == Guid.Empty || invite.ClinicId == Guid.Empty)
            return Unauthorized(new { error = "invalid_or_expired_invite" });

        using var tenantScope = _tenantContext.PushTenant(invite.TenantId);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct) : null;
        if (_db.Database.IsRelational())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.current_tenant_id', {invite.TenantId.ToString()}, true), set_config('app.is_platform_admin', 'false', true)", ct);

        if (!StaffRolePolicy.TryParseInvitableRole(invite.Role.ToString(), allowClinicAdmin: true, out _)
            || !await _db.Clinics.AnyAsync(c => c.Id == invite.ClinicId && c.TenantId == invite.TenantId, ct))
            return Unauthorized(new { error = "invalid_or_expired_invite" });

        var acceptedAt = DateTimeOffset.UtcNow;
        if (_db.Database.IsRelational() && await _db.StaffInvites
            .Where(i => i.Id == invite.Id && i.TokenHash == tokenHash && i.AcceptedAt == null && i.ExpiresAt > acceptedAt)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, acceptedAt), ct) != 1)
            return Unauthorized(new { error = "invalid_or_expired_invite" });

        var existingUser = await _db.Users.AnyAsync(u => u.Email == invite.Email, ct);
        if (existingUser)
            return Conflict(new { error = "email_already_registered" });

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = invite.TenantId,
            ClinicId = invite.ClinicId,
            Name = invite.Name,
            Email = invite.Email,
            PasswordHash = request.Password.HashPassword(),
            Role = invite.Role,
            Specialty = !string.IsNullOrWhiteSpace(invite.Specialization) ? invite.Specialization : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Users.Add(user);

        // Section 0: invited staff get an explicit membership, not a legacy User.TenantId row.
        _db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantId = invite.TenantId,
            ClinicId = invite.ClinicId,
            Role = invite.Role,
            Specialty = user.Specialty,
            IsDefault = true,
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // Create NurseProfile if role is Nurse
        if (invite.Role == UserRole.Nurse)
        {
            var nurseProfile = new NurseProfile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TenantId = user.TenantId ?? invite.TenantId,
                DepartmentId = invite.DepartmentId,
                WardId = invite.WardId,
                Designation = invite.Designation ?? "Staff Nurse",
                EmploymentType = invite.EmploymentType,
                JoiningDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Status = invite.EmploymentStatus,
                LicenseNumber = invite.LicenseNumber ?? "N/A",
                LicenseAuthority = invite.LicenseAuthority ?? "Nursing Council",
                LicenseIssueDate = invite.LicenseIssueDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                LicenseExpiryDate = invite.LicenseExpiryDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddYears(5)),
                NursingQualification = invite.NursingQualification ?? "General Nursing",
                Institution = invite.Institution ?? "Medical College",
                GraduationYear = invite.GraduationYear,
                Specialization = invite.Specialization ?? "General",
                YearsExperience = invite.YearsExperience,
                Skills = invite.Skills ?? string.Empty,
                Languages = invite.Languages ?? string.Empty,
                EmergencyContactName = invite.EmergencyContactName ?? string.Empty,
                EmergencyContactPhone = invite.EmergencyContactPhone ?? string.Empty,
                EmergencyContactRelation = invite.EmergencyContactRelation ?? string.Empty,
                ShiftPreferencesJson = invite.ShiftPreferencesJson ?? string.Empty,
                WorkRestrictionsJson = invite.WorkRestrictionsJson ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _db.NurseProfiles.Add(nurseProfile);
            invite.NurseProfileId = nurseProfile.Id;
        }

        invite.AcceptedAt = acceptedAt;
        try
        {
            await _db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "invite_acceptance_conflict" });
        }

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.RoleGranted, "success",
            user.Id, invite.TenantId, user.Email, $"role={invite.Role}", ct);

        return Ok(new { message = "invite_accepted", userId = user.Id });
    }

    [HttpGet("invites")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListInvites(CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedAdminMembershipAsync(ct);
        if (membership is null)
            return Forbid();

        var invites = await _db.StaffInvites
            .Where(i => i.TenantId == membership.TenantId && i.ClinicId == membership.ClinicId && i.AcceptedAt == null && i.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(i => i.ExpiresAt)
            .Select(i => new
            {
                id = i.Id,
                name = i.Name,
                email = i.Email,
                role = i.Role.ToString().ToLower(),
                expiresAt = i.ExpiresAt
            })
            .ToListAsync(ct);

        return Ok(invites);
    }

    [HttpDelete("invites/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> CancelInvite(Guid id, CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedAdminMembershipAsync(ct);
        if (membership is null)
            return Forbid();

        var pending = _db.StaffInvites.Where(i => i.Id == id && i.TenantId == membership.TenantId
            && i.ClinicId == membership.ClinicId && i.AcceptedAt == null);
        if (_db.Database.IsRelational())
        {
            // Acceptance and cancellation contend on the same pending row; accepted history cannot be deleted.
            if (await pending.ExecuteDeleteAsync(ct) != 1)
                return NotFound(new { error = "invite_not_found" });
        }
        else
        {
            var invite = await pending.SingleOrDefaultAsync(ct);
            if (invite is null) return NotFound(new { error = "invite_not_found" });
            _db.StaffInvites.Remove(invite);
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new { message = "invite_cancelled" });
    }

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListStaff(CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!adminId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedAdminMembershipAsync(ct);
        if (membership is null)
            return Forbid();

        var staff = await _db.TenantMemberships
            .Where(m => m.TenantId == membership.TenantId && m.ClinicId == membership.ClinicId
                && m.Status == "active" && m.Role != UserRole.PlatformAdmin)
            .OrderBy(m => m.User.Name)
            .Select(m => new
            {
                id = m.UserId,
                name = m.User.Name,
                email = m.User.Email,
                role = m.Role.ToString().ToLower(),
                specialty = m.Specialty,
                // Signing a consultation is refused without these; the directory shows which doctors lack them.
                registrationNumber = m.RegistrationNumber,
                qualifications = m.Qualifications,
                status = m.Status,
                joinedAt = m.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(staff);
    }

    /// <summary>
    /// Sets the credentials a doctor's signature carries (FR-14). Without them
    /// FinalizeConsultation returns signing_credentials_required, so this is the only path
    /// that makes a real clinic able to sign at all.
    /// </summary>
    [HttpPatch("{userId:guid}/credentials")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> UpdateSigningCredentials(Guid userId,
        [FromBody] SigningCredentialsRequest request, CancellationToken ct)
    {
        var admin = await SelectedAdminMembershipAsync(ct);
        if (admin is null) return Forbid();

        var registration = request.RegistrationNumber?.Trim();
        var qualifications = request.Qualifications?.Trim();
        if (registration?.Length is not (>= 2 and <= 100) || qualifications?.Length is not (>= 2 and <= 200))
            return BadRequest(new { error = "credentials_required", message = "A signing doctor needs both a registration number and qualifications." });

        var doctor = await _db.TenantMemberships.FirstOrDefaultAsync(m =>
            m.UserId == userId && m.TenantId == admin.TenantId && m.ClinicId == admin.ClinicId
            && m.Status == "active" && m.Role == UserRole.Doctor, ct);
        if (doctor is null) return NotFound(new { error = "doctor_membership_not_found" });

        doctor.RegistrationNumber = registration;
        doctor.Qualifications = qualifications;
        doctor.UpdatedAt = DateTimeOffset.UtcNow;

        // Unlike best-effort login auditing, a credential change must not report success if its audit fails.
        _db.AuthAuditLogs.Add(new AuthAuditLog
        {
            EventType = AuthEvent.SigningCredentialsUpdated, Outcome = "success",
            UserId = admin.UserId, TenantId = admin.TenantId, Detail = $"doctor={userId}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString(), CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            registrationNumber = doctor.RegistrationNumber,
            qualifications = doctor.Qualifications
        });
    }

    private Task<TenantMembership?> SelectedAdminMembershipAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        if (userId is null || tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty)
            return Task.FromResult<TenantMembership?>(null);
        return _db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(m =>
            m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active"
            && (m.Role == UserRole.ClinicAdmin || m.Role == UserRole.Owner) && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
    }
}

public record StaffInviteRequest(
    [Required(ErrorMessage = "Staff Name is required"), StringLength(100, MinimumLength = 2, ErrorMessage = "Staff Name must be between 2 and 100 characters")]
    string Name,

    [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Valid email address required"), StringLength(254)]
    string Email,

    [Required(ErrorMessage = "Role is required")]
    string Role,

    // Doctor specialization
    string? Specialty = null,

    // Nurse-specific optional fields
    string? LicenseNumber = null,
    string? LicenseAuthority = null,
    DateOnly? LicenseIssueDate = null,
    DateOnly? LicenseExpiryDate = null,
    string? NursingQualification = null,
    string? Institution = null,
    int? GraduationYear = null,
    string? Specialization = null,
    int? YearsExperience = null,
    string? Skills = null,
    string? Languages = null,
    string? EmergencyContactName = null,
    string? EmergencyContactPhone = null,
    string? EmergencyContactRelation = null,
    string? ShiftPreferencesJson = null,
    string? WorkRestrictionsJson = null,
    Guid? DepartmentId = null,
    Guid? WardId = null,
    string? Designation = null,
    EmploymentType? EmploymentType = null,
    EmploymentStatus? EmploymentStatus = null);

public record SigningCredentialsRequest(
    [Required(ErrorMessage = "Registration number is required"), StringLength(100, MinimumLength = 2)]
    string RegistrationNumber,

    [Required(ErrorMessage = "Qualifications are required"), StringLength(200, MinimumLength = 2)]
    string Qualifications);

public record AcceptInviteRequest(
    [Required(ErrorMessage = "InviteToken is required")]
    string InviteToken,

    [Required(ErrorMessage = "Password is required"), MinLength(8, ErrorMessage = "Password must be at least 8 characters"), MaxLength(72)]
    string Password);
