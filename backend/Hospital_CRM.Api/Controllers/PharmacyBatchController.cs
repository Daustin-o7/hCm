using System.Data;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/pharmacy")]
[RequireModule(ModuleRegistry.Pharmacy)]
public class PharmacyBatchController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<PharmacyBatchController> _logger;

    public PharmacyBatchController(HospitalCrmDbContext db, ILogger<PharmacyBatchController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("drugs/{id:guid}/batches/fefo")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor", "Receptionist", "Nurse")]
    public async Task<IActionResult> GetFefoBatches(Guid id, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nearExpiryThreshold = today.AddDays(60);

        var batches = await _db.DrugBatches.AsNoTracking()
            .Where(b => b.DrugId == id && b.QuantityRemaining > 0 && b.ExpiryDate >= today && !b.IsRecalled)
            .OrderBy(b => b.ExpiryDate) // FEFO: First Expiry First Out
            .Select(b => new
            {
                id = b.Id,
                batchNumber = b.BatchNumber,
                expiryDate = b.ExpiryDate.ToString("yyyy-MM-dd"),
                isNearExpiry = b.ExpiryDate <= nearExpiryThreshold,
                quantityRemaining = b.QuantityRemaining,
                mrp = b.Mrp,
                purchaseRate = b.PurchaseRate,
                packSize = b.PackSize,
                invoiceNumber = b.InvoiceNumber,
                isRecalled = b.IsRecalled
            })
            .ToListAsync(ct);

        return Ok(batches);
    }

    [HttpGet("drugs/{id:guid}/fefo-batch")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor", "Receptionist", "Nurse")]
    public async Task<IActionResult> GetSingleFefoBatch(Guid id, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nearExpiryThreshold = today.AddDays(60);

        var batch = await _db.DrugBatches.AsNoTracking()
            .Where(b => b.DrugId == id && b.QuantityRemaining > 0 && b.ExpiryDate >= today && !b.IsRecalled)
            .OrderBy(b => b.ExpiryDate)
            .Select(b => new
            {
                id = b.Id,
                batchId = b.Id,
                batchNumber = b.BatchNumber,
                expiryDate = b.ExpiryDate.ToString("yyyy-MM-dd"),
                isNearExpiry = b.ExpiryDate <= nearExpiryThreshold,
                quantityRemaining = b.QuantityRemaining,
                mrp = b.Mrp,
                purchaseRate = b.PurchaseRate,
                packSize = b.PackSize,
                invoiceNumber = b.InvoiceNumber
            })
            .FirstOrDefaultAsync(ct);

        if (batch == null)
            return NotFound(new { error = "no_active_batch_in_stock", message = "No active, non-recalled batch in stock for this medicine." });

        return Ok(batch);
    }

    /// <summary>
    /// Tenant-wide near-expiry batch list for the dispensary alert panel. Same 60-day horizon
    /// and stock filters as the per-drug FEFO endpoint.
    /// </summary>
    [HttpGet("batches/expiring")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetExpiringBatches([FromQuery] int days = 60, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(Math.Clamp(days, 1, 365));

        var rows = await _db.DrugBatches.AsNoTracking()
            .Where(b => b.QuantityRemaining > 0 && b.ExpiryDate >= today && b.ExpiryDate <= horizon && !b.IsRecalled)
            .OrderBy(b => b.ExpiryDate)
            .Take(100)
            .Select(b => new
            {
                id = b.Id,
                drugName = b.Drug != null ? b.Drug.Name : "Unknown drug",
                batchNumber = b.BatchNumber,
                expiryDate = b.ExpiryDate,
                quantityRemaining = b.QuantityRemaining
            })
            .ToListAsync(ct);

        return Ok(rows.Select(b => new
        {
            b.id,
            b.drugName,
            b.batchNumber,
            expiryDate = b.expiryDate.ToString("yyyy-MM-dd"),
            daysUntilExpiry = b.expiryDate.DayNumber - today.DayNumber,
            b.quantityRemaining
        }));
    }

    [HttpPost("drugs/{id:guid}/batches")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> InwardBatch(Guid id, [FromBody] InwardBatchRequest req, CancellationToken ct)
    {
        var drug = await _db.Drugs.FindAsync([id], ct);
        if (drug == null)
            return NotFound(new { error = "drug_not_found" });

        if (string.IsNullOrWhiteSpace(req.BatchNumber) || req.QuantityReceived <= 0 || req.Mrp <= 0)
            return BadRequest(new { error = "invalid_batch_details" });

        if (!DateOnly.TryParse(req.ExpiryDate, out var expiryDate))
            return BadRequest(new { error = "invalid_expiry_date" });

        DateOnly? mfgDate = null;
        if (!string.IsNullOrWhiteSpace(req.MfgDate) && DateOnly.TryParse(req.MfgDate, out var parsedMfg))
        {
            mfgDate = parsedMfg;
        }

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        var batch = new DrugBatch
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = id,
            BatchNumber = req.BatchNumber.Trim().ToUpperInvariant(),
            ExpiryDate = expiryDate,
            MfgDate = mfgDate,
            QuantityReceived = req.QuantityReceived,
            QuantityRemaining = req.QuantityReceived,
            Mrp = req.Mrp,
            PurchaseRate = req.PurchaseRate,
            SupplierId = req.SupplierId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.DrugBatches.Add(batch);

        // Append to immutable stock movement ledger
        _db.DrugStockMovements.Add(new DrugStockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = id,
            DrugBatchId = batch.Id,
            Quantity = req.QuantityReceived,
            Direction = MovementDirection.In,
            ReferenceType = "StockIn",
            ReferenceId = batch.Id,
            Note = $"Batch inward {batch.BatchNumber}",
            RecordedBy = currentUserId,
            RecordedAt = DateTimeOffset.UtcNow
        });

        drug.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new { id = batch.Id, message = "batch_inwarded" });
    }

    [HttpPost("stock-in")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> StockIn([FromBody] StockInRequest req, CancellationToken ct)
    {
        var drug = await _db.Drugs.FindAsync([req.DrugId], ct);
        if (drug == null)
            return NotFound(new { error = "drug_not_found" });

        if (string.IsNullOrWhiteSpace(req.BatchNumber))
            return BadRequest(new { error = "batch_number_required" });

        if (!DateOnly.TryParse(req.ExpiryDate, out var expiryDate))
            return BadRequest(new { error = "invalid_expiry_date" });

        // Expired stock must never enter the shelf; it cannot be dispensed and hides real stock.
        if (expiryDate < DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(new { error = "expiry_date_in_past" });

        DateOnly? mfgDate = null;
        if (!string.IsNullOrWhiteSpace(req.MfgDate) && DateOnly.TryParse(req.MfgDate, out var parsedMfg))
        {
            mfgDate = parsedMfg;
        }

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        var batch = new DrugBatch
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = req.DrugId,
            BatchNumber = req.BatchNumber.Trim().ToUpperInvariant(),
            ExpiryDate = expiryDate,
            MfgDate = mfgDate,
            QuantityReceived = req.QuantityReceived,
            QuantityRemaining = req.QuantityReceived,
            Mrp = req.Mrp,
            PurchaseRate = req.PurchaseRate,
            PackSize = req.PackSize ?? drug.StandardPackSize,
            BaseUnitQuantity = req.BaseUnitQuantity ?? 1,
            InvoiceNumber = req.SupplierInvoiceNumber,
            InvoiceDate = req.InvoiceDate,
            SupplierLicenceNumber = req.SupplierLicenceNumber?.Trim(),
            SupplierId = req.SupplierId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.DrugBatches.Add(batch);

        // Immutable stock movement ledger row
        _db.DrugStockMovements.Add(new DrugStockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = req.DrugId,
            DrugBatchId = batch.Id,
            Quantity = req.QuantityReceived,
            Direction = MovementDirection.In,
            ReferenceType = "StockIn",
            ReferenceId = batch.Id,
            Note = $"Supplier invoice #{req.SupplierInvoiceNumber ?? "N/A"}",
            RecordedBy = currentUserId,
            RecordedAt = DateTimeOffset.UtcNow
        });

        drug.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            batchId = batch.Id,
            drugId = drug.Id,
            batchNumber = batch.BatchNumber,
            quantityRemaining = batch.QuantityRemaining,
            invoiceNumber = batch.InvoiceNumber,
            invoiceDate = batch.InvoiceDate,
            supplierLicenceNumber = batch.SupplierLicenceNumber,
            message = "stock_in_recorded"
        });
    }

    [HttpPost("batches/{id:guid}/recall")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> RecallBatch(Guid id, [FromBody] BatchRecallRequest req, CancellationToken ct)
    {
        var batch = await _db.DrugBatches.FindAsync([id], ct);
        if (batch == null)
            return NotFound(new { error = "batch_not_found" });

        if (batch.IsRecalled)
            return BadRequest(new { error = "batch_already_recalled" });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        batch.IsRecalled = true;
        batch.RecallReason = req.Reason.Trim();

        _db.DrugStockMovements.Add(new DrugStockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = batch.DrugId,
            DrugBatchId = batch.Id,
            Quantity = batch.QuantityRemaining,
            Direction = MovementDirection.Out,
            ReferenceType = "Recall",
            ReferenceId = batch.Id,
            Note = $"Batch Recall: {req.Reason.Trim()}",
            RecordedBy = currentUserId,
            RecordedAt = DateTimeOffset.UtcNow
        });

        // Recalled stock is out of the dispensable pool; the ledger and QuantityRemaining agree.
        batch.QuantityRemaining = 0;
        batch.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            batchId = batch.Id,
            batchNumber = batch.BatchNumber,
            isRecalled = true,
            recallReason = batch.RecallReason,
            message = "batch_marked_recalled"
        });
    }

    [HttpPost("batches/{id:guid}/adjust")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> AdjustBatchStock(Guid id, [FromBody] StockAdjustmentRequest req, CancellationToken ct)
    {
        var batch = await _db.DrugBatches.FindAsync([id], ct);
        if (batch == null)
            return NotFound(new { error = "batch_not_found" });

        if (batch.IsRecalled)
            return BadRequest(new { error = "batch_recalled", message = "Recalled stock cannot be adjusted back into the dispensable pool." });

        if (batch.QuantityRemaining + req.AdjustmentQuantity < 0)
            return BadRequest(new { error = "insufficient_stock_for_adjustment", currentQuantity = batch.QuantityRemaining });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        batch.QuantityRemaining += req.AdjustmentQuantity;

        var direction = req.AdjustmentQuantity >= 0 ? MovementDirection.In : MovementDirection.Out;
        var refType = req.AdjustmentQuantity < 0 ? "WriteOff" : "Adjustment";

        _db.DrugStockMovements.Add(new DrugStockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = batch.DrugId,
            DrugBatchId = batch.Id,
            Quantity = Math.Abs(req.AdjustmentQuantity),
            Direction = direction,
            ReferenceType = refType,
            ReferenceId = batch.Id,
            Note = $"{req.Reason.Trim()} (Approved: {req.ApprovedBy ?? "ClinicAdmin"})",
            RecordedBy = currentUserId,
            RecordedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            batchId = batch.Id,
            batchNumber = batch.BatchNumber,
            adjustedBy = req.AdjustmentQuantity,
            newQuantity = batch.QuantityRemaining,
            message = "stock_adjustment_recorded"
        });
    }

    [HttpGet("stock-movements")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetStockMovements([FromQuery] Guid? drugId, [FromQuery] Guid? batchId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;

        var q = _db.DrugStockMovements
            .Include(m => m.Drug)
            .Include(m => m.DrugBatch)
            .AsNoTracking()
            .Where(m => tenantId == Guid.Empty || m.TenantId == tenantId);

        if (drugId.HasValue)
            q = q.Where(m => m.DrugId == drugId.Value);

        if (batchId.HasValue)
            q = q.Where(m => m.DrugBatchId == batchId.Value);

        var movements = await q
            .OrderByDescending(m => m.RecordedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(m => new
            {
                id = m.Id,
                drugName = m.Drug.Name,
                batchNumber = m.DrugBatch.BatchNumber,
                quantity = m.Quantity,
                direction = m.Direction.ToString(),
                referenceType = m.ReferenceType,
                referenceId = m.ReferenceId,
                note = m.Note,
                recordedAt = m.RecordedAt,
                recordedBy = m.RecordedBy
            })
            .ToListAsync(ct);

        return Ok(movements);
    }

    [HttpPost("purchase-orders/{id:guid}/receive")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> ReceivePurchaseOrder(Guid id, [FromBody] ReceivePurchaseOrderRequest req, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Supplier)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (po == null)
            return NotFound(new { error = "purchase_order_not_found" });

        if (po.Status == PurchaseOrderStatus.Received)
            return BadRequest(new { error = "already_received" });

        if (po.Status == PurchaseOrderStatus.Cancelled)
            return BadRequest(new { error = "cannot_receive_cancelled_order" });

        var items = System.Text.Json.JsonSerializer.Deserialize<List<PoReceiveItem>>(po.ItemsJson);
        if (items == null || items.Count == 0)
            return BadRequest(new { error = "no_items_in_purchase_order" });

        var receivedItems = items
            .Where(item => req.ReceivedQuantities.TryGetValue(item.DrugId, out var quantity) && quantity > 0)
            .ToList();
        if (receivedItems.Count == 0)
            return BadRequest(new { error = "no_items_received" });

        foreach (var item in receivedItems)
        {
            if (req.Mrps?.TryGetValue(item.DrugId, out var mrp) != true || mrp <= 0)
                return BadRequest(new { error = "mrp_required", drugId = item.DrugId });

            if (req.PurchaseRates?.TryGetValue(item.DrugId, out var purchaseRate) == true && purchaseRate <= 0)
                return BadRequest(new { error = "invalid_purchase_rate", drugId = item.DrugId });

            if (req.PurchaseRates?.ContainsKey(item.DrugId) != true && item.UnitPrice <= 0)
                return BadRequest(new { error = "purchase_rate_required", drugId = item.DrugId });
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var createdBatches = new List<object>();

            foreach (var item in items)
            {
                var receivedQty = req.ReceivedQuantities.TryGetValue(item.DrugId, out var qty) ? qty : 0;
                if (receivedQty <= 0)
                    continue;

                var drug = await _db.Drugs.FindAsync([item.DrugId], ct);
                if (drug == null)
                    continue;

                var batchNumber = req.BatchNumbers?.TryGetValue(item.DrugId, out var bn) == true && !string.IsNullOrWhiteSpace(bn)
                    ? bn.Trim().ToUpperInvariant()
                    : $"PO-{id:N}-{item.DrugId:N}".Substring(0, Math.Min(20, $"PO-{id:N}-{item.DrugId:N}".Length));

                var expiryDate = req.ExpiryDates?.TryGetValue(item.DrugId, out var exp) == true && DateOnly.TryParse(exp, out var parsedExp)
                    ? parsedExp
                    : today.AddMonths(12);

                var mfgDate = req.MfgDates?.TryGetValue(item.DrugId, out var mfg) == true && DateOnly.TryParse(mfg, out var parsedMfg)
                    ? parsedMfg
                    : today.AddMonths(-1);

                var mrp = req.Mrps![item.DrugId];
                var purchaseRate = req.PurchaseRates?.TryGetValue(item.DrugId, out var prVal) == true
                    ? prVal
                    : item.UnitPrice;

                var batch = new DrugBatch
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DrugId = item.DrugId,
                    BatchNumber = batchNumber,
                    ExpiryDate = expiryDate,
                    MfgDate = mfgDate,
                    QuantityReceived = receivedQty,
                    QuantityRemaining = receivedQty,
                    Mrp = mrp,
                    PurchaseRate = purchaseRate,
                    SupplierId = po.SupplierId,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                _db.DrugBatches.Add(batch);

                // Movement ledger
                _db.DrugStockMovements.Add(new DrugStockMovement
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DrugId = item.DrugId,
                    DrugBatchId = batch.Id,
                    Quantity = receivedQty,
                    Direction = MovementDirection.In,
                    ReferenceType = "StockIn",
                    ReferenceId = po.Id,
                    Note = $"PO #{po.OrderNumber}",
                    RecordedBy = currentUserId,
                    RecordedAt = DateTimeOffset.UtcNow
                });

                drug.UpdatedAt = DateTimeOffset.UtcNow;

                createdBatches.Add(new
                {
                    batchId = batch.Id,
                    drugId = item.DrugId,
                    drugName = drug.Name,
                    batchNumber = batch.BatchNumber,
                    expiryDate = batch.ExpiryDate.ToString("yyyy-MM-dd"),
                    quantityReceived = batch.QuantityReceived,
                    mrp = batch.Mrp,
                    purchaseRate = batch.PurchaseRate
                });
            }

            po.Status = PurchaseOrderStatus.Received;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return StatusCode(201, new
            {
                purchaseOrderId = po.Id,
                orderNumber = po.OrderNumber,
                status = po.Status.ToString().ToLower(),
                receivedBatches = createdBatches
            });
        });
    }
}
