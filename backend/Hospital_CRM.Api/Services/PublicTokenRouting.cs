using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public enum PublicTokenPurpose { Queue, Precheck, Prescription }

[AttributeUsage(AttributeTargets.Method)]
public sealed class PublicTokenEndpointAttribute(PublicTokenPurpose purpose, string routeParameter = "token") : Attribute
{
    public PublicTokenPurpose Purpose { get; } = purpose;
    public string RouteParameter { get; } = routeParameter;
}

public sealed record PublicTokenBinding(Guid TenantId, Guid ResourceId);

/// <summary>Routes an exact bearer token without exposing PHI or granting platform authority.</summary>
public static class PublicTokenRouting
{
    /// <summary>
    /// The caller must PushTenant synchronously after awaiting this lookup, then recheck
    /// the exact resource/token before accessing data. Requires public.resolve_public_token(text,text).
    /// It returns at most one binding; prescription amendments share a code and must route to
    /// one version of the same visit, rejecting collisions across tenants or visits.
    /// </summary>
    public static async Task<PublicTokenBinding?> ResolveAsync(HospitalCrmDbContext db,
        PublicTokenPurpose purpose, string token, CancellationToken ct = default)
    {
        if (!IsWellFormed(purpose, token))
            return null;

        var kind = purpose switch
        {
            PublicTokenPurpose.Queue => "queue",
            PublicTokenPurpose.Precheck => "precheck",
            PublicTokenPurpose.Prescription => "prescription",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
        var key = purpose == PublicTokenPurpose.Precheck ? token.HashToken() : token;
        var binding = await db.Database.SqlQuery<PublicTokenBinding>(
            $"SELECT \"TenantId\", \"ResourceId\" FROM public.resolve_public_token({kind}, {key})")
            .SingleOrDefaultAsync(ct);
        return binding is { TenantId: var tenant, ResourceId: var resource }
            && tenant != Guid.Empty && resource != Guid.Empty ? binding : null;
    }

    public static bool IsWellFormed(PublicTokenPurpose purpose, string? token) => purpose switch
    {
        PublicTokenPurpose.Queue => token is { Length: 37 } && token.StartsWith("qtrk_", StringComparison.Ordinal)
            && token.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") < 0,
        PublicTokenPurpose.Precheck => token is { Length: 43 }
            && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
        PublicTokenPurpose.Prescription => token is { Length: >= 15 and <= 67 }
            && token.StartsWith("RX-", StringComparison.Ordinal)
            && token.AsSpan(3).IndexOfAnyExcept("0123456789ABCDEF") < 0,
        _ => false
    };
}
