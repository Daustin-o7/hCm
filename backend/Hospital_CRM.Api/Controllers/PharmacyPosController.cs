using System.Data;
using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/pharmacy")]
[RequireModule(ModuleRegistry.Pharmacy)]
public class PharmacyPosController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<PharmacyPosController> _logger;
    private readonly ITimezoneService _timezoneService;
    private readonly IConfiguration _config;
    private readonly RazorpayOrderClient _razorpay;

    public PharmacyPosController(
        HospitalCrmDbContext db,
        ILogger<PharmacyPosController> logger,
        ITimezoneService timezoneService,
        IConfiguration config,
        RazorpayOrderClient razorpay)
    {
        _db = db;
        _logger = logger;
        _timezoneService = timezoneService;
        _config = config;
        _razorpay = razorpay;
    }

    private Guid TenantId => User.GetTenantId()!.Value;

    /// <summary>Fail-closed Form 20/21 gate: a missing clinic row or licence is "not licensed".</summary>
    private async Task<Clinic?> GetLicensedClinicAsync(CancellationToken ct)
    {
        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.TenantId == TenantId, ct);
        return clinic != null &&
               (!string.IsNullOrWhiteSpace(clinic.PharmacyLicenceNumber20) ||
                !string.IsNullOrWhiteSpace(clinic.PharmacyLicenceNumber21))
            ? clinic
            : null;
    }

    [HttpPost("checkout")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Receptionist", "Doctor")]
    public async Task<IActionResult> CheckoutPos([FromBody] PosCheckoutRequest req, CancellationToken ct)
    {
        var tenantId = TenantId;
        var currentUserId = User.GetUserId();
        if (!currentUserId.HasValue || tenantId == Guid.Empty) return Unauthorized();
        if (req.Items is not { Count: > 0 and <= 50 })
            return BadRequest(new { error = "invalid_cart_size" });
        if (req.Items.Any(i => i is null || i.DrugId == Guid.Empty || i.DrugBatchId == Guid.Empty || i.Quantity is < 1 or > 1000))
            return BadRequest(new { error = "invalid_line_quantity_or_batch" });
        var paymentMethod = PaymentMethod.Cash;
        if (!string.IsNullOrWhiteSpace(req.PaymentMethod) && !Enum.TryParse(req.PaymentMethod, true, out paymentMethod)
            || paymentMethod is not (PaymentMethod.Cash or PaymentMethod.UPI or PaymentMethod.Card or PaymentMethod.NetBanking))
            return BadRequest(new { error = "unsupported_counter_payment_method" });
        if (req.TenderedAmount < 0 || req.TenderedAmount > 1000000m || decimal.Round(req.TenderedAmount, 2) != req.TenderedAmount
            || req.PaymentReference?.Length > 64)
            return BadRequest(new { error = "invalid_payment_metadata" });
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 128)
            return BadRequest(new { error = "idempotency_key_required", message = "Use the same key when retrying this checkout." });
        if (paymentMethod != PaymentMethod.Cash && string.IsNullOrWhiteSpace(req.PaymentReference))
            return BadRequest(new { error = "payment_reference_required", message = "Record the actual terminal/bank receipt reference." });

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {

            // 1. Regulatory Licence Gate (Form 20 / Form 21). Fail closed: no clinic row or no
            // licence number means no checkout.
            if (await GetLicensedClinicAsync(ct) is null)
            {
                return StatusCode(403, new
                {
                    error = "pharmacy_licence_missing",
                    message = "Clinic must register Form 20 or Form 21 Retail Drug Licence before dispensing medications."
                });
            }

            if (req.PatientId.HasValue && !await _db.Patients.AnyAsync(p => p.Id == req.PatientId && p.TenantId == tenantId, ct))
                return BadRequest(new { error = "invalid_patient" });

            var requestHash = PharmacyCommon.RequestHash(req);
            var existing = await _db.Invoices.AsNoTracking()
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.IdempotencyKey == req.IdempotencyKey, ct);
            if (existing != null)
            {
                if (existing.InvoiceType != InvoiceType.Pharmacy || existing.CheckoutRequestHash != requestHash)
                    return Conflict(new { error = "idempotency_key_conflict", message = "This key belongs to another checkout payload, or a historical checkout without saved retry evidence. Review the original invoice; do not submit it as a new sale." });
                if (existing.CheckoutResultJson is null)
                    return Conflict(new { error = "saved_checkout_receipt_unavailable" });
                return Ok(JsonSerializer.Deserialize<JsonElement>(existing.CheckoutResultJson));
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            // Fetch and validate batches
            var batchIds = req.Items.Select(i => i.DrugBatchId).Distinct().ToList();
            var batches = new List<DrugBatch>();
            foreach (var bId in batchIds)
            {
                var b = await _db.DrugBatches.Include(x => x.Drug).FirstOrDefaultAsync(x => x.Id == bId && x.TenantId == tenantId, ct);
                if (b != null) batches.Add(b);
            }

            var batchMap = batches.ToDictionary(b => b.Id);

            decimal subtotal = 0m;
            decimal totalGst = 0m;
            var scheduleH1Items = new List<(DrugBatch Batch, int Qty, Drug Drug)>();

            foreach (var item in req.Items)
            {
                if (!batchMap.TryGetValue(item.DrugBatchId, out var batch))
                    return BadRequest(new { error = $"batch_not_found: {item.DrugBatchId}" });

                if (item.DrugId != batch.DrugId)
                    return BadRequest(new { error = $"batch_drug_mismatch: {batch.BatchNumber}" });

                if (batch.IsRecalled)
                    return BadRequest(new { error = $"cannot_dispense_recalled_batch: {batch.BatchNumber}", reason = batch.RecallReason });

                if (batch.ExpiryDate < today)
                    return BadRequest(new { error = $"cannot_dispense_expired_batch: {batch.BatchNumber}" });

                if (batch.QuantityRemaining < req.Items.Where(i => i.DrugBatchId == batch.Id).Sum(i => (long)i.Quantity))
                    return BadRequest(new { error = $"insufficient_stock_for_batch: {batch.BatchNumber} (Available: {batch.QuantityRemaining}, Requested: {item.Quantity})" });

                var drug = batch.Drug!;
                if (drug == null || drug.TenantId != tenantId || batch.Mrp <= 0 || batch.Mrp > 500000m
                    || decimal.Round(batch.Mrp, 2) != batch.Mrp || drug.GstRate < 0 || drug.GstRate > 28
                    || decimal.Round(drug.GstRate, 2) != drug.GstRate)
                    return BadRequest(new { error = "invalid_catalogue_price_or_tax" });

                // 2. Regulatory Check: Hard-block NDPS digital dispensing
                if (drug.ScheduleClass == ScheduleClass.NDPS)
                {
                    return BadRequest(new
                    {
                        error = "ndps_digital_dispensing_prohibited",
                        message = "NDPS drugs are legally prohibited from digital retail dispensing under NDPS Rules. Physical official triplicate book register required."
                    });
                }

                if (drug.ScheduleClass is ScheduleClass.ScheduleH1 or ScheduleClass.ScheduleX)
                {
                    scheduleH1Items.Add((batch, item.Quantity, drug));
                }

                // 3. Indian Pharma MRP-Inclusive Reverse GST Calculation.
                // The patient pays the printed MRP and the catalogue decides the tax rate; the
                // client's price and GST fields are not trusted.
                var lineGross = batch.Mrp * item.Quantity;
                var lineTaxable = decimal.Round(lineGross / (1m + (drug.GstRate / 100m)), 2, MidpointRounding.AwayFromZero);
                var lineGst = lineGross - lineTaxable;

                subtotal += lineTaxable;
                totalGst += lineGst;
            }

            // Regulatory Check for Schedule H1
            if (scheduleH1Items.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(req.PrescriberName) || string.IsNullOrWhiteSpace(req.PrescriberRegNo))
                {
                    return BadRequest(new
                    {
                        error = "schedule_h1_prescriber_required",
                        message = "Prescription and Doctor Medical Registration No. are legally mandated under Drugs & Cosmetics Act for Schedule H1/ScheduleX drugs."
                    });
                }

                var patientIdentifier = req.PatientName ?? req.WalkInCustomerName;
                if (string.IsNullOrWhiteSpace(patientIdentifier))
                {
                    return BadRequest(new
                    {
                        error = "patient_name_required_for_scheduled_drug",
                        message = "Patient name is required for statutory Schedule H1 dispensing register."
                    });
                }
            }

            var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);
            var maxInvoiceNumber = await _db.Invoices
                .Where(i => i.TenantId == tenantId && i.FinancialYear == currentFy && i.Series == "PHARM")
                .MaxAsync(i => (int?)i.InvoiceNumber, ct) ?? 0;

            var invoiceTotal = subtotal + totalGst;
            if (invoiceTotal > 1000000m || req.TenderedAmount > 0 && req.TenderedAmount < invoiceTotal)
                return BadRequest(new { error = "insufficient_tendered_amount_or_total_limit" });
            if (paymentMethod != PaymentMethod.Cash && req.TenderedAmount > 0 && req.TenderedAmount != invoiceTotal)
                return BadRequest(new { error = "electronic_payment_amount_mismatch" });

            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                AppointmentId = null,
                PatientId = req.PatientId,
                InvoiceType = InvoiceType.Pharmacy,
                Series = "PHARM",
                FinancialYear = currentFy,
                WalkInCustomerName = req.WalkInCustomerName ?? req.PatientName,
                WalkInCustomerPhone = req.WalkInCustomerPhone,
                InvoiceNumber = maxInvoiceNumber + 1,
                Subtotal = subtotal,
                GstAmount = totalGst,
                Total = invoiceTotal,
                PaidAmount = 0m,
                BalanceDue = invoiceTotal,
                Status = InvoiceStatus.Finalized,
                DocumentType = totalGst > 0 ? InvoiceDocumentType.TaxInvoice : InvoiceDocumentType.BillOfSupply,
                FinalizedAt = DateTimeOffset.UtcNow,
                FinalizedByUserId = currentUserId,
                IdempotencyKey = req.IdempotencyKey,
                CheckoutRequestHash = requestHash,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Deduct stock & create stock movements
            foreach (var item in req.Items)
            {
                var batch = batchMap[item.DrugBatchId];
                var drug = batch.Drug!;
                batch.QuantityRemaining -= item.Quantity;

                _db.DrugStockMovements.Add(new DrugStockMovement
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DrugId = drug.Id,
                    DrugBatchId = batch.Id,
                    Quantity = item.Quantity,
                    Direction = MovementDirection.Out,
                    ReferenceType = "Dispense",
                    ReferenceId = invoice.Id,
                    Note = $"POS Checkout Inv #{invoice.FormatInvoiceNumber()}",
                    RecordedBy = currentUserId.Value,
                    RecordedAt = DateTimeOffset.UtcNow
                });

                var lineGross = batch.Mrp * item.Quantity;
                var lineTaxable = decimal.Round(lineGross / (1m + (drug.GstRate / 100m)), 2, MidpointRounding.AwayFromZero);

                invoice.LineItems.Add(new InvoiceLineItem
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    Description = $"{drug.Name} (Batch: {batch.BatchNumber})",
                    Quantity = item.Quantity,
                    UnitPrice = batch.Mrp,
                    Amount = lineTaxable,
                    TaxAmount = lineGross - lineTaxable,
                    GstRate = drug.GstRate,
                    TaxCategory = drug.GstRate > 0 ? TaxCategory.Taxable : TaxCategory.Exempt,
                    HsnCode = drug.HsnCode,
                    // GSTR-1 table 12 groups by HsnSacCode; without this the drug falls under the
                    // healthcare-service default 999312 instead of its drug HSN.
                    HsnSacCode = drug.HsnCode ?? "999312",
                    DrugBatchId = batch.Id
                });
            }

            _db.Invoices.Add(invoice);

            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                Amount = invoiceTotal,
                Method = paymentMethod,
                Utr = req.PaymentReference?.Trim(),
                IdempotencyKey = $"POS-PAY-{invoice.Id:N}",
                Status = PaymentStatus.Completed,
                VerificationStatus = PaymentVerificationStatus.Confirmed,
                ConfirmedByUserId = currentUserId,
                PaidAt = DateTimeOffset.UtcNow,
                ConfirmedAt = DateTimeOffset.UtcNow
            };
            _db.Payments.Add(payment);
            InvoiceAccounting.PostFinalization(_db, invoice, currentUserId);
            InvoiceAccounting.ConfirmPayment(_db, invoice, payment, currentUserId);

            var dispenserUser = await _db.Users.FindAsync([currentUserId], ct);
            var dispenserName = dispenserUser?.Name ?? "Pharmacist";

            foreach (var (batch, qty, drug) in scheduleH1Items)
            {
                _db.ControlledSubstanceRegisters.Add(new ControlledSubstanceRegister
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    InvoiceId = invoice.Id,
                    DrugId = drug.Id,
                    ScheduleClass = drug.ScheduleClass,
                    DrugName = drug.Name,
                    BatchNumber = batch.BatchNumber,
                    Quantity = qty,
                    PatientName = req.PatientName ?? req.WalkInCustomerName ?? "Walk-in Patient",
                    PatientAddress = req.PatientAddress ?? "Local",
                    PrescriberName = req.PrescriberName!,
                    PrescriberRegNo = req.PrescriberRegNo!,
                    DispensedBy = currentUserId.Value,
                    DispenserName = dispenserName,
                    DispensedAt = DateTimeOffset.UtcNow
                });
            }

            var tendered = req.TenderedAmount > 0 ? req.TenderedAmount : invoiceTotal;
            var changeDue = Math.Max(0, tendered - invoiceTotal);
            var result = new
            {
                idempotencyKey = req.IdempotencyKey,
                invoiceId = invoice.Id,
                invoiceNumber = invoice.FormatInvoiceNumber(),
                subtotal = invoice.Subtotal,
                gstAmount = invoice.GstAmount,
                total = invoice.Total,
                paidAmount = invoice.PaidAmount,
                balanceDue = invoice.BalanceDue,
                status = invoice.Status.ToString(),
                tenderedAmount = tendered,
                changeDue,
                paymentMethod = paymentMethod.ToString(),
                paymentId = payment.Id,
                paymentReference = payment.Utr,
                paymentStatus = payment.Status.ToString(),
                verificationStatus = payment.VerificationStatus.ToString(),
                confirmedAt = payment.ConfirmedAt,
                createdAt = invoice.CreatedAt,
                itemCount = req.Items.Count,
                items = invoice.LineItems.Select(li => new
                {
                    lineItemId = li.Id,
                    drugBatchId = li.DrugBatchId,
                    description = li.Description,
                    quantity = li.Quantity,
                    unitPrice = li.UnitPrice,
                    amount = li.Amount,
                    taxAmount = li.TaxAmount,
                    grossAmount = li.GrossAmount(invoice.InvoiceType),
                    gstRate = li.GstRate,
                    hsnCode = li.HsnCode
                }).ToArray()
            };
            invoice.CheckoutResultJson = JsonSerializer.Serialize(result);
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return StatusCode(201, result);
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex)
            || ex is DbUpdateException update && (InvoiceAccounting.IsIdempotencyConflict(update) || InvoiceAccounting.IsNumberingConflict(update)))
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "checkout_changed_concurrently", message = "Retry checkout with the same key after reloading stock." });
        }
    }

    [HttpPost("returns/{invoiceId:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Receptionist", "Doctor")]
    [RequirePermission(Permissions.PaymentRecord)]
    public Task<IActionResult> ProcessReturn(Guid invoiceId, [FromBody] ReturnRequest req, CancellationToken ct)
    {
        var controller = new InvoicesController(_db, _config, _razorpay) { ControllerContext = ControllerContext };
        return controller.ProcessPharmacyReturnAsync(invoiceId, req, ct);
    }

    [HttpGet("stats")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor")]
    public async Task<IActionResult> GetPharmacyStats(CancellationToken ct)
    {
        var tenantId = TenantId;
        var clinic = await _db.Clinics.FirstOrDefaultAsync(ct);
        var today = _timezoneService.GetClinicToday(clinic?.Timezone, HttpContext);
        var startOfTodayUtc = _timezoneService.GetStartOfDayUtc(today, clinic?.Timezone);
        var endOfTodayUtc = _timezoneService.GetEndOfDayUtc(today, clinic?.Timezone);
        var sixtyDaysLater = today.AddDays(60);

        var totalDrugs = await _db.Drugs.CountAsync(ct);

        var todaySales = await _db.Invoices
            .Where(i => i.InvoiceType == InvoiceType.Pharmacy
                     && i.Status == InvoiceStatus.Paid
                     && i.CreatedAt >= startOfTodayUtc
                     && i.CreatedAt < endOfTodayUtc)
            .SumAsync(i => (decimal?)i.Total, ct) ?? 0m;

        var todayInvoicesCount = await _db.Invoices
            .CountAsync(i => i.InvoiceType == InvoiceType.Pharmacy
                          && i.Status == InvoiceStatus.Paid
                          && i.CreatedAt >= startOfTodayUtc
                          && i.CreatedAt < endOfTodayUtc, ct);

        var expiringSoonBatches = await _db.DrugBatches
            .CountAsync(b => b.QuantityRemaining > 0 && b.ExpiryDate >= today && b.ExpiryDate <= sixtyDaysLater && !b.IsRecalled, ct);

        var expiredBatches = await _db.DrugBatches
            .CountAsync(b => b.QuantityRemaining > 0 && b.ExpiryDate < today, ct);

        var recalledBatches = await _db.DrugBatches
            .CountAsync(b => b.IsRecalled, ct);

        var lowStockCount = await _db.Drugs
            .Where(d => d.Batches.Where(b => b.ExpiryDate >= today && !b.IsRecalled).Sum(b => b.QuantityRemaining) < 20)
            .CountAsync(ct);

        var scheduleH1Today = await _db.ControlledSubstanceRegisters
            .CountAsync(r => r.DispensedAt >= startOfTodayUtc && r.DispensedAt < endOfTodayUtc, ct);

        return Ok(new
        {
            totalDrugs,
            todaySales,
            todayInvoicesCount,
            expiringSoonBatches,
            expiredBatches,
            recalledBatches,
            lowStockCount,
            scheduleH1Today
        });
    }
}
