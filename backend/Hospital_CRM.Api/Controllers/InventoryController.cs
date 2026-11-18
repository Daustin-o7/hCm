using System.ComponentModel.DataAnnotations;
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
[Route("api/v1/inventory")]
[RequireModule(ModuleRegistry.Inventory)]
public class InventoryController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;

    public InventoryController(HospitalCrmDbContext db) => _db = db;

    // ----- FR-09-01: Manage Item Catalog -----

    [HttpGet("items")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist", "Doctor")]
    public async Task<IActionResult> ListItems([FromQuery] bool? activeOnly, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var query = _db.InventoryItems.Where(i => i.TenantId == tenantId);
        if (activeOnly ?? true) query = query.Where(i => i.Active);

        var items = await query
            .OrderBy(i => i.Name)
            .ToListAsync(ct);

        // Aggregate every movement in SQL (one row per item) — a Contains/ANY
        // filter crashes Npgsql's SqlNullabilityProcessor (PgAnyExpression).
        var balances = await _db.StockMovements
            .GroupBy(m => m.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                Total = g.Sum(m => m.Direction == MovementDirection.In ? m.Quantity : -m.Quantity)
            })
            .ToDictionaryAsync(x => x.ItemId, x => x.Total, ct);

        return Ok(items.Select(i => new
        {
            id = i.Id,
            name = i.Name,
            tier = i.Tier.ToString().ToLower(),
            unit = i.Unit,
            active = i.Active,
            lowStockThreshold = i.LowStockThreshold,
            balance = balances.GetValueOrDefault(i.Id, 0),
            createdAt = i.CreatedAt
        }));
    }

    [HttpPost("items")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    public async Task<IActionResult> CreateItem([FromBody] CreateInventoryItemRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var unit = request.Unit?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(unit))
            return BadRequest(new { error = "name_and_unit_required" });

        // Enum.TryParse alone accepts "99", which is not a tier.
        if (!Enum.TryParse<InventoryTier>(request.Tier, true, out var tier) || !Enum.IsDefined(tier))
            return BadRequest(new { error = "invalid_tier" });

        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            TenantId = User.GetTenantId() ?? Guid.Empty,
            Name = name,
            Tier = tier,
            Unit = unit,
            LowStockThreshold = request.LowStockThreshold ?? 0,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.InventoryItems.Add(item);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = item.Id,
            name = item.Name,
            tier = item.Tier.ToString().ToLower(),
            unit = item.Unit,
            lowStockThreshold = item.LowStockThreshold
        });
    }

    [HttpPatch("items/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    public async Task<IActionResult> UpdateItem(Guid id, [FromBody] UpdateInventoryItemRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var item = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (item is null)
            return NotFound(new { error = "item_not_found" });

        // Soft-deactivate only — historical stock_movements must still reference this row
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0) return BadRequest(new { error = "name_required" });
            item.Name = name;
        }
        if (request.Unit is not null)
        {
            var unit = request.Unit.Trim();
            if (unit.Length == 0) return BadRequest(new { error = "unit_required" });
            item.Unit = unit;
        }
        if (request.LowStockThreshold.HasValue) item.LowStockThreshold = request.LowStockThreshold.Value;
        if (request.Active.HasValue) item.Active = request.Active.Value;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = item.Id,
            name = item.Name,
            tier = item.Tier.ToString().ToLower(),
            unit = item.Unit,
            active = item.Active,
            lowStockThreshold = item.LowStockThreshold
        });
    }

    // ----- FR-09-02: Record Stock Movement -----

    [HttpPost("items/{id:guid}/movements")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    public async Task<IActionResult> RecordMovement(Guid id, [FromBody] RecordMovementRequest request, CancellationToken ct)
    {
        if (request.Quantity <= 0)
            return BadRequest(new { error = "quantity_must_be_positive" });

        // TryParse alone accepts "99", which is neither in nor out.
        if (!Enum.TryParse<MovementDirection>(request.Direction, true, out var direction) || !Enum.IsDefined(direction))
            return BadRequest(new { error = "invalid_direction" });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var item = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (item is null)
            return NotFound(new { error = "item_not_found" });

        if (!item.Active)
            return BadRequest(new { error = "item_is_inactive" });

        // FR-09-02 edge case: movement that would take balance negative → WARN, don't hard-block
        // (physical stock corrections happen; a hard block would just get staff to enter fake numbers)
        int? currentBalance = null;
        bool wouldGoNegative = false;
        if (direction == MovementDirection.Out)
        {
            currentBalance = await GetBalanceAsync(item.Id, ct);
            wouldGoNegative = (currentBalance.Value - request.Quantity) < 0;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var movement = new StockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ItemId = id,
            Quantity = request.Quantity,
            Direction = direction,
            Note = request.Note,
            RecordedBy = userId.Value,
            RecordedAt = DateTimeOffset.UtcNow
        };
        _db.StockMovements.Add(movement);
        await _db.SaveChangesAsync(ct);

        // Recompute balance after insert
        var newBalance = await GetBalanceAsync(item.Id, ct);

        return StatusCode(201, new
        {
            movementId = movement.Id,
            itemId = item.Id,
            direction = direction.ToString().ToLower(),
            quantity = movement.Quantity,
            balanceBefore = currentBalance,
            balanceAfter = newBalance,
            wouldGoNegative = wouldGoNegative,
            warning = wouldGoNegative ? "balance_went_negative" : null
        });
    }

    private async Task<int> GetBalanceAsync(Guid itemId, CancellationToken ct)
    {
        var movements = await _db.StockMovements
            .Where(m => m.ItemId == itemId)
            .Select(m => new { m.Direction, m.Quantity })
            .ToListAsync(ct);
        var inQty = movements.Where(m => m.Direction == MovementDirection.In).Sum(m => m.Quantity);
        var outQty = movements.Where(m => m.Direction == MovementDirection.Out).Sum(m => m.Quantity);
        return inQty - outQty;
    }

    [HttpGet("items/{id:guid}/movements")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist", "Doctor")]
    public async Task<IActionResult> ListMovements(Guid id, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var movements = await _db.StockMovements
            .Where(m => m.TenantId == tenantId && m.ItemId == id)
            .OrderByDescending(m => m.RecordedAt)
            .Select(m => new
            {
                id = m.Id,
                quantity = m.Quantity,
                direction = m.Direction.ToString().ToLower(),
                note = m.Note,
                recordedBy = m.RecordedBy,
                recordedByName = m.Recorder.Name,
                recordedAt = m.RecordedAt
            })
            .ToListAsync(ct);
        return Ok(movements);
    }

    // ----- FR-09-03: Low-Stock Report -----

    [HttpGet("low-stock")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> LowStock(CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var items = await _db.InventoryItems
            .Where(i => i.TenantId == tenantId && i.Active && i.LowStockThreshold > 0)
            .ToListAsync(ct);

        // One grouped query instead of one per item (FR-09-03 performance NFR).
        var balances = await _db.StockMovements
            .Where(m => m.TenantId == tenantId)
            .GroupBy(m => m.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                Total = g.Sum(m => m.Direction == MovementDirection.In ? m.Quantity : -m.Quantity)
            })
            .ToDictionaryAsync(x => x.ItemId, x => x.Total, ct);

        var report = items
            .Select(item => new { item, balance = balances.GetValueOrDefault(item.Id, 0) })
            .Where(x => x.balance <= x.item.LowStockThreshold)
            .Select(x => new
            {
                itemId = x.item.Id,
                name = x.item.Name,
                tier = x.item.Tier.ToString().ToLower(),
                unit = x.item.Unit,
                balance = x.balance,
                lowStockThreshold = x.item.LowStockThreshold
            })
            .ToList();
        return Ok(report);
    }
}

public record CreateInventoryItemRequest(
    [Required(ErrorMessage = "Item Name is required"), StringLength(150, MinimumLength = 2, ErrorMessage = "Item Name must be between 2 and 150 characters")]
    string Name,

    [Required(ErrorMessage = "Tier is required")]
    string Tier,

    [Required(ErrorMessage = "Unit is required"), StringLength(50, MinimumLength = 1)]
    string Unit,

    [Range(0, 1000000, ErrorMessage = "LowStockThreshold must be >= 0")]
    int? LowStockThreshold);

public record UpdateInventoryItemRequest(
    [StringLength(150, MinimumLength = 2, ErrorMessage = "Item Name must be between 2 and 150 characters")]
    string? Name,

    [StringLength(50, MinimumLength = 1)]
    string? Unit,

    [Range(0, 1000000, ErrorMessage = "LowStockThreshold must be >= 0")]
    int? LowStockThreshold,

    bool? Active);

public record RecordMovementRequest(
    [Range(1, 1000000, ErrorMessage = "Movement Quantity must be at least 1")]
    int Quantity,

    [Required(ErrorMessage = "Direction is required"), RegularExpression("(?i)^(in|out)$", ErrorMessage = "Direction must be 'in' or 'out'")]
    string Direction,

    [StringLength(500, ErrorMessage = "Note cannot exceed 500 characters")]
    string? Note);
