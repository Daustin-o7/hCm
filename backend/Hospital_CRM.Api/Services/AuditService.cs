using System.Text.Json;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public static class AuditService
{
    public static async Task LogChangeAsync(
        HospitalCrmDbContext context,
        string entityType,
        Guid entityId,
        Guid userId,
        string fieldName,
        string? oldValue,
        string? newValue,
        CancellationToken ct = default)
    {
        if (string.Equals(entityType, "Patient", StringComparison.OrdinalIgnoreCase))
        {
            context.PatientAuditLogs.Add(new PatientAuditLog
            {
                Id = Guid.NewGuid(),
                PatientId = entityId,
                ChangedBy = userId,
                FieldName = fieldName,
                OldValue = oldValue ?? string.Empty,
                NewValue = newValue ?? string.Empty,
                ChangedAt = DateTimeOffset.UtcNow
            });
        }

        await context.SaveChangesAsync(ct);
    }

    public static async Task LogEntityAuditAsync<TEntity>(
        HospitalCrmDbContext context,
        TEntity entity,
        Guid userId,
        string action,
        CancellationToken ct = default) where TEntity : class
    {
        var entityName = typeof(TEntity).Name;
        var entry = context.Entry(entity);
        var primaryKey = entry.Property("Id").CurrentValue?.ToString() ?? Guid.NewGuid().ToString();

        if (Guid.TryParse(primaryKey, out var entityGuid) && entityName == "Patient")
        {
            foreach (var prop in entry.Properties)
            {
                if (prop.IsModified)
                {
                    context.PatientAuditLogs.Add(new PatientAuditLog
                    {
                        Id = Guid.NewGuid(),
                        PatientId = entityGuid,
                        ChangedBy = userId,
                        FieldName = JsonNamingPolicy.CamelCase.ConvertName(prop.Metadata.Name),
                        OldValue = prop.OriginalValue is DateOnly oldDate ? oldDate.ToString("yyyy-MM-dd") : prop.OriginalValue?.ToString() ?? string.Empty,
                        NewValue = prop.CurrentValue is DateOnly newDate ? newDate.ToString("yyyy-MM-dd") : prop.CurrentValue?.ToString() ?? string.Empty,
                        ChangedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        await context.SaveChangesAsync(ct);
    }
}
