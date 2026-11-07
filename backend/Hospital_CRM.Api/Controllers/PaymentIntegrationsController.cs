using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

/// <summary>
/// B2 / D7: the clinic's own PSP (Paytm Business, PhonePe Business) notifies SAMSTACK
/// directly. SAMSTACK is a software ledger and never holds patient funds.
///
/// Tenant routing for the anonymous webhook is the unguessable path token on the
/// integration row, then the signature is verified with that tenant's secret. A
/// webhook signed with tenant B's secret therefore never validates for tenant A.
/// </summary>
[ApiController]
[Route("api/v1")]
public class PaymentIntegrationsController : ControllerBase
{
    private static readonly string[] PaymentProviders = ["paytm", "phonepe", "razorpay"];
    private static readonly string[] AllProviders = ["paytm", "phonepe", "razorpay", "whatsapp"];

    private readonly HospitalCrmDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly IConfiguration _config;
    private readonly ILogger<PaymentIntegrationsController> _logger;
    private readonly ITenantContext? _tenantContext;

    public PaymentIntegrationsController(
        HospitalCrmDbContext db,
        ISecretProtector secretProtector,
        IConfiguration config,
        ILogger<PaymentIntegrationsController> logger,
        ITenantContext? tenantContext = null)
    {
        _db = db;
        _secretProtector = secretProtector;
        _config = config;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    // ----- Clinic Admin connect screen -----

    [HttpGet("integrations/payments")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        var rows = await _db.TenantIntegrations
            .Where(i => i.TenantId == tenantId.Value)
            .OrderBy(i => i.Provider)
            .ToListAsync(ct);

        var baseUrl = PublicBaseUrl();
        return Ok(rows.Select(i => new
        {
            provider = i.Provider,
            merchantId = i.MerchantId,
            enabled = i.Enabled,
            hasSecret = !string.IsNullOrEmpty(i.SecretProtected),
            webhookUrl = PaymentProviders.Contains(i.Provider) && !string.IsNullOrEmpty(i.WebhookPathToken)
                ? $"{baseUrl}/api/v1/webhooks/payments/{i.Provider}/{i.WebhookPathToken}"
                : null,
            lastVerifiedAt = i.LastVerifiedAt
        }));
    }

    [HttpPut("integrations/payments/{provider}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Upsert(string provider, [FromBody] UpsertIntegrationRequest request, CancellationToken ct)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllProviders.Contains(provider))
            return BadRequest(new { error = "unknown_provider" });

        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Secret) && string.IsNullOrWhiteSpace(request.MerchantId))
            return BadRequest(new { error = "merchant_id_or_secret_required" });

        var integration = await _db.TenantIntegrations
            .FirstOrDefaultAsync(i => i.TenantId == tenantId.Value && i.Provider == provider, ct);

        if (integration is null)
        {
            integration = new TenantIntegration
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId.Value,
                Provider = provider,
                WebhookPathToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
            };
            _db.TenantIntegrations.Add(integration);
        }

        if (!string.IsNullOrWhiteSpace(request.MerchantId))
            integration.MerchantId = request.MerchantId.Trim();

        // Write-only: a blank secret means "keep the existing one".
        if (!string.IsNullOrWhiteSpace(request.Secret))
            integration.SecretProtected = _secretProtector.Protect(request.Secret);

        if (request.Enabled.HasValue)
            integration.Enabled = request.Enabled.Value;

        integration.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Integration {Provider} configured for tenant {TenantId}", provider, tenantId.Value);

