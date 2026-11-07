using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public interface IEmailSender
{
    /// <summary>True only when SMTP accepted the message; this is not mailbox-delivery proof.</summary>
    Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Plain SMTP from the BCL, so the free email-code second factor needs no package.
/// Missing SMTP configuration fails closed in every environment; message contents are never logged.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration config, IWebHostEnvironment env, ILogger<SmtpEmailSender> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var host = _config["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogError("Email handoff failed: SMTP host is not configured");
            return false;
        }

        try
        {
            using var message = new MailMessage(
                _config["Smtp:From"] ?? "no-reply@samstack.ai",
                to,
                subject,
                body);

            using var client = new SmtpClient(host, _config.GetValue("Smtp:Port", 587))
            {
                EnableSsl = _config.GetValue("Smtp:EnableSsl", true),
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            var username = _config["Smtp:Username"];
            if (!string.IsNullOrWhiteSpace(username))
                client.Credentials = new NetworkCredential(username, _config["Smtp:Password"]);

            await client.SendMailAsync(message, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // SMTP exception messages may include recipients or provider payloads.
            _logger.LogError("Email handoff failed: {FailureType}", ex.GetType().Name);
            return false;
        }
    }
}

internal static class IdentityEmail
{
    public static string? Link(IConfiguration? config, string path, string token)
    {
        if (!Uri.TryCreate(config?["App:PublicBaseUrl"], UriKind.Absolute, out var root)
            || root.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(root.UserInfo)
            || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            return null;
        return $"{root.AbsoluteUri.TrimEnd('/')}/{path}?token={Uri.EscapeDataString(token)}";
    }

    public static async Task<string> InviteAsync(IEmailSender? sender, IConfiguration? config,
        string email, string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var link = Link(config, "accept-invite", token);
        if (sender is null || link is null) return "not_configured";
        try
        {
            return await sender.SendAsync(email, "Your clinic invitation",
                $"Accept your clinic invitation: {link}\nExpires at {expiresAt:O} (72 hours from issuance).", ct)
                ? "handoff" : "failed";
        }
        catch (Exception) { return "unknown"; }
    }
}

/// <summary>Durable, protected Hangfire handoff keeps SMTP and account lookup out of the public reset request.</summary>
public sealed class PasswordResetEmailJob(
    HospitalCrmDbContext db, IEmailSender email, IConfiguration config,
    IDataProtectionProvider protection, ILogger<PasswordResetEmailJob> logger)
{
    private const string Purpose = "HospitalCrm.PasswordResetEmail.v1";
    private sealed record Payload(string Email, string Token, DateTimeOffset ExpiresAt);

    public static string ProtectRequest(IDataProtectionProvider protection, string email) =>
        protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(new Payload(
            email, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            DateTimeOffset.UtcNow.AddMinutes(30))));

    // Ambiguous SMTP acceptance must be reconciled by a new request, never an automatic resend.
    [AutomaticRetry(Attempts = 0)]
    public async Task SendAsync(Guid requestId, string protectedRequest, CancellationToken ct)
    {
        var failure = "payload_invalid";
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protection.CreateProtector(Purpose)
                .Unprotect(protectedRequest)) ?? throw new CryptographicException();
            if (payload.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                logger.LogInformation("Reset email {RequestId} expired before processing", requestId);
                return;
            }
            failure = "lookup_failed";
            var user = await db.Users.SingleOrDefaultAsync(u => u.Email == payload.Email, ct);
            if (user is null) return;
            failure = "url_not_configured";
            var link = IdentityEmail.Link(config, "reset-password", payload.Token);
            if (link is null) throw new InvalidOperationException();

            failure = "already_attempted";
            if (await db.PasswordResetTokens.AnyAsync(t => t.Id == requestId, ct))
                throw new InvalidOperationException();

            failure = "persistence_failed";
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = requestId,
                UserId = user.Id,
                TokenHash = payload.Token.HashToken(),
                ExpiresAt = payload.ExpiresAt
            });
            await db.SaveChangesAsync(ct);

            failure = "handoff_unknown";
            var accepted = await email.SendAsync(user.Email, "Reset your password",
                $"Reset your password: {link}\nExpires at {payload.ExpiresAt:O} (30 minutes from request).", ct);
            failure = "handoff_failed";
            if (!accepted) throw new InvalidOperationException();
            logger.LogInformation("Reset email {RequestId} transport outcome: handoff", requestId);
        }
        catch (Exception ex)
        {
            // Hangfire persists exceptions, so never attach provider/database messages or inner exceptions.
            logger.LogWarning("Reset email {RequestId} outcome: {Outcome} ({FailureType}); request a fresh link",
                requestId, failure, ex.GetType().Name);
            throw new InvalidOperationException($"password_reset_email_{failure}");
        }
    }
}
