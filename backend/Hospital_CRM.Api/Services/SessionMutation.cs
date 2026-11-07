using Hospital_CRM.Infrastructure.Data;
using Hospital_CRM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public static class SessionMutation
{
    // Lock the durable user, not the current token: rotation can insert a new family member.
    public static async Task<T> RunAsync<T>(HospitalCrmDbContext db, Guid userId,
        Func<Task<T>> action, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await action();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, userId, ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result;
    }

    public static async Task LockAsync(HospitalCrmDbContext db, Guid userId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE", ct);
        // Authentication middleware may already have tracked a pre-lock credential snapshot.
        var tracked = db.ChangeTracker.Entries<User>().SingleOrDefault(e => e.Entity.Id == userId);
        if (tracked is not null) await tracked.ReloadAsync(ct);
    }
}
