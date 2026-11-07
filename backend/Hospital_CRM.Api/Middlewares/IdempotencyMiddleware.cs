using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Hospital_CRM.Api.Middlewares;

public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    private const string PendingSentinel = "__PENDING__";

    public IdempotencyMiddleware(RequestDelegate next, IMemoryCache cache, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only enforce for mutating requests
        if (!HttpMethods.IsPost(context.Request.Method) &&
            !HttpMethods.IsPut(context.Request.Method) &&
            !HttpMethods.IsPatch(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Check for idempotency key headers (supports both X-Idempotency-Key and Idempotency-Key)
        string? key = null;
        if (context.Request.Headers.TryGetValue("X-Idempotency-Key", out var xKey) && !string.IsNullOrWhiteSpace(xKey))
        {
            key = xKey.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue("Idempotency-Key", out var stdKey) && !string.IsNullOrWhiteSpace(stdKey))
        {
            key = stdKey.ToString().Trim();
        }

        // If no idempotency key was supplied by client, pass through normally
        if (string.IsNullOrEmpty(key))
        {
            await _next(context);
            return;
        }

        // Validate key format and reasonable length
        if (key.Length > 128)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "invalid_idempotency_key",
                message = "Idempotency key length must be 128 characters or fewer."
            }));
            return;
        }

        // Durable writes validate the original payload and return current saved state themselves.
        var path = context.Request.Path.Value?.TrimEnd('/');
        if (string.Equals(path, "/api/v1/pharmacy/checkout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/api/v1/pharmacy/dispense", StringComparison.OrdinalIgnoreCase)
            || HttpMethods.IsPost(context.Request.Method) && (
                string.Equals(path, "/api/v1/patients", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/v1/platform-admin/tenants", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(path ?? "", @"^/api/v1/invoices/[^/]+/(?:payment|payments/(?:report|confirm))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            await _next(context);
            return;
        }

        // Scope by tenant, method and path: the same client key on a different endpoint - or the
        // same key from a different tenant - must never replay another request's response.
        var scope = context.User.FindFirst("tenant_id")?.Value
                 ?? context.User.FindFirst("tenantId")?.Value
                 ?? "anon";
        var cacheKey = $"idemp:{context.Request.Method}:{context.Request.Path}:{scope}:{key}";

        // Check cache for existing record
        if (_cache.TryGetValue(cacheKey, out object? cachedObj))
        {
            if (cachedObj is string s && s == PendingSentinel)
            {
                _logger.LogWarning("Concurrent request with in-flight idempotency key: {Key}", key);
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = "request_in_progress",
                    message = "A request with this idempotency key is currently being processed."
                }));
                return;
            }

            if (cachedObj is CachedIdempotentResponse cachedResponse)
            {
                _logger.LogInformation("Replaying cached response for idempotency key: {Key}", key);
                context.Response.StatusCode = cachedResponse.StatusCode;
                context.Response.ContentType = cachedResponse.ContentType;
                context.Response.Headers["X-Cache-Lookup"] = "HIT";
                context.Response.Headers["X-Idempotency-Replay"] = "true";
                await context.Response.Body.WriteAsync(cachedResponse.Body);
                return;
            }
        }

        // Mark in-flight with short expiration to unlock if process aborts
        _cache.Set(cacheKey, PendingSentinel, TimeSpan.FromSeconds(60));

        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        try
        {
            await _next(context);

            memoryStream.Seek(0, SeekOrigin.Begin);
            var responseBytes = memoryStream.ToArray();

            // Only successful responses are replayed. Caching 4xx would freeze a transient
            // failure (expired token, suspended tenant) for the life of the key.
            if (context.Response.StatusCode < StatusCodes.Status300MultipleChoices)
            {
                var cached = new CachedIdempotentResponse
                {
                    StatusCode = context.Response.StatusCode,
                    ContentType = context.Response.ContentType ?? "application/json",
                    Body = responseBytes
                };
                _cache.Set(cacheKey, cached, TimeSpan.FromHours(24));
            }
            else
            {
                _cache.Remove(cacheKey);
            }

            context.Response.Headers["X-Cache-Lookup"] = "MISS";
            context.Response.Body = originalBodyStream;
            await context.Response.Body.WriteAsync(responseBytes);
        }
        catch
        {
            _cache.Remove(cacheKey);
            context.Response.Body = originalBodyStream;
            throw;
        }
    }

    private sealed class CachedIdempotentResponse
    {
        public int StatusCode { get; init; }
        public string ContentType { get; init; } = "application/json";
        public byte[] Body { get; init; } = Array.Empty<byte>();
    }
}
