using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// MOD-09 (FR-09-01..03): catalog edits, staff-entered movement quantities, negative-balance
/// warning instead of a hard block, and a single-query low-stock report.
/// </summary>
public sealed class Part2InventoryTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private HospitalCrmDbContext Database(Guid tenant)
    {
        var context = new TenantContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = Principal(tenant) }
        });
        return new HospitalCrmDbContext(
            new DbContextOptionsBuilder<HospitalCrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            context);
    }

    private ClaimsPrincipal Principal(Guid tenant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _user.ToString()),
            new("tenant_id", tenant.ToString()),
            new(ClaimTypes.Role, "ClinicAdmin")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private InventoryController Controller(HospitalCrmDbContext db, Guid tenant) =>
        new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static InventoryItem Item(Guid tenant, string name = "Surgical Gauze",
        InventoryTier tier = InventoryTier.Consumable, bool active = true, int threshold = 10) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Name = name,
            Tier = tier,
            Unit = "pcs",
            Active = active,
            LowStockThreshold = threshold,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static StockMovement Movement(Guid tenant, Guid itemId, int qty, MovementDirection dir) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            ItemId = itemId,
            Quantity = qty,
            Direction = dir,
            RecordedBy = Guid.NewGuid(),
            RecordedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task CreateItem_TrimsNameAndUnit()
    {
        await using var db = Database(_tenant);

        var result = await Controller(db, _tenant).CreateItem(
            new CreateInventoryItemRequest("  Surgical Gauze  ", "consumable", "  pcs  ", 10),
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var item = await db.InventoryItems.SingleAsync();
        Assert.Equal("Surgical Gauze", item.Name);
        Assert.Equal("pcs", item.Unit);
        Assert.Equal(InventoryTier.Consumable, item.Tier);
    }

    [Fact]
    public async Task CreateItem_RejectsNumericTierOutOfRange()
    {
        await using var db = Database(_tenant);

        var result = await Controller(db, _tenant).CreateItem(
            new CreateInventoryItemRequest("Gauze", "99", "pcs", null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("invalid_tier", Json(((BadRequestObjectResult)result).Value));
        Assert.Empty(db.InventoryItems);
    }

    [Theory]
    [InlineData("   ", "pcs")]
    [InlineData("Gauze", "   ")]
    public async Task CreateItem_RejectsBlankNameOrUnit(string name, string unit)
    {
        await using var db = Database(_tenant);

        var result = await Controller(db, _tenant).CreateItem(
            new CreateInventoryItemRequest(name, "consumable", unit, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.InventoryItems);
    }

    [Fact]
    public async Task UpdateItem_RenamesKeepingMovementLinkById()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant, "Gauze 10x10");
        db.InventoryItems.Add(item);
        db.StockMovements.Add(Movement(_tenant, item.Id, 5, MovementDirection.In));
        await db.SaveChangesAsync();
        var movementId = db.StockMovements.Single().Id;

        var result = await Controller(db, _tenant).UpdateItem(
            item.Id, new UpdateInventoryItemRequest("  Gauze 10x10cm  ", null, 25, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.InventoryItems.SingleAsync();
        Assert.Equal("Gauze 10x10cm", saved.Name);
        Assert.Equal(25, saved.LowStockThreshold);
        Assert.Equal(movementId, db.StockMovements.Single().Id);
        Assert.Equal(item.Id, db.StockMovements.Single().ItemId);
    }

    [Fact]
    public async Task UpdateItem_RejectsBlankName()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).UpdateItem(
            item.Id, new UpdateInventoryItemRequest("   ", null, null, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Surgical Gauze", (await db.InventoryItems.SingleAsync()).Name);
    }

    [Fact]
    public async Task UpdateItem_DeactivatesWithoutDeleting()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).UpdateItem(
            item.Id, new UpdateInventoryItemRequest(null, null, null, false), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.InventoryItems.SingleAsync();
        Assert.False(saved.Active);
    }

    [Fact]
    public async Task UpdateItem_OtherTenantItemIsNotFound()
    {
        await using var db = Database(_tenant);
        var foreign = Item(_otherTenant);
        db.InventoryItems.Add(foreign);
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).UpdateItem(
            foreign.Id, new UpdateInventoryItemRequest("Hijacked", null, null, null), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        // The tenant filter hides the row, so read it unfiltered.
        Assert.Equal("Surgical Gauze", (await db.InventoryItems.IgnoreQueryFilters().SingleAsync()).Name);
    }

    [Fact]
    public async Task RecordMovement_IssueBelowZeroWarnsInsteadOfBlocking()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant);
        db.InventoryItems.Add(item);
        db.StockMovements.Add(Movement(_tenant, item.Id, 3, MovementDirection.In));
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).RecordMovement(
            item.Id, new RecordMovementRequest(10, "out", "Issued to minor OT"), CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var body = Json(created.Value);
        Assert.Contains("balance_went_negative", body);
        Assert.Contains("\"balanceAfter\":-7", body);
        Assert.Equal(2, await db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task RecordMovement_AcceptsStaffEnteredQuantity()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        await Controller(db, _tenant).RecordMovement(
            item.Id, new RecordMovementRequest(137, "in", "Invoice INV-8842"), CancellationToken.None);

        var movement = await db.StockMovements.SingleAsync();
        Assert.Equal(137, movement.Quantity);
        Assert.Equal(MovementDirection.In, movement.Direction);
        Assert.Equal("Invoice INV-8842", movement.Note);
        Assert.Equal(_user, movement.RecordedBy);
    }

    [Fact]
    public async Task RecordMovement_RejectsNumericDirectionOutOfRange()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).RecordMovement(
            item.Id, new RecordMovementRequest(5, "99", null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task RecordMovement_RejectsInactiveItem()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant, active: false);
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).RecordMovement(
            item.Id, new RecordMovementRequest(1, "in", null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task ListMovements_IncludesRecordingStaffName()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant);
        db.InventoryItems.Add(item);
        db.Users.Add(new User { Id = _user, Name = "Asha Menon", Email = "asha@clinic.test" });
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant,
            ItemId = item.Id,
            Quantity = 4,
            Direction = MovementDirection.Out,
            Note = "Minor OT",
            RecordedBy = _user,
            RecordedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).ListMovements(item.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Asha Menon", Json(ok.Value));
    }

    [Fact]
    public async Task LowStock_ReturnsOnlyActiveItemsAtOrBelowThreshold()
    {
        await using var db = Database(_tenant);
        var low = Item(_tenant, "Low Gauze", threshold: 10);
        var ok = Item(_tenant, "Plenty Gloves", threshold: 10);
        var noThreshold = Item(_tenant, "No Threshold", threshold: 0);
        var inactive = Item(_tenant, "Inactive Tape", threshold: 10, active: false);
        db.InventoryItems.AddRange(low, ok, noThreshold, inactive);
        db.StockMovements.AddRange(
            Movement(_tenant, low.Id, 8, MovementDirection.In),
            Movement(_tenant, ok.Id, 40, MovementDirection.In),
            Movement(_tenant, inactive.Id, 1, MovementDirection.In));
        await db.SaveChangesAsync();

        var result = await Controller(db, _tenant).LowStock(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var body = Json(okResult.Value);
        Assert.Contains("Low Gauze", body);
        Assert.DoesNotContain("Plenty Gloves", body);
        Assert.DoesNotContain("No Threshold", body);
        Assert.DoesNotContain("Inactive Tape", body);
    }
}