        return Ok(new
        {
            provider = integration.Provider,
            merchantId = integration.MerchantId,
            enabled = integration.Enabled,
            hasSecret = !string.IsNullOrEmpty(integration.SecretProtected),
            webhookUrl = PaymentProviders.Contains(provider)
                ? $"{PublicBaseUrl()}/api/v1/webhooks/payments/{provider}/{integration.WebhookPathToken}"
                : null
        });
    }

    [HttpDelete("integrations/payments/{provider}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Remove(string provider, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var integration = await _db.TenantIntegrations
            .FirstOrDefaultAsync(i => i.TenantId == tenantId.Value && i.Provider == provider, ct);
        if (integration is null) return NotFound(new { error = "integration_not_found" });

        _db.TenantIntegrations.Remove(integration);
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "integration_removed" });
    }

    /// <summary>
    /// Configuration self-test. Without a merchant API credential there is nothing to
    /// call at the PSP, so this proves what can be proven locally: the stored secret
    /// decrypts, and a probe payload signed with it passes the same verifier the
    /// webhook uses. A true round-trip still needs the merchant account.
    /// </summary>
    [HttpPost("integrations/payments/{provider}/verify")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> Verify(string provider, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var integration = await _db.TenantIntegrations
            .FirstOrDefaultAsync(i => i.TenantId == tenantId.Value && i.Provider == provider, ct);
        if (integration is null) return NotFound(new { error = "integration_not_found" });

        var secret = _secretProtector.Unprotect(integration.SecretProtected);
        if (string.IsNullOrEmpty(secret))
            return UnprocessableEntity(new { error = "secret_missing_or_unreadable" });

        var probe = "{\"probe\":true}";
        var signature = PaymentWebhookCodec.SignHmac(secret, probe);
        if (!PaymentWebhookCodec.Verify(provider, secret, probe, signature))
            return UnprocessableEntity(new { error = "signature_verification_failed" });

        integration.LastVerifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { verified = true, lastVerifiedAt = integration.LastVerifiedAt });
    }

    // ----- Unmatched payments (never dropped) -----

    [HttpGet("integrations/payments/unmatched")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListUnmatched(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        var rows = await _db.ProcessedWebhookEvents
            .Where(e => e.TenantId == tenantId.Value && e.Status == ProcessedWebhookEvent.Unmatched)
            .OrderByDescending(e => e.ReceivedAt)
            .Take(100)
            .Select(e => new
            {
                id = e.Id,
                provider = e.Provider,
                amount = e.Amount,
                utr = e.Utr,
                reference = e.Reference,
                receivedAt = e.ReceivedAt
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("integrations/payments/unmatched/{id:guid}/match")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> MatchUnmatched(Guid id, [FromBody] MatchUnmatchedRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var evt = await _db.ProcessedWebhookEvents
                .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId.Value, ct);
            if (evt is null) return NotFound(new { error = "unmatched_payment_not_found" });
            if (evt.Status != ProcessedWebhookEvent.Unmatched)
            {
                var original = await _db.Payments.FirstOrDefaultAsync(p => p.Id == evt.PaymentId && p.TenantId == tenantId.Value, ct);
                return original != null && original.InvoiceId == request.InvoiceId
                    ? Ok(new { status = "matched", paymentId = original.Id, alreadyMatched = true })
                    : Conflict(new { error = "payment_already_matched" });
            }

            var invoice = await _db.Invoices
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId && i.TenantId == tenantId.Value, ct);
            if (invoice is null) return NotFound(new { error = "invoice_not_found" });
            if (InvoiceAccounting.InvoiceReferences(evt.Reference ?? "").Count > 0 && !InvoiceAccounting.ReferenceAgrees(invoice, evt.Reference))
                return BadRequest(new { error = "invoice_reference_conflict", message = "The receipt names a different or ambiguous invoice; retain it for reconciliation." });

            Payment? existing = null;
            PaymentEvent? capture = null;
            if (evt.Provider == "razorpay")
            {
                capture = PaymentWebhookCodec.TryParse("razorpay", evt.RawPayload ?? "");
                if (capture is null || capture.Amount != evt.Amount || capture.EventId != evt.EventId)
                    return BadRequest(new { error = "capture_details_required", message = "A verified captured payment ID and amount are required before matching a Razorpay receipt." });
                var matches = await InvoiceAccounting.FindRazorpayPaymentsAsync(_db, tenantId.Value, capture, ct);
                if (matches.Count > 1 || matches.Count == 1 && (matches[0].InvoiceId != invoice.Id || !InvoiceAccounting.CaptureAgrees(matches[0], capture)))
                    return BadRequest(new { error = "capture_order_conflict" });
                existing = matches.SingleOrDefault();
                if (existing != null && (existing.Status != PaymentStatus.Pending || existing.VerificationStatus != PaymentVerificationStatus.Pending)
                    && (existing.Status != PaymentStatus.Completed || existing.VerificationStatus != PaymentVerificationStatus.Confirmed))
                    return BadRequest(new { error = "capture_not_confirmable" });
            }
            var alreadyConfirmed = existing is { Status: PaymentStatus.Completed, VerificationStatus: PaymentVerificationStatus.Confirmed };
            if (!alreadyConfirmed && !InvoiceAccounting.CanCollect(invoice, evt.Amount))
                return BadRequest(new { error = "invoice_not_payable", message = "Select an issued invoice with sufficient outstanding balance for this receipt." });

            var payment = existing ?? CloseInvoice(invoice, tenantId.Value, evt.Amount, evt.Utr, capture?.ProviderPaymentId);
            if (existing != null && !alreadyConfirmed)
            {
                payment.RazorpayPaymentId = capture!.ProviderPaymentId;
                InvoiceAccounting.ConfirmPayment(_db, invoice, payment, User.GetUserId());
            }
            if (capture != null) payment.RazorpayOrderId = capture.ProviderOrderId;
            evt.Status = ProcessedWebhookEvent.Processed;
            evt.PaymentId = payment.Id;

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return Ok(new { status = "matched", paymentId = payment.Id, alreadyMatched = alreadyConfirmed });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "payment_changed_concurrently", message = "Reload the unmatched queue and retry the same event." });
        }
    }

    // ----- B2.9 Android notification bridge (fallback) -----

    /// <summary>
    /// Creates an unpaired device row and returns a one-time six-digit code. The clinic
    /// admin reads the code aloud (or types it on the handset); it expires in ten
    /// minutes and is useless without an account in the same tenant.
    /// </summary>
    [HttpPost("integrations/payments/bridge/devices/pairing-code")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> CreatePairingCode([FromBody] CreatePairingCodeRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        var name = string.IsNullOrWhiteSpace(request.Name) ? "Clinic device" : request.Name.Trim();
        if (name.Length > 120) name = name[..120];

        var device = new PaymentBridgeDevice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId.Value,
            Name = name,
            PairingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            CreatedByUserId = User.GetUserId()
        };
        var code = DeviceSignature.NewPairingCode();
        device.PairingCodeHash = DeviceSignature.HashPairingCode(device.Id.ToString(), code);

        _db.PaymentBridgeDevices.Add(device);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            deviceId = device.Id,
            pairingCode = code,
            expiresAt = device.PairingExpiresAt
        });
    }

    /// <summary>
    /// Exchanges a pairing code for the device secret. The secret is returned exactly
    /// once and stored on the handset; the server keeps only a protected copy.
    /// </summary>
    [HttpPost("integrations/payments/bridge/devices/pair")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    public async Task<IActionResult> PairDevice([FromBody] PairDeviceRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();
        if (string.IsNullOrWhiteSpace(request.PairingCode))
            return BadRequest(new { error = "pairing_code_required" });

        var now = DateTimeOffset.UtcNow;
        var code = request.PairingCode.Trim();

        var pending = await _db.PaymentBridgeDevices
            .Where(d => d.TenantId == tenantId.Value
                        && d.PairedAt == null
                        && d.RevokedAt == null
                        && d.PairingExpiresAt > now
                        && d.PairingAttempts < 5)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return BadRequest(new { error = "no_pending_pairing" });

        var device = pending.FirstOrDefault(d =>
            d.PairingCodeHash is not null &&
            DeviceSignature.HashPairingCode(d.Id.ToString(), code) == d.PairingCodeHash);

        if (device is null)
        {
            // Bound the guesses on every outstanding code, not just the one aimed at.
            foreach (var candidate in pending) candidate.PairingAttempts++;
            await _db.SaveChangesAsync(ct);
            return BadRequest(new { error = "invalid_pairing_code" });
        }

        var secret = DeviceSignature.NewSecret();
        device.SecretProtected = _secretProtector.Protect(secret);
        device.PairedAt = now;
        device.PairingCodeHash = null;
        device.PairingExpiresAt = null;
        device.PairingAttempts = 0;
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = request.Name.Trim();
            device.Name = name.Length > 120 ? name[..120] : name;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Payment bridge device {DeviceId} paired for tenant {TenantId}", device.Id, tenantId);

        return Ok(new { deviceId = device.Id, device.Name, secret });
    }

    [HttpGet("integrations/payments/bridge/devices")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> ListDevices(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        var devices = await _db.PaymentBridgeDevices
            .Where(d => d.TenantId == tenantId.Value)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                paired = d.PairedAt != null,
                pairedAt = d.PairedAt,
                lastSeenAt = d.LastSeenAt,
                revoked = d.RevokedAt != null
            })
            .ToListAsync(ct);

        return Ok(new { devices });
    }

    [HttpPost("integrations/payments/bridge/devices/{id:guid}/revoke")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> RevokeDevice(Guid id, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        var device = await _db.PaymentBridgeDevices
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId.Value, ct);
        if (device is null) return NotFound(new { error = "device_not_found" });

        device.RevokedAt = DateTimeOffset.UtcNow;
        device.SecretProtected = null;
        device.PairingCodeHash = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Payment bridge device {DeviceId} revoked for tenant {TenantId}", id, tenantId);
        return Ok(new { status = "revoked" });
    }

    /// <summary>
    /// Fallback for clinics whose UPI ID exposes no PSP webhook: the receptionist's
    /// Android device reads the bank SMS / soundbox notification and posts it here.
    /// The tenant is taken from the session, never from the payload.
    ///
    /// A paired handset also signs the request body with its own secret. Once a tenant
    /// has a paired device the signature is mandatory, so a clinic login on its own can
    /// no longer manufacture a receipt. Tenants with no device yet still post with the
    /// JWT alone, which is what the rollout depends on.
    /// </summary>
    [HttpPost("integrations/payments/bridge/notifications")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    public async Task<IActionResult> IngestDeviceNotification(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        if (tenantId is null) return Forbid();

        // A bank alert is a few hundred bytes; anything larger is not a receipt.
        const int maxBodyBytes = 8 * 1024;
        if (Request.ContentLength > maxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "payload_too_large" });

        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(ct);

        if (Encoding.UTF8.GetByteCount(body) > maxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "payload_too_large" });

        var device = await VerifyDeviceAsync(tenantId.Value, body, ct);
        if (device.Error is not null) return device.Error;

        DevicePaymentNotification? request;
        try
        {
            request = JsonSerializer.Deserialize<DevicePaymentNotification>(
                body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "invalid_payload" });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 255
            || request.Amount is null or <= 0 || decimal.Round(request.Amount.Value, 2) != request.Amount
            || request.Utr?.Length > 64 || request.Reference?.Length > 255)
            return BadRequest(new { error = "event_id_and_amount_required" });

        // Defence in depth: the device already filters, but a debit message must never
        // close an invoice, whatever the handset sent.
        if (IsDebitOnly(request.RawText ?? request.Reference))
            return Ok(new { status = "ignored" });

        var provider = ("bridge:" + (request.Source ?? "unknown").Trim().ToLowerInvariant());
        if (provider.Length > 32)
            return BadRequest(new { error = "source_too_long" });

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var already = await _db.ProcessedWebhookEvents
                .FirstOrDefaultAsync(e => e.TenantId == tenantId.Value && e.Provider == provider && e.EventId == request.EventId, ct);
            if (already != null)
                return Ok(new { status = "duplicate", outcome = already.Status, paymentId = already.PaymentId });

            var evt = new PaymentEvent(request.EventId, request.Amount, request.Utr, request.Reference);
            var invoice = await MatchInvoiceAsync(tenantId.Value, evt, ct);

            var record = new ProcessedWebhookEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId.Value,
                Provider = provider,
                EventId = request.EventId,
                Amount = request.Amount,
                Utr = request.Utr,
                Reference = request.Reference,
                RawPayload = request.RawText is { Length: > 0 } raw ? (raw.Length > 4000 ? raw[..4000] : raw) : null,
                ReceivedAt = DateTimeOffset.UtcNow
            };

            if (device.Device is not null)
            {
                device.Device.LastSeenAt = DateTimeOffset.UtcNow;
                _db.PaymentBridgeDevices.Update(device.Device);
            }

            if (invoice is null)
            {
                record.Status = ProcessedWebhookEvent.Unmatched;
                _db.ProcessedWebhookEvents.Add(record);
                await _db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                return Ok(new { status = "unmatched" });
            }

            var payment = CloseInvoice(invoice, tenantId.Value, request.Amount, request.Utr);
            record.Status = ProcessedWebhookEvent.Processed;
            record.PaymentId = payment.Id;
            _db.ProcessedWebhookEvents.Add(record);
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return Ok(new { status = "ok", paymentId = payment.Id });
        }
        catch (DbUpdateException ex) when (InvoiceAccounting.IsWebhookDuplicate(ex))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Ok(new { status = "duplicate" });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return StatusCode(503, new { error = "payment_changed_concurrently", message = "Retry with the same device event ID." });
        }
    }

    private readonly record struct DeviceCheck(PaymentBridgeDevice? Device, IActionResult? Error);

    /// <summary>
    /// Resolves the calling handset. A tenant that has any paired device must sign;
    /// before the first pairing the JWT is the only credential (rollout window).
    /// </summary>
    private async Task<DeviceCheck> VerifyDeviceAsync(Guid tenantId, string body, CancellationToken ct)
    {
        var deviceIdHeader = Request.Headers[DeviceSignature.DeviceHeader].ToString();
        var signature = Request.Headers[DeviceSignature.Header].ToString();

        if (string.IsNullOrWhiteSpace(deviceIdHeader))
        {
            var hasPairedDevice = await _db.PaymentBridgeDevices
                .AnyAsync(d => d.TenantId == tenantId && d.PairedAt != null && d.RevokedAt == null, ct);

            if (hasPairedDevice)
                return new DeviceCheck(null, Unauthorized(new { error = "device_signature_required" }));

            _logger.LogWarning("Unsigned payment bridge signal accepted for tenant {TenantId}: no paired device", tenantId);
            return new DeviceCheck(null, null);
        }

        if (!Guid.TryParse(deviceIdHeader, out var deviceId))
            return new DeviceCheck(null, Unauthorized(new { error = "invalid_device" }));

        if (!long.TryParse(Request.Headers[DeviceSignature.TimestampHeader].ToString(), out var timestampMs)
            || !DeviceSignature.IsFresh(timestampMs, DateTimeOffset.UtcNow))
            return new DeviceCheck(null, Unauthorized(new { error = "stale_device_signature" }));

        var device = await _db.PaymentBridgeDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId
                                      && d.TenantId == tenantId
                                      && d.PairedAt != null
                                      && d.RevokedAt == null, ct);

        if (device is null)
            return new DeviceCheck(null, Unauthorized(new { error = "unknown_device" }));

        var secret = _secretProtector.Unprotect(device.SecretProtected);
        if (string.IsNullOrEmpty(secret)
            || !DeviceSignature.Verify(secret, deviceIdHeader, timestampMs, body, signature))
            return new DeviceCheck(null, Unauthorized(new { error = "invalid_device_signature" }));

        return new DeviceCheck(device, null);
    }

    /// <summary>
    /// "Rs 500 debited" must never be read as income. Kept server-side as well so an
    /// older handset cannot close an invoice with a debit alert.
    /// </summary>
    private static bool IsDebitOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        var debit = lower.Contains("debited") || lower.Contains("withdrawn") || lower.Contains("deducted")
                    || lower.Contains("paid to") || lower.Contains("sent to") || lower.Contains("spent");
        var credit = lower.Contains("credited") || lower.Contains("received") || lower.Contains("deposited");
        return debit && !credit;
    }

    /// <summary>
    /// The single place a payment closes an invoice, shared by the PSP webhook, the
    /// device bridge and the manual review queue.
    /// </summary>
    private Payment CloseInvoice(Invoice invoice, Guid tenantId, decimal? amount, string? utr, string? razorpayPaymentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var paid = amount ?? invoice.BalanceDue;
        var userId = User.GetUserId();
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = razorpayPaymentId != null ? PaymentMethod.Razorpay : PaymentMethod.UPI,
            Amount = paid,
            Utr = utr,
            RazorpayPaymentId = razorpayPaymentId,
            Status = PaymentStatus.Completed,
            VerificationStatus = PaymentVerificationStatus.Confirmed,
            ConfirmedAt = now,
            ConfirmedByUserId = userId,
            PaidAt = now,
            CreatedAt = now
        };
        _db.Payments.Add(payment);
        InvoiceAccounting.ConfirmPayment(_db, invoice, payment, userId);
        return payment;
    }

    // ----- PSP webhook receiver (anonymous) -----

    [HttpPost("webhooks/payments/{provider}/{pathToken}")]
    [AllowAnonymous]
    public async Task<IActionResult> Receive(string provider, string pathToken, CancellationToken ct)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!PaymentProviders.Contains(provider))
            return NotFound(new { error = "unknown_provider" });

        // No RLS on TenantIntegrations by design: this lookup happens before any tenant
        // context exists. It is the routing step that establishes the tenant.
        var integration = await _db.TenantIntegrations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.WebhookPathToken == pathToken && i.Provider == provider, ct);

        if (integration is null || !integration.Enabled)
            return NotFound(new { error = "integration_not_found" });

        var secret = _secretProtector.Unprotect(integration.SecretProtected);
        if (string.IsNullOrEmpty(secret))
            return StatusCode(503, new { error = "integration_secret_unavailable" });

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var signature = GetSignatureHeader();
        if (string.IsNullOrWhiteSpace(signature) || !PaymentWebhookCodec.Verify(provider, secret, body, signature))
            return BadRequest(new { error = "invalid_webhook_signature" });

        var evt = PaymentWebhookCodec.TryParse(provider, body);
        if (evt is null)
            return Ok(new { status = "ignored" }); // signed but no payment in the payload
        if (evt.EventId.Length > 255 || evt.Utr?.Length > 64 || evt.Reference?.Length > 255)
            return BadRequest(new { error = "invalid_payment_reference" });

        if (integration.TenantId == Guid.Empty)
            return StatusCode(503, new { error = "integration_tenant_unavailable" });

        // Bind EF filters and command-level RLS only after signature verification.
        using var tenantScope = _tenantContext?.PushTenant(integration.TenantId);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        if (transaction != null)
            await _db.Database.ExecuteSqlAsync(
                $"SELECT set_config('app.current_tenant_id', {integration.TenantId.ToString()}, true), set_config('app.is_platform_admin', 'false', true)", ct);

        try
        {
            var already = await _db.ProcessedWebhookEvents.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.TenantId == integration.TenantId && e.Provider == provider && e.EventId == evt.EventId, ct);
            if (already != null)
            {
                if (transaction != null) await transaction.CommitAsync(ct);
                return Ok(new { status = "duplicate", outcome = already.Status, paymentId = already.PaymentId });
            }

            var paymentMatches = provider == "razorpay"
                ? await InvoiceAccounting.FindRazorpayPaymentsAsync(_db, integration.TenantId, evt, ct) : [];
            var existingPayment = paymentMatches.Count == 1 ? paymentMatches[0] : null;
            var invoice = await MatchInvoiceAsync(integration.TenantId, evt, ct);

            var record = new ProcessedWebhookEvent
            {
                Id = Guid.NewGuid(),
                TenantId = integration.TenantId,
                Provider = provider,
                EventId = evt.EventId,
                Amount = evt.Amount,
                Utr = evt.Utr,
                Reference = evt.Reference,
                RawPayload = body.Length > 4000 ? body[..4000] : body,
                ReceivedAt = DateTimeOffset.UtcNow
            };

            if (existingPayment is { Status: PaymentStatus.Completed, VerificationStatus: PaymentVerificationStatus.Confirmed }
                && InvoiceAccounting.CaptureAgrees(existingPayment, evt) && existingPayment.RazorpayPaymentId == evt.ProviderPaymentId)
            {
                record.PaymentId = existingPayment.Id;
                _db.ProcessedWebhookEvents.Add(record);
                await _db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                return Ok(new { status = "duplicate" });
            }

            if (invoice is null)
            {
                record.Status = ProcessedWebhookEvent.Unmatched;
                _db.ProcessedWebhookEvents.Add(record);
                await _db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                _logger.LogWarning("Unmatched {Provider} payment (event {EventId}) for tenant {TenantId}",
                    provider, evt.EventId, integration.TenantId);
                return Ok(new { status = "unmatched" });
            }

            var payment = existingPayment ?? CloseInvoice(invoice, integration.TenantId, evt.Amount, evt.Utr, evt.ProviderPaymentId);
            if (existingPayment != null)
            {
                payment.RazorpayPaymentId = evt.ProviderPaymentId;
                payment.Utr = evt.Utr ?? payment.Utr;
                InvoiceAccounting.ConfirmPayment(_db, invoice, payment, null);
            }
            payment.RazorpayOrderId = evt.ProviderOrderId;

            record.Status = ProcessedWebhookEvent.Processed;
            record.PaymentId = payment.Id;
            _db.ProcessedWebhookEvents.Add(record);

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            _logger.LogInformation("Auto-closed invoice {InvoiceId} from {Provider} webhook for tenant {TenantId}",
                invoice.Id, provider, integration.TenantId);

            return Ok(new { status = "ok" });
        }
        catch (DbUpdateException ex) when (InvoiceAccounting.IsWebhookDuplicate(ex))
        {
            // Concurrent delivery of the same event: the other request won, this one is a no-op.
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Ok(new { status = "duplicate" });
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return StatusCode(503, new { error = "payment_changed_concurrently", message = "Retry this signed event with the same event ID." });
        }
    }

    // ----- matching / verification helpers -----

    private async Task<Invoice?> MatchInvoiceAsync(Guid tenantId, PaymentEvent evt, CancellationToken ct)
    {
        if (evt.Amount is null or <= 0 || decimal.Round(evt.Amount.Value, 2) != evt.Amount)
            return null;

        var amount = evt.Amount.Value;
        if (evt.ProviderPaymentId != null)
        {
            var matches = await InvoiceAccounting.FindRazorpayPaymentsAsync(_db, tenantId, evt, ct);
            if (matches.Count > 1) return null;
            var pending = matches.SingleOrDefault();
            if (pending != null)
            {
                if (pending.Status != PaymentStatus.Pending || pending.VerificationStatus != PaymentVerificationStatus.Pending
                    || !InvoiceAccounting.CaptureAgrees(pending, evt))
                    return null;
                var target = await _db.Invoices.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.Id == pending.InvoiceId && i.TenantId == tenantId, ct);
                return target != null && InvoiceAccounting.CanCollect(target, amount) && InvoiceAccounting.ReferenceAgrees(target, evt.Reference)
                    ? target : null;
            }
            // An unknown provider order is not a reason to guess another patient's bill by amount.
            if (!string.IsNullOrWhiteSpace(evt.ProviderOrderId) && string.IsNullOrWhiteSpace(evt.Reference)) return null;
        }
        var query = _db.Invoices.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId
                && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.Finalized || i.Status == InvoiceStatus.PartiallyPaid)
                && i.BalanceDue >= amount);

        if (!string.IsNullOrWhiteSpace(evt.Reference))
        {
            var references = InvoiceAccounting.InvoiceReferences(evt.Reference);
            if (references.Count != 1) return null;
            var reference = references[0];
            var parts = reference.Split('-');
            var number = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            var financialYear = $"{parts[1]}-{parts[2]}";
            var matches = await query.Where(i => i.InvoiceNumber == number && i.FinancialYear == financialYear).ToListAsync(ct);
            return matches.SingleOrDefault(i => string.Equals(i.FormatInvoiceNumber(), reference, StringComparison.OrdinalIgnoreCase));
        }

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var candidates = await query.Where(i => i.BalanceDue == amount && i.CreatedAt >= since)
            .Take(2)
            .ToListAsync(ct);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private string GetSignatureHeader()
    {
        string[] names = ["X-Paytm-Signature", "X-PhonePe-Signature", "X-Webhook-Signature", "X-Verify", "X-Razorpay-Signature"];
        foreach (var name in names)
        {
            if (Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.ToString();
        }
        return string.Empty;
    }

    private string PublicBaseUrl() =>
        _config["App:PublicBaseUrl"]?.TrimEnd('/')
        ?? $"{Request.Scheme}://{Request.Host}";
}

public record UpsertIntegrationRequest(
    [MaxLength(255)] string? MerchantId,
    [MaxLength(500)] string? Secret,
    bool? Enabled);

public record MatchUnmatchedRequest(Guid InvoiceId);

/// <summary>
/// B2.9 bridge payload. Amount comes from the parsed bank SMS / soundbox text on the
/// device; the server still matches it against an open invoice and never trusts the
/// device for the tenant.
/// </summary>
public record DevicePaymentNotification(
    string EventId,
    decimal? Amount,
    string? Utr = null,
    string? Reference = null,
    string? Source = null,
    string? RawText = null);

public record CreatePairingCodeRequest(string? Name);

public record PairDeviceRequest(string PairingCode, string? Name = null);
