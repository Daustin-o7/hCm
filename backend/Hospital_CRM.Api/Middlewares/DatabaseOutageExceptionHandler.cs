using System.Net.Sockets;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Hospital_CRM.Api.Middlewares;

/// <summary>
/// G02 Outage Contract: Intercepts transient PostgreSQL connection drops and network outages,
/// returning an RFC 7807 503 Service Unavailable problem details response without leaking internal details.
/// Requires explicit database provenance (Npgsql / EntityFrameworkCore) to prevent non-database network
/// socket or timeout exceptions (e.g. external payment gateways or SMS APIs) from being misclassified.
/// </summary>
public sealed class DatabaseOutageExceptionHandler(ILogger<DatabaseOutageExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsTransientDatabaseOutage(exception))
        {
            return false; // Let standard 500 handler process non-outage exceptions
        }

        logger.LogWarning(exception, "Transient database connectivity outage on {Path}", httpContext.Request.Path);

        var problemDetails = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc7807",
            Title = "Service Unavailable",
            Status = StatusCodes.Status503ServiceUnavailable,
            Detail = "The database service is temporarily unavailable. Please retry shortly.",
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["error"] = "database_unavailable";

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";

        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }

    public static bool IsTransientDatabaseOutage(Exception? ex)
    {
        if (ex == null) return false;

        // Must require database provenance in the exception hierarchy
        if (!HasDatabaseProvenance(ex))
        {
            return false;
        }

        var curr = ex;
        while (curr != null)
        {
            if (curr is NpgsqlException nex)
            {
                if (nex.IsTransient) return true;
                if (nex.InnerException is SocketException or TimeoutException) return true;
                if (nex.SqlState is "08000" or "08003" or "08006" or "08001" or "08004" or "57P01" or "57P02" or "57P03") return true;
            }

            if (curr is RetryLimitExceededException)
            {
                return true;
            }

            if (curr is SocketException or TimeoutException)
            {
                return true;
            }

            curr = curr.InnerException;
        }

        return false;
    }

    private static bool HasDatabaseProvenance(Exception ex)
    {
        var curr = (Exception?)ex;
        while (curr != null)
        {
            if (curr is NpgsqlException) return true;
            if (curr is RetryLimitExceededException) return true;
            if (curr is DbUpdateException) return true;
            if (curr.Source != null && (curr.Source.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                                        curr.Source.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))) return true;
            curr = curr.InnerException;
        }
        return false;
    }
}
