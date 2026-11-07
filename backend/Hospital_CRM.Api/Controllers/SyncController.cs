using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

public record SyncPushItem(Guid Id, int Type, string? IdempotencyKey, string PayloadJson, DateTimeOffset CreatedAt);
public record SyncPushRequest(List<SyncPushItem> Items);
public record PatientConsentSyncPayload(bool Accepted, string? Purpose);
public record PatientSyncPayload(
    string Name, string Phone, DateOnly? Dob, int? ApproxAge, string? Gender, string? Address,
    PatientConsentSyncPayload? Consent, bool WhatsAppOptIn = false, string? GuardianName = null,
    string? GuardianRelationship = null, string? NoticeVersion = null, string? Language = null,
    string? CaptureMethod = null, bool ConfirmDifferentPersonOnSamePhone = false);

public record InvoiceSyncPayload(
    Guid? PatientId, decimal Subtotal, decimal GstAmount, decimal Total, string? PaymentMethod,
    string? WalkInCustomerName, string? WalkInCustomerPhone, string? Status,
    Guid AppointmentId = default, List<InvoiceLineItemRequest>? LineItems = null,
    decimal DiscountAmount = 0m, string? DiscountReason = null, Guid? LinkedPharmacyInvoiceId = null);

public record SyncItemResultDto(Guid Id, string? IdempotencyKey, string Status, string Type, string? Reason = null);
public record SyncPushResponse(int Synced, int Skipped, List<SyncItemResultDto> Results);

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class SyncController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IPatientSearchService _search;
    private readonly ILogger<SyncController> _logger;
    private readonly IConfiguration _config;
    private readonly RazorpayOrderClient _razorpay;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxPayloadLength = 65536;
    private const int MaxBatchItems = 500;

    public SyncController(HospitalCrmDbContext db, IPatientSearchService search, ILogger<SyncController> logger,
        IConfiguration config, RazorpayOrderClient razorpay)
    {
        _db = db;
        _search = search;
        _logger = logger;
        _config = config;
        _razorpay = razorpay;
    }

    [HttpPost("push")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist", "Doctor")]
    [RequirePermission(Permissions.PatientWrite)]
    public async Task<IActionResult> Push([FromBody] SyncPushRequest request, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId || userId == Guid.Empty
            || User.GetTenantId() is not { } tenantId || tenantId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });
        if (!Enum.TryParse<UserRole>(User.GetUserRole(), true, out var role)
            || !Permissions.RoleHasPermission(role, Permissions.PatientWrite)
            || !Permissions.RoleHasPermission(role, Permissions.InvoiceWrite)
            || !Permissions.RoleHasPermission(role, Permissions.PaymentRecord))
            return StatusCode(403, new { error = "sync_permission_denied" });
        if (request?.Items == null || request.Items.Count == 0)
            return Ok(new SyncPushResponse(0, 0, []));
        if (request.Items.Count > MaxBatchItems)
            return BadRequest(new { error = "batch_too_large", maxItems = MaxBatchItems });
        if (request.Items.Any(item => item == null))
            return BadRequest(new { error = "invalid_sync_item" });

        var results = new List<SyncItemResultDto>();
        foreach (var item in request.Items)
        {
            var type = item.Type == 1 ? "patient" : item.Type == 2 ? "invoice" : "unknown";
            if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.IdempotencyKey) || item.IdempotencyKey.Length > 128)
            {
                results.Add(new(item.Id, item.IdempotencyKey, "rejected", type, "A client ID and a stable key of at most 128 characters are required."));
                continue;
            }
            if (item.PayloadJson == null || item.PayloadJson.Length > MaxPayloadLength)
            {
                results.Add(new(item.Id, item.IdempotencyKey, "rejected", type, "Payload exceeds the 64KB limit or is missing."));
                continue;
            }
            if (item.Type is not (1 or 2))
            {
                results.Add(new(item.Id, item.IdempotencyKey, "unsupported_type", type));
                continue;
            }
            var prefix = item.Type == 1 ? "PAT" : "INV";
            if (!Regex.IsMatch(item.IdempotencyKey, $@"^IDEMP-{prefix}-[a-zA-Z0-9\-]+$") && !Guid.TryParse(item.IdempotencyKey, out _))
            {
                results.Add(new(item.Id, item.IdempotencyKey, "invalid_key_format", type));
                continue;
            }

            // Each queued write is atomic; a bad item must not roll back earlier acknowledgements.
            SyncItemResultDto result;
            await using (var transaction = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null)
            {
                try
                {
                    result = item.Type == 1 ? await RegisterPatientAsync(item, ct) : await CreateInvoiceAsync(item, tenantId, ct);
                    if (result.Status is "created" or "idempotent_duplicate")
                    {
                        if (transaction != null) await transaction.CommitAsync(ct);
                    }
                    else if (transaction != null) await transaction.RollbackAsync(ct);
                }
                catch (JsonException)
                {
                    if (transaction != null) await transaction.RollbackAsync(ct);
                    result = new(item.Id, item.IdempotencyKey, "invalid_payload", type, "Malformed JSON payload.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (transaction != null) await transaction.RollbackAsync(ct);
                    _logger.LogError(ex, "Sync item {ItemId} failed", item.Id);
                    result = new(item.Id, item.IdempotencyKey, "retry_required", type, "Retry this item with the same key; no success was acknowledged.");
                }
                finally
                {
                    _db.ChangeTracker.Clear();
                }
            }
            results.Add(result);

            // The write is committed and disposed; indexing cannot roll it back or change its acknowledgement.
            if (item.Type == 1 && result.Status is "created" or "idempotent_duplicate")
            {
                try
                {
                    var patient = await _db.Patients.AsNoTracking()
                        .SingleAsync(p => p.Id == result.Id && p.TenantId == tenantId, ct);
                    await _search.IndexAsync(patient, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Committed sync patient {PatientId} awaits search reconciliation ({FailureType})", result.Id, ex.GetType().Name);
                }
            }
        }
        var synced = results.Count(r => r.Status is "created" or "idempotent_duplicate");
        return Ok(new SyncPushResponse(synced, results.Count - synced, results));
    }

    private async Task<SyncItemResultDto> RegisterPatientAsync(SyncPushItem item, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<PatientSyncPayload>(item.PayloadJson, JsonOptions);
        if (payload == null) return new(item.Id, item.IdempotencyKey, "invalid_payload", "patient");
        var controller = new PatientsController(_db, _search) { ControllerContext = ControllerContext };
        var action = await controller.RegisterPatientAsync(new(payload.Name, payload.Phone, payload.Dob, payload.ApproxAge,
            payload.Gender, payload.Address, payload.Consent == null ? null : new ConsentRequest(payload.Consent.Accepted, payload.Consent.Purpose ?? ""),
            item.IdempotencyKey, payload.WhatsAppOptIn, payload.GuardianName, payload.GuardianRelationship,
            payload.NoticeVersion, payload.Language, payload.CaptureMethod, payload.ConfirmDifferentPersonOnSamePhone), ct, item.Id);
        var data = JsonSerializer.SerializeToElement((action as ObjectResult)?.Value);
        if (action is ObjectResult { StatusCode: 201 }) return new(item.Id, item.IdempotencyKey, "created", "patient");
        if (action is OkObjectResult && data.TryGetProperty("idempotentReplay", out var replay) && replay.GetBoolean())
            return new(data.GetProperty("patientId").GetGuid(), item.IdempotencyKey, "idempotent_duplicate", "patient");
        if (action is OkObjectResult)
            return new(item.Id, item.IdempotencyKey, "duplicate_patient", "patient", "Review the existing patient or confirm family-phone sharing before retrying.");
        return Rejected(item, "patient", action);
    }

    private async Task<SyncItemResultDto> CreateInvoiceAsync(SyncPushItem item, Guid tenantId, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<InvoiceSyncPayload>(item.PayloadJson, JsonOptions);
        if (payload?.LineItems is not { Count: > 0 } || payload.AppointmentId == Guid.Empty
            || !string.IsNullOrWhiteSpace(payload.WalkInCustomerName) || !string.IsNullOrWhiteSpace(payload.WalkInCustomerPhone))
            return new(item.Id, item.IdempotencyKey, "invalid_payload", "invoice", "Consultation billing requires an appointment and authoritative line items, not header totals or walk-in stock sales.");
        var status = payload.Status?.ToLowerInvariant() ?? "draft";
        if (status is not ("draft" or "unpaid" or "issued" or "finalized" or "paid"))
            return new(item.Id, item.IdempotencyKey, "invalid_payload", "invoice", "Unsupported offline invoice state.");
        if (status == "paid" && !string.Equals(payload.PaymentMethod, "cash", StringComparison.OrdinalIgnoreCase))
            return new(item.Id, item.IdempotencyKey, "invalid_payload", "invoice", "Only genuinely collected cash can be confirmed offline. Electronic payments require the normal confirmation flow.");
        if (payload.PatientId.HasValue && !await _db.Appointments.AnyAsync(a => a.Id == payload.AppointmentId
            && a.TenantId == tenantId && a.PatientId == payload.PatientId, ct))
            return new(item.Id, item.IdempotencyKey, "invalid_payload", "invoice", "The patient does not belong to this appointment.");

        var controller = new InvoicesController(_db, _config, _razorpay) { ControllerContext = ControllerContext };
        var action = await controller.GenerateInvoiceAsync(new(payload.AppointmentId, payload.LineItems, item.IdempotencyKey,
            payload.DiscountAmount, payload.DiscountReason, payload.LinkedPharmacyInvoiceId, status != "draft"), ct, item.Id);
        if (action is not ObjectResult { StatusCode: 200 or 201 }) return Rejected(item, "invoice", action);
        var data = JsonSerializer.SerializeToElement(((ObjectResult)action).Value);
        var invoiceId = data.GetProperty("invoiceId").GetGuid();
        var isReplay = action is OkObjectResult;
        if (status == "paid" && data.GetProperty("total").GetDecimal() > 0)
        {
            var paymentKey = $"IDEMP-PAY-{invoiceId:N}";
            if (isReplay && !await _db.Payments.AnyAsync(p => p.TenantId == tenantId && p.InvoiceId == invoiceId
                && p.IdempotencyKey == paymentKey && p.Amount > 0 && p.Method == PaymentMethod.Cash, ct))
                return new(item.Id, item.IdempotencyKey, "conflict", "invoice", "A replay cannot add a new cash receipt to an originally unpaid bill. Use the normal collection workflow.");
            var receipt = await controller.CollectPayment(invoiceId, new("cash", data.GetProperty("total").GetDecimal(), paymentKey), ct);
            if (receipt is not OkObjectResult) return Rejected(item, "invoice", receipt);
        }
        return new(invoiceId, item.IdempotencyKey, isReplay ? "idempotent_duplicate" : "created", "invoice");
    }

    private static SyncItemResultDto Rejected(SyncPushItem item, string type, IActionResult action)
    {
        var data = JsonSerializer.SerializeToElement((action as ObjectResult)?.Value);
        var reason = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var error) ? error.GetString() : "Request rejected.";
        return new(item.Id, item.IdempotencyKey, action is ConflictObjectResult ? "conflict" : "invalid_payload", type, reason);
    }
}
