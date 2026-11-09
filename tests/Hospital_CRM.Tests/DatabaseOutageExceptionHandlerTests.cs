using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Hospital_CRM.Api.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Hospital_CRM.Tests;

public class DatabaseOutageExceptionHandlerTests
{
    private readonly DatabaseOutageExceptionHandler _handler = new(NullLogger<DatabaseOutageExceptionHandler>.Instance);

    [Fact]
    public async Task TryHandleAsync_ReturnsTrue_AndWrites503ProblemDetails_WhenNpgsqlSocketExceptionOccurs()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/patients";
        context.Response.Body = new MemoryStream();

        var inner = new SocketException((int)SocketError.ConnectionRefused);
        var ex = new NpgsqlException("PostgreSQL socket connection refused", inner);
        var handled = await _handler.TryHandleAsync(context, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Equal("5", context.Response.Headers.RetryAfter);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        Assert.Equal("https://tools.ietf.org/html/rfc7807", root.GetProperty("type").GetString());
        Assert.Equal("Service Unavailable", root.GetProperty("title").GetString());
        Assert.Equal(503, root.GetProperty("status").GetInt32());
        Assert.Equal("database_unavailable", root.GetProperty("error").GetString());
        Assert.Equal("The database service is temporarily unavailable. Please retry shortly.", root.GetProperty("detail").GetString());
        Assert.Equal("/api/v1/patients", root.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsTrue_WhenNpgsqlTimeoutExceptionOccurs()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/appointments";
        context.Response.Body = new MemoryStream();

        var inner = new TimeoutException("Database connection timed out");
        var ex = new NpgsqlException("PostgreSQL query execution timed out", inner);
        var handled = await _handler.TryHandleAsync(context, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsFalse_WhenGenericSocketExceptionOccurs_WithoutDatabaseProvenance()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/payments/razorpay/sync";
        context.Response.Body = new MemoryStream();

        // Socket exception without Npgsql database provenance (e.g. from Razorpay or external webhook)
        var ex = new SocketException((int)SocketError.ConnectionRefused);
        var handled = await _handler.TryHandleAsync(context, ex, CancellationToken.None);

        // Non-database socket exception must not be classified as 503 database_unavailable
        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsFalse_WhenHttpRequestExceptionOccurs_WithTimeoutInner()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/notifications/whatsapp";
        context.Response.Body = new MemoryStream();

        // HTTP timeout to an external messaging service (e.g. WhatsApp BSP)
        var ex = new HttpRequestException("External API timed out", new TimeoutException("Request timed out"));
        var handled = await _handler.TryHandleAsync(context, ex, CancellationToken.None);

        // Must fall through to generic 500 handler, not database 503
        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsFalse_WhenApplicationExceptionOccurs()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/reports/financial";
        context.Response.Body = new MemoryStream();

        var ex = new ObjectDisposedException("HospitalCrmDbContext");
        var handled = await _handler.TryHandleAsync(context, ex, CancellationToken.None);

        // Disposed context / application defect must fall through to the default 500 handler
        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsFalse_WhenNullReferenceExceptionOccurs()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/invoices";
        context.Response.Body = new MemoryStream();

        var ex = new NullReferenceException("Unexpected null");
        var handled = await _handler.TryHandleAsync(context, ex, CancellationToken.None);

        Assert.False(handled);
    }
}
