using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;

    public UsersController(HospitalCrmDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? role, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || userId is null || userId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });

        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        if (tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty
            || _db.CurrentTenantId != tenantId
            || !Enum.TryParse<UserRole>(User.GetUserRole(), true, out var callerRole)
            || !Enum.IsDefined(callerRole) || callerRole == UserRole.PlatformAdmin)
            return Forbid();

        // Users and memberships are global; a missing selected scope must never open the directory.
        if (!await _db.TenantMemberships.AnyAsync(m => m.UserId == userId && m.TenantId == tenantId
            && m.ClinicId == clinicId && m.Status == "active" && m.Role == callerRole
            && m.Clinic != null && m.Clinic.TenantId == tenantId, ct))
            return Forbid();

        var q = _db.TenantMemberships.AsNoTracking().Where(m => m.TenantId == tenantId
            && m.ClinicId == clinicId && m.Status == "active" && m.Role != UserRole.PlatformAdmin);

        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<UserRole>(role, true, out var userRole) || !Enum.IsDefined(userRole))
                return BadRequest(new { error = "invalid_role" });
            q = q.Where(m => m.Role == userRole);
        }

        var users = await q
            .OrderBy(m => m.User.Name)
            .Select(m => new
            {
                id = m.UserId,
                name = m.User.Name,
                email = m.User.Email,
                role = m.Role.ToString(),
                specialty = m.Specialty
            })
            .ToListAsync(ct);

        return Ok(users);
    }
}
