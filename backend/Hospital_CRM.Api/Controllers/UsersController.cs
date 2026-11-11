using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
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
        var q = _db.Users.AsNoTracking().AsQueryable();

        // User carries no tenant query filter, so the staff directory is scoped by membership here:
        // otherwise any signed-in user can enumerate every tenant's staff names and emails.
        var tenantId = _db.CurrentTenantId;
        var clinicId = User.GetClinicId();
        if (tenantId != Guid.Empty)
            q = q.Where(u => _db.TenantMemberships.Any(m => m.UserId == u.Id && m.TenantId == tenantId
                && (clinicId == null || m.ClinicId == clinicId) && m.Status == "active"));

        if (!string.IsNullOrWhiteSpace(role))
        {
            if (Enum.TryParse<UserRole>(role, true, out var userRole))
            {
                q = q.Where(u => u.Role == userRole);
            }
        }

        var users = await q
            .OrderBy(u => u.Name)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                email = u.Email,
                role = u.Role.ToString(),
                specialty = u.Specialty
            })
            .ToListAsync(ct);

        return Ok(users);
    }
}
