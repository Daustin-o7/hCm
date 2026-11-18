using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hospital_CRM.Tests;

public sealed class Part2WishlistItemsTests
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
            new(ClaimTypes.Role, "Doctor")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private WishlistItemsController Controller(HospitalCrmDbContext db, Guid tenant) =>
        new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };

    private WishlistItem Item(
        Guid tenant,
        string text,
        WishlistCategory category = WishlistCategory.Task,
        WishlistStatus status = WishlistStatus.Open,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            CreatedBy = _user,
            Text = text,
            Category = category,
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };

    private static string Error(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task Create_TrimsText_UsesClaimCreator_AndOpensItem()
    {
        await using var db = Database(_tenant);

        var response = await Controller(db, _tenant)
            .Create(new CreateWishlistItemRequest("  Replace centrifuge  ", "Equipment"), default);

        Assert.Equal(201, Assert.IsType<ObjectResult>(response).StatusCode);
        var item = await db.WishlistItems.SingleAsync();
        Assert.Equal("Replace centrifuge", item.Text);
        Assert.Equal(_tenant, item.TenantId);
        Assert.Equal(_user, item.CreatedBy);
        Assert.Equal(WishlistCategory.Equipment, item.Category);
        Assert.Equal(WishlistStatus.Open, item.Status);
    }

    [Theory]
    [InlineData("", "task", "text_required")]
    [InlineData("   ", "task", "text_required")]
    [InlineData("Valid", "nonsense", "invalid_category")]
    [InlineData("Valid", "99", "invalid_category")]
    public async Task Create_Rejects_Invalid_Input_Without_Writing(string text, string category, string error)
    {
        await using var db = Database(_tenant);

        var response = await Controller(db, _tenant)
            .Create(new CreateWishlistItemRequest(text, category), default);

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains(error, Error(bad.Value));
        Assert.Empty(await db.WishlistItems.ToListAsync());
    }

    [Fact]
    public async Task Create_Rejects_Text_Over_The_Database_Maximum()
    {
        await using var db = Database(_tenant);

        var response = await Controller(db, _tenant)
            .Create(new CreateWishlistItemRequest(new string('a', 1001), "task"), default);

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains("text_too_long", Error(bad.Value));
        Assert.Empty(await db.WishlistItems.ToListAsync());
    }

    [Fact]
    public async Task Create_Accepts_Text_At_The_Database_Maximum()
    {
        await using var db = Database(_tenant);

        var response = await Controller(db, _tenant)
            .Create(new CreateWishlistItemRequest(new string('a', 1000), "task"), default);

        Assert.Equal(201, Assert.IsType<ObjectResult>(response).StatusCode);
    }

    [Fact]
    public async Task List_FiltersByStatus_OrdersOpenFirstThenNewest_AndIsolatesTenant()
    {
        await using var db = Database(_tenant);
        var older = Item(_tenant, "older open", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        var newer = Item(_tenant, "newer open", createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var done = Item(_tenant, "done item", status: WishlistStatus.Done);
        // CreatedBy is a required FK, so the author row must exist or the projection's join drops the item.
        db.Users.Add(new User
        {
            Id = _user, TenantId = _tenant, Name = "Dr. Author", Role = UserRole.Doctor,
            Email = "author@synthetic.invalid", PasswordHash = "h"
        });
        db.WishlistItems.AddRange(older, newer, done, Item(_otherTenant, "foreign item"));
        await db.SaveChangesAsync();

        var all = Assert.IsType<OkObjectResult>(await Controller(db, _tenant).List(null, default));
        Assert.Equal(3, Assert.IsAssignableFrom<IEnumerable<object>>(all.Value).Count());

        var doneOnly = Assert.IsType<OkObjectResult>(await Controller(db, _tenant).List("done", default));
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(doneOnly.Value));

        var caseInsensitive = Assert.IsType<OkObjectResult>(await Controller(db, _tenant).List("OPEN", default));
        Assert.Equal(2, Assert.IsAssignableFrom<IEnumerable<object>>(caseInsensitive.Value).Count());

        var serialized = JsonSerializer.Serialize(all.Value);
        Assert.True(serialized.IndexOf("newer open", StringComparison.Ordinal) <
                    serialized.IndexOf("older open", StringComparison.Ordinal));
        Assert.Contains("Dr. Author", serialized);
        Assert.DoesNotContain("foreign item", serialized);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("99")]
    public async Task List_Rejects_An_Unknown_Status_Instead_Of_Returning_Everything(string status)
    {
        await using var db = Database(_tenant);
        db.WishlistItems.Add(Item(_tenant, "open item"));
        await db.SaveChangesAsync();

        var response = await Controller(db, _tenant).List(status, default);

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains("invalid_status", Error(bad.Value));
    }

    [Fact]
    public async Task Update_Changes_Fields_And_Trims()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant, "old");
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        var response = await Controller(db, _tenant).Update(item.Id,
            new UpdateWishlistItemRequest("  new text  ", "goal", "done"), default);

        Assert.IsType<OkObjectResult>(response);
        var updated = await db.WishlistItems.SingleAsync();
        Assert.Equal("new text", updated.Text);
        Assert.Equal(WishlistCategory.Goal, updated.Category);
        Assert.Equal(WishlistStatus.Done, updated.Status);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Theory]
    [InlineData("", null, null, "text_required")]
    [InlineData("   ", null, null, "text_required")]
    [InlineData(null, "nonsense", null, "invalid_category")]
    [InlineData(null, "99", null, "invalid_category")]
    [InlineData(null, null, "nonsense", "invalid_status")]
    [InlineData(null, null, "99", "invalid_status")]
    public async Task Update_Rejects_Invalid_Values_Without_Writing(
        string? text, string? category, string? status, string error)
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant, "original");
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        var response = await Controller(db, _tenant)
            .Update(item.Id, new UpdateWishlistItemRequest(text, category, status), default);

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains(error, Error(bad.Value));
        var unchanged = await db.WishlistItems.SingleAsync();
        Assert.Equal("original", unchanged.Text);
        Assert.Equal(WishlistCategory.Task, unchanged.Category);
        Assert.Equal(WishlistStatus.Open, unchanged.Status);
    }

    [Fact]
    public async Task Update_Rejects_Text_Over_The_Database_Maximum()
    {
        await using var db = Database(_tenant);
        var item = Item(_tenant, "original");
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        var response = await Controller(db, _tenant).Update(item.Id,
            new UpdateWishlistItemRequest(new string('a', 1001), null, null), default);

        var bad = Assert.IsType<BadRequestObjectResult>(response);
        Assert.Contains("text_too_long", Error(bad.Value));
        Assert.Equal("original", (await db.WishlistItems.SingleAsync()).Text);
    }

    [Fact]
    public async Task Update_Cannot_Reach_Another_Tenants_Item()
    {
        await using var db = Database(_tenant);
        var foreign = Item(_otherTenant, "foreign");
        db.WishlistItems.Add(foreign);
        await db.SaveChangesAsync();

        var response = await Controller(db, _tenant).Update(foreign.Id,
            new UpdateWishlistItemRequest("hijacked", null, null), default);

        Assert.IsType<NotFoundObjectResult>(response);
        var untouched = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("foreign", untouched.Text);
        Assert.Equal(_otherTenant, untouched.TenantId);
    }

    [Theory]
    [InlineData(nameof(WishlistItemsController.List))]
    [InlineData(nameof(WishlistItemsController.Create))]
    [InlineData(nameof(WishlistItemsController.Update))]
    public void Actions_Are_Doctor_Or_ClinicAdmin_Only(string action)
    {
        var roles = typeof(WishlistItemsController)
            .GetMethod(action)!
            .GetCustomAttributes(typeof(AuthorizeRolesAttribute), inherit: false)
            .Cast<AuthorizeRolesAttribute>()
            .SelectMany(a => (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        Assert.Contains("Doctor", roles);
        Assert.Contains("ClinicAdmin", roles);
        Assert.Contains("Owner", roles); // AuthorizeRoles expands ClinicAdmin to Owner
        Assert.DoesNotContain("Receptionist", roles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nurse", roles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Controller_Is_Gated_By_The_Wishlist_Module()
    {
        Assert.Equal(ModuleRegistry.Wishlist,
            typeof(WishlistItemsController).GetCustomAttribute<RequireModuleAttribute>()?.Module);
        Assert.Equal("api/v1/wishlist-items",
            typeof(WishlistItemsController).GetCustomAttribute<RouteAttribute>()?.Template);
    }
}
