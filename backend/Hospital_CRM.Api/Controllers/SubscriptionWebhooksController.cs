using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

/// <summary>
/// B4: SaaS subscription billing webhooks. Unlike B2's payment webhooks these belong to
/// SAMSTACK's own merchant account, not the clinic's, so the secret is platform config
/// rather than a per-tenant integration row. The tenant is carried in the subscription
/// notes (there is no tenant claim on an anonymous webhook) and every transition is
/// signature-verified, deduped and applied through the A4 state machine.
/// </summary>
[ApiController]
[Route("api/v1/webhooks")]
public class SubscriptionWebhooksController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<SubscriptionWebhooksController> _logger;
    private readonly ITenantContext? _tenantContext;

    public SubscriptionWebhooksController(
        HospitalCrmDbContext db,
        IConfiguration config,
        ILogger<SubscriptionWebhooksController> logger,
        ITenantContext? tenantContext = null)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    [HttpPost("subscriptions/{provider}")]
    [AllowAnonymous]
    public async Task<IActionResult> Receive(string provider, CancellationToken ct)
    {
        provider = (provider ?? string.Empty).Trim().ToLowerInvariant();

        var secret = _config[$"SubscriptionBilling:WebhookSecrets:{provider}"];
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(503, new { error = "billing_not_configured" });

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Razorpay-Signature"].ToString();
        if (string.IsNullOrWhiteSpace(signature) || !PaymentWebhookCodec.Verify(provider, secret, body, signature))
            return BadRequest(new { error = "invalid_webhook_signature" });

        var billingEvent = SubscriptionLifecycle.Parse(provider, body);
        if (billingEvent is null)
            return Ok(new { status = "ignored" });

        if (billingEvent.TenantId is null || billingEvent.TenantId == Guid.Empty)
        {
            // Nothing to scope the dedupe row to, so this cannot be recorded per tenant.
            _logger.LogWarning("Subscription webhook for {Provider} carried no notes.tenant_id", provider);
            return Accepted(new { status = "unresolved_tenant" });
        }

        var tenantId = billingEvent.TenantId.Value;
        using var tenantScope = _tenantContext?.PushTenant(tenantId);
        var eventId = Request.Headers["X-Razorpay-Event-Id"].ToString();
        if (string.IsNullOrWhiteSpace(eventId))
            eventId = PaymentWebhookCodec.SignSha256(body);

        // Namespaced so a subscription event can never collide with a payment event
        // from the same PSP.
        var eventProvider = provider + ":subscription";

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // Set tenant context for RLS (PostgreSQL). In-memory provider throws, so guard it.
            try
            {
                await _db.Database.ExecuteSqlAsync(
                    $"SELECT set_config('app.current_tenant_id', {tenantId.ToString()}, true), set_config('app.is_platform_admin', 'false', true)", ct);
            }
            catch (InvalidOperationException)
            {
                // In-memory provider doesn't support ExecuteSqlAsync; RLS not enforced in tests.
            }

            var already = await _db.ProcessedWebhookEvents.IgnoreQueryFilters()
                .AnyAsync(e => e.TenantId == tenantId && e.Provider == eventProvider && e.EventId == eventId, ct);
            if (already)
            {
                await transaction.CommitAsync(ct);
                return Ok(new { status = "duplicate" });
            }

            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null)
            {
                await transaction.CommitAsync(ct);
                return Accepted(new { status = "unresolved_tenant" });
            }

            var (status, endsAt) = SubscriptionLifecycle.Transition(billingEvent, tenant.SubscriptionEndsAt);

            var changed = tenant.SubscriptionStatus != status || tenant.SubscriptionEndsAt != endsAt;
            tenant.SubscriptionStatus = status;
            tenant.SubscriptionEndsAt = endsAt;
            tenant.UpdatedAt = DateTimeOffset.UtcNow;

            // Clinic mirrors the tenant's billing state, exactly as provisioning sets it.
            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
            if (clinic is not null)
            {
                clinic.SubscriptionStatus = status;
                clinic.SubscriptionEndsAt = endsAt;
                clinic.UpdatedAt = tenant.UpdatedAt;
            }

            _db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Provider = eventProvider,
                EventId = eventId,
                Status = ProcessedWebhookEvent.Processed,
                Reference = billingEvent.ProviderSubscriptionId,
                RawPayload = body.Length > 4000 ? body[..4000] : body,
                ReceivedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation("Subscription {Kind} for tenant {TenantId} -> {Status}", billingEvent.Kind, tenantId, status);

            return Ok(new
            {
                status = "ok",
                subscriptionStatus = status.ToString().ToLowerInvariant(),
                subscriptionEndsAt = endsAt,
                changed
            });
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Concurrent delivery of the same event: the other request won, this one is a no-op.
            await transaction.RollbackAsync(ct);
            return Ok(new { status = "duplicate" });
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
