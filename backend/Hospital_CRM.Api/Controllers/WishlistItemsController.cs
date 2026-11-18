using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/wishlist-items")]
[RequireModule(ModuleRegistry.Wishlist)]
public class WishlistItemsController : ControllerBase
{
    // Matches WishlistItem.Text HasMaxLength; a longer value would fail in PostgreSQL as a 500.
    private const int MaxTextLength = 1000;
    private readonly HospitalCrmDbContext _db;

    public WishlistItemsController(HospitalCrmDbContext db) => _db = db;

    private static bool TryParseDefined<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value) &&
               Enum.TryParse(value, true, out result) &&
               Enum.IsDefined(result);
    }

    [HttpGet]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var query = _db.WishlistItems.Where(w => w.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseDefined<WishlistStatus>(status, out var statusEnum))
                return BadRequest(new { error = "invalid_status" });
            query = query.Where(w => w.Status == statusEnum);
        }

        var items = await query
            .OrderBy(w => w.Status)
            .ThenByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                id = w.Id,
                text = w.Text,
                category = w.Category.ToString().ToLower(),
                status = w.Status.ToString().ToLower(),
                createdBy = w.CreatedBy,
                // Correlated lookup, not a navigation join: a missing or out-of-scope author must not drop the row.
                createdByName = _db.Users.Where(u => u.Id == w.CreatedBy).Select(u => u.Name).FirstOrDefault(),
                createdAt = w.CreatedAt,
                updatedAt = w.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> Create([FromBody] CreateWishlistItemRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "text_required" });

        var text = request.Text.Trim();
        if (text.Length > MaxTextLength)
            return BadRequest(new { error = "text_too_long", maxLength = MaxTextLength });

        if (!TryParseDefined<WishlistCategory>(request.Category, out var category))
            return BadRequest(new { error = "invalid_category" });

        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var item = new WishlistItem
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CreatedBy = userId.Value,
            Text = text,
            Category = category,
            Status = WishlistStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.WishlistItems.Add(item);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = item.Id,
            text = item.Text,
            category = item.Category.ToString().ToLower(),
            status = item.Status.ToString().ToLower()
        });
    }

    [HttpPatch("{id:guid}")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWishlistItemRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var item = await _db.WishlistItems.FirstOrDefaultAsync(w => w.Id == id && w.TenantId == tenantId, ct);
        if (item is null)
            return NotFound(new { error = "item_not_found" });

        if (request.Text is not null)
        {
            var text = request.Text.Trim();
            if (text.Length == 0)
                return BadRequest(new { error = "text_required" });
            if (text.Length > MaxTextLength)
                return BadRequest(new { error = "text_too_long", maxLength = MaxTextLength });
            item.Text = text;
        }

        if (request.Category is not null)
        {
            if (!TryParseDefined<WishlistCategory>(request.Category, out var category))
                return BadRequest(new { error = "invalid_category" });
            item.Category = category;
        }

        if (request.Status is not null)
        {
            if (!TryParseDefined<WishlistStatus>(request.Status, out var status))
                return BadRequest(new { error = "invalid_status" });
            item.Status = status;
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = item.Id,
            text = item.Text,
            category = item.Category.ToString().ToLower(),
            status = item.Status.ToString().ToLower()
        });
    }
}

public record CreateWishlistItemRequest(string Text, string Category);
public record UpdateWishlistItemRequest(string? Text, string? Category, string? Status);
