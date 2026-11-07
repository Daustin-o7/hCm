using System.Data;
using System.Text;
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
public class PharmacyComplianceController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<PharmacyComplianceController> _logger;

    public PharmacyComplianceController(
        HospitalCrmDbContext db,
        ILogger<PharmacyComplianceController> logger)
    {
        _db = db;
        _logger = logger;
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

    private static BadRequestObjectResult NdpsBlocked() => new(new
    {
        error = "ndps_digital_dispensing_prohibited",
        message = "NDPS drugs are legally prohibited from digital retail dispensing under NDPS Rules. Physical triplicate official register required."
    });

    /// <summary>
    /// Item → catalogue drug: explicit id first, then exact brand/medicine text, then exact
    /// generic + strength + form. A mismatch is refused; nothing is guessed.
    /// </summary>
    private static Drug? ResolveDrug(PrescriptionItem rxItem, IReadOnlyCollection<Drug> candidates)
    {
        if (rxItem.DrugId is { } id) return candidates.FirstOrDefault(d => d.Id == id);

        var byText = candidates.FirstOrDefault(d =>
            string.Equals(d.Name, rxItem.MedicineText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byText != null) return byText;

        return candidates.FirstOrDefault(d =>
            string.Equals(d.GenericName, rxItem.GenericName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Strength, rxItem.Strength.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.DosageForm, rxItem.DosageForm.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(rxItem.BrandName) ||
             string.Equals(d.Name, rxItem.BrandName.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    [HttpPost("dispense")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor")]
    public async Task<IActionResult> DispensePrescription([FromBody] DispensePrescriptionRequest req, CancellationToken ct)
    {
        var callerTenant = User.GetTenantId();
        var callerId = User.GetUserId();
        if (!callerTenant.HasValue || !callerId.HasValue) return Unauthorized(new { error = "invalid_token" });
        var tenantId = callerTenant.Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentUserId = callerId.Value;

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync<IActionResult>(async () =>
            {
                await using var transaction = _db.Database.IsRelational()
                    ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;

                if (await GetLicensedClinicAsync(ct) is null)
                    return StatusCode(403, new { error = "pharmacy_licence_missing", message = "Clinic must register Form 20 or Form 21 Retail Drug Licence before dispensing medications." });
                if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 128)
                    return BadRequest(new { error = "idempotency_key_required", message = "Use the same key when retrying this supply." });
                if (req.ItemSelections is { } input && (input.Count is < 1 or > 50
                    || input.Any(s => s is null || s.PrescriptionItemId == Guid.Empty || s.Quantity is < 1 or > 1000 || s.SubstitutionReason?.Length > 500)))
                    return BadRequest(new { error = "invalid_prescription_item_selection" });

                var requestHash = PharmacyCommon.RequestHash(req);
                var existing = await _db.DispenseRecords.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.IdempotencyKey == req.IdempotencyKey, ct);
                if (existing != null)
                {
                    if (existing.RequestHash != requestHash)
                        return Conflict(new { error = "idempotency_key_conflict", message = "This key belongs to a different supply payload, or a historical supply without saved retry evidence. Review the original dispense; do not submit it as a new supply." });
                    if (existing.InvoiceId is null || existing.ResultJson is null)
                        return Conflict(new { error = "saved_dispense_outcome_unavailable" });
                    return Ok(JsonSerializer.Deserialize<JsonElement>(existing.ResultJson));
                }

                // Read fulfillment inside the transaction; amendment and dispense share the encounter token.
                var prescription = await _db.Prescriptions
                    .Include(p => p.Items)
                    .Include(p => p.Consultation).ThenInclude(c => c.Doctor)
                    .Include(p => p.Consultation).ThenInclude(c => c.Appointment).ThenInclude(a => a.Patient)
                    .FirstOrDefaultAsync(p => p.Id == req.PrescriptionId && p.Consultation.Appointment.Patient.TenantId == tenantId, ct);
                if (prescription is null) return NotFound(new { error = "prescription_not_found" });

                if (prescription.Consultation.Status == ConsultationStatus.Draft)
                    return BadRequest(new { error = "cannot_dispense_draft_prescription", message = "Prescription must be finalized by doctor before dispensing." });
                if (await _db.Consultations.AnyAsync(c => c.AppointmentId == prescription.Consultation.AppointmentId && c.Version > prescription.Consultation.Version, ct))
                    return Conflict(new { error = "prescription_superseded", message = "Dispense only the latest signed prescription." });

                var patientName = req.PatientName ?? prescription.Consultation?.Appointment?.Patient?.Name ?? "Walk-in Patient";
                var patientAddress = req.PatientAddress ?? "Local";

                if (req.ItemSelections is { } selections && (selections.Count == 0 ||
                    selections.Select(s => s.PrescriptionItemId).Distinct().Count() != selections.Count ||
                    selections.Any(s => !prescription.Items.Any(i => i.Id == s.PrescriptionItemId))))
                    return BadRequest(new { error = "invalid_prescription_item_selection" });
                if (prescription.Items.Any(i => i.TotalQuantity < 1 || i.DispensedQuantity < 0 || i.DispensedQuantity > i.TotalQuantity))
                    return Conflict(new { error = "prescription_fulfillment_inconsistent", message = "Reconcile supplied quantities before dispensing." });

                var prescriptionItems = prescription.Items
                    .Where(i => req.ItemSelections != null ? req.ItemSelections.Any(s => s.PrescriptionItemId == i.Id) : i.TotalQuantity > i.DispensedQuantity)
                    .ToList();
                if (prescriptionItems.Count == 0) return BadRequest(new { error = "prescription_already_dispensed" });
                var drugIds = prescriptionItems.Where(i => i.DrugId.HasValue).Select(i => i.DrugId!.Value).Distinct().ToHashSet();
                var medicineTexts = prescriptionItems.Where(i => !string.IsNullOrWhiteSpace(i.MedicineText)).Select(i => i.MedicineText.Trim().ToLower()).Distinct().ToHashSet();
                var genericNames = prescriptionItems.Where(i => !string.IsNullOrWhiteSpace(i.GenericName)).Select(i => i.GenericName.Trim().ToLower()).Distinct().ToHashSet();

                var allTenantDrugs = await _db.Drugs
                    .Include(d => d.Batches)
                    .Where(d => d.TenantId == tenantId)
                    .ToListAsync(ct);

                var candidateDrugs = allTenantDrugs
                    .Where(d => drugIds.Contains(d.Id)
                        || medicineTexts.Contains(d.Name.Trim().ToLower())
                        || genericNames.Contains(d.GenericName.Trim().ToLower()))
                    .ToList();

                // 2. NDPS hard block. The prescription's own schedule is authoritative, so a line
                // carrying NDPS is refused even when no catalogue row can be matched.
                if (prescription.Items.Any(i => i.ScheduleClass == ScheduleClass.NDPS))
                    return NdpsBlocked();

                var rxTargets = new List<(PrescriptionItem RxItem, Drug Drug)>();
                foreach (var rxItem in prescriptionItems)
                {
                    var targetDrug = ResolveDrug(rxItem, candidateDrugs);
                    if (targetDrug is null)
                    {
                        return BadRequest(new
                        {
                            error = "drug_not_matched",
                            message = $"'{rxItem.MedicineText}' is not in the pharmacy catalogue. Curate the medicine before dispensing.",
                            prescriptionItemId = rxItem.Id
                        });
                    }

                    if (targetDrug.ScheduleClass == ScheduleClass.NDPS)
                        return NdpsBlocked();

                    var itemSelection = req.ItemSelections?.FirstOrDefault(s => s.PrescriptionItemId == rxItem.Id);

                    // Substitution is optional and must stay same generic + strength + form.
                    if (itemSelection?.SubstitutedDrugId is { } substitutedId && substitutedId != targetDrug.Id)
                    {
                        var substitutedDrug = candidateDrugs.FirstOrDefault(d => d.Id == substitutedId)
                            ?? await _db.Drugs.Include(d => d.Batches).FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == substitutedId, ct);

                        if (substitutedDrug is null)
                            return BadRequest(new { error = "substitution_drug_not_found" });

                        if (!string.Equals(targetDrug.GenericName, substitutedDrug.GenericName, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(targetDrug.Strength, substitutedDrug.Strength, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(targetDrug.DosageForm, substitutedDrug.DosageForm, StringComparison.OrdinalIgnoreCase))
                        {
                            return BadRequest(new
                            {
                                error = "substitution_generic_mismatch",
                                message = $"Substitution permitted only for same generic, strength and form '{targetDrug.GenericName} {targetDrug.Strength}'. Therapeutic substitution requires doctor amendment."
                            });
                        }

                        if (substitutedDrug.ScheduleClass == ScheduleClass.NDPS)
                            return NdpsBlocked();

                        targetDrug = substitutedDrug;
                    }

                    rxTargets.Add((rxItem, targetDrug));
                }

                var plan = new List<(PrescriptionItem RxItem, Drug Drug, DrugBatch Batch, int Quantity, DispenseItemSelection? Selection)>();
                var allocated = new Dictionary<Guid, int>();
                var prescriberName = prescription.Consultation?.Doctor?.Name;
                var prescriberRegNo = prescription.Consultation?.DoctorRegistrationNumber;
                // Validate every line and shared-batch allowance before touching tracked stock or fulfillment.
                foreach (var (rxItem, targetDrug) in rxTargets)
                {
                    var itemSelection = req.ItemSelections?.FirstOrDefault(s => s.PrescriptionItemId == rxItem.Id);

                    var remaining = rxItem.TotalQuantity - rxItem.DispensedQuantity;
                    if (remaining == 0)
                        return BadRequest(new { error = "prescription_item_already_dispensed", prescriptionItemId = rxItem.Id });

                    var dispenseQty = itemSelection?.Quantity ?? remaining;
                    if (dispenseQty < 1 || dispenseQty > remaining)
                    {
                        return BadRequest(new
                        {
                            error = "dispense_quantity_exceeds_prescribed",
                            prescriptionItemId = rxItem.Id,
                            prescribed = rxItem.TotalQuantity,
                            alreadyDispensed = rxItem.DispensedQuantity,
                            requested = dispenseQty
                        });
                    }

                    // FEFO selection excluding recalled and expired batches; an explicit batch must
                    // belong to the resolved drug.
                    DrugBatch? availableBatch = null;
                    if (itemSelection?.DrugBatchId is { } batchId && batchId != Guid.Empty)
                    {
                        availableBatch = targetDrug.Batches.FirstOrDefault(b =>
                            b.Id == batchId && !b.IsRecalled && b.ExpiryDate >= today && b.QuantityRemaining > 0);
                        if (availableBatch is null) return BadRequest(new { error = "invalid_dispense_batch", prescriptionItemId = rxItem.Id });
                    }

                    if (availableBatch == null)
                    {
                        availableBatch = targetDrug.Batches
                            .Where(b => b.QuantityRemaining - allocated.GetValueOrDefault(b.Id) >= dispenseQty && b.ExpiryDate >= today && !b.IsRecalled)
                            .OrderBy(b => b.ExpiryDate)
                            .FirstOrDefault();
                    }

                    if (availableBatch == null)
                    {
                        return BadRequest(new { error = $"no_active_batch_in_stock: {targetDrug.Name}", drugName = targetDrug.Name });
                    }

                    // SEC-006: Guard against driving batch stock negative
                    var available = availableBatch.QuantityRemaining - allocated.GetValueOrDefault(availableBatch.Id);
                    if (available < dispenseQty)
                    {
                        return BadRequest(new { error = $"insufficient_stock_for_drug: {targetDrug.Name}", drugName = targetDrug.Name, available, requested = dispenseQty });
                    }

                    if (targetDrug.ScheduleClass is ScheduleClass.ScheduleH1 or ScheduleClass.ScheduleX)
                    {
                        if (string.IsNullOrWhiteSpace(prescriberName) || string.IsNullOrWhiteSpace(prescriberRegNo))
                        {
                            return BadRequest(new
                            {
                                error = "schedule_h1_prescriber_required",
                                message = "The signed consultation has no prescribing doctor registration number; amend or re-sign before dispensing Schedule H1/X drugs."
                            });
                        }
                    }
                    if (availableBatch.Mrp <= 0 || availableBatch.Mrp > 500000m || decimal.Round(availableBatch.Mrp, 2) != availableBatch.Mrp
                        || targetDrug.GstRate is < 0 or > 28 || decimal.Round(targetDrug.GstRate, 2) != targetDrug.GstRate)
                        return BadRequest(new { error = "invalid_catalogue_price_or_tax", drugId = targetDrug.Id });
                    allocated[availableBatch.Id] = allocated.GetValueOrDefault(availableBatch.Id) + dispenseQty;
                    plan.Add((rxItem, targetDrug, availableBatch, dispenseQty, itemSelection));
                }
                if (plan.Sum(p => p.Batch.Mrp * p.Quantity) > 1000000m)
                    return BadRequest(new { error = "dispense_total_limit" });

                var dispenseRecord = new DispenseRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PrescriptionId = prescription.Id,
                    PatientId = prescription.Consultation?.Appointment?.PatientId,
                    WalkInCustomerName = req.PatientName,
                    DispensedBy = currentUserId,
                    DispensedAt = DateTimeOffset.UtcNow,
                    IdempotencyKey = req.IdempotencyKey,
                    RequestHash = requestHash
                };
                _db.DispenseRecords.Add(dispenseRecord);
                decimal totalAmount = 0m;
                decimal pharmSubtotal = 0m;
                decimal pharmTotalGst = 0m;
                var invoiceLineItems = new List<InvoiceLineItem>();
                foreach (var (rxItem, targetDrug, availableBatch, dispenseQty, itemSelection) in plan)
                {
                    if (itemSelection?.SubstitutedDrugId is { } substitutedId)
                    {
                        rxItem.SubstitutedDrugId = substitutedId;
                        rxItem.SubstitutionReason = itemSelection.SubstitutionReason ?? "Pharmacist generic substitution";
                    }

                    availableBatch.QuantityRemaining -= dispenseQty;
                    availableBatch.UpdatedAt = DateTimeOffset.UtcNow;

                    // Append-only stock movement ledger
                    _db.DrugStockMovements.Add(new DrugStockMovement
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        DrugId = targetDrug.Id,
                        DrugBatchId = availableBatch.Id,
                        Quantity = dispenseQty,
                        Direction = MovementDirection.Out,
                        ReferenceType = "Dispense",
                        ReferenceId = dispenseRecord.Id,
                        Note = $"Dispensed for Rx #{prescription.Id:N}",
                        RecordedBy = currentUserId,
                        RecordedAt = DateTimeOffset.UtcNow
                    });

                    // Update prescription item dispensed state
                    rxItem.DispensedQuantity += dispenseQty;
                    rxItem.IsDispensed = rxItem.DispensedQuantity == rxItem.TotalQuantity;
                    rxItem.DispensedBatchId = availableBatch.Id;

                    dispenseRecord.Items.Add(new DispenseItem
                    {
                        Id = Guid.NewGuid(),
                        DispenseRecordId = dispenseRecord.Id,
                        PrescriptionItemId = rxItem.Id,
                        DrugBatchId = availableBatch.Id,
                        Quantity = dispenseQty,
                        UnitPrice = availableBatch.Mrp
                    });

                    // Indian Pharma MRP-Inclusive Reverse GST Calculation
                    var lineGross = availableBatch.Mrp * dispenseQty;
                    var lineTaxable = decimal.Round(lineGross / (1m + (targetDrug.GstRate / 100m)), 2, MidpointRounding.AwayFromZero);
                    var lineGst = lineGross - lineTaxable;

                    pharmSubtotal += lineTaxable;
                    pharmTotalGst += lineGst;
                    totalAmount += lineGross;

                    invoiceLineItems.Add(new InvoiceLineItem
                    {
                        Id = Guid.NewGuid(),
                        Description = $"{targetDrug.Name} (Batch: {availableBatch.BatchNumber})",
                        Quantity = dispenseQty,
                        UnitPrice = availableBatch.Mrp,
                        Amount = lineTaxable,
                        TaxAmount = lineGst,
                        GstRate = targetDrug.GstRate,
                        HsnSacCode = targetDrug.HsnCode,
                        HsnCode = targetDrug.HsnCode,
                        DrugBatchId = availableBatch.Id,
                        TaxCategory = targetDrug.GstRate > 0 ? TaxCategory.Taxable : TaxCategory.Exempt
                    });

                    if (targetDrug.ScheduleClass is ScheduleClass.ScheduleH1 or ScheduleClass.ScheduleX)
                    {
                        var dispenserUser = await _db.Users.FindAsync([currentUserId], ct);
                        var dispenserName = dispenserUser?.Name ?? "Pharmacist";

                        _db.ControlledSubstanceRegisters.Add(new ControlledSubstanceRegister
                        {
                            Id = Guid.NewGuid(),
                            TenantId = tenantId,
                            DispenseRecordId = dispenseRecord.Id,
                            DrugId = targetDrug.Id,
                            ScheduleClass = targetDrug.ScheduleClass,
                            DrugName = targetDrug.Name,
                            BatchNumber = availableBatch.BatchNumber,
                            Quantity = dispenseQty,
                            PatientName = patientName,
                            PatientAddress = patientAddress,
                            PrescriberName = prescriberName!,
                            PrescriberRegNo = prescriberRegNo!,
                            DispensedBy = currentUserId,
                            DispenserName = dispenserName,
                            DispensedAt = DateTimeOffset.UtcNow
                        });
                    }
                }

                var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);
                var maxInvoiceNumber = await _db.Invoices
                    .Where(i => i.TenantId == tenantId && i.FinancialYear == currentFy && i.Series == "PHARM")
                    .MaxAsync(i => (int?)i.InvoiceNumber, ct) ?? 0;

                var pharmInvoice = new Invoice
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    AppointmentId = prescription.Consultation?.AppointmentId,
                    PatientId = prescription.Consultation?.Appointment?.PatientId,
                    InvoiceType = InvoiceType.Pharmacy,
                    Series = "PHARM",
                    FinancialYear = currentFy,
                    DocumentType = pharmTotalGst > 0 ? InvoiceDocumentType.TaxInvoice : InvoiceDocumentType.BillOfSupply,
                    WalkInCustomerName = req.PatientName ?? prescription.Consultation?.Appointment?.Patient?.Name,
                    WalkInCustomerPhone = prescription.Consultation?.Appointment?.Patient?.Phone,
                    InvoiceNumber = maxInvoiceNumber + 1,
                    Subtotal = pharmSubtotal,
                    GstAmount = pharmTotalGst,
                    Total = totalAmount,
                    PaidAmount = 0.0m,
                    BalanceDue = totalAmount,
                    Status = InvoiceStatus.Finalized,
                    FinalizedAt = DateTimeOffset.UtcNow,
                    FinalizedByUserId = currentUserId,
                    IdempotencyKey = $"pharm_inv_{req.IdempotencyKey}",
                    CreatedAt = DateTimeOffset.UtcNow
                };

                foreach (var li in invoiceLineItems)
                {
                    li.InvoiceId = pharmInvoice.Id;
                    pharmInvoice.LineItems.Add(li);
                }
                _db.Invoices.Add(pharmInvoice);
                dispenseRecord.InvoiceId = pharmInvoice.Id;

                var pharmInvoiceRef = pharmInvoice.FormatInvoiceNumber();

                InvoiceAccounting.PostFinalization(_db, pharmInvoice, currentUserId);

                // Link to consultation invoice if appointment has one
                if (prescription.Consultation?.AppointmentId is { } apptId)
                {
                    var consultationInvoice = await _db.Invoices
                        .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.AppointmentId == apptId && i.Series == "INV", ct);
                    if (consultationInvoice != null)
                    {
                        consultationInvoice.LinkedPharmacyInvoiceId = pharmInvoice.Id;
                    }
                }

                prescription.Consultation!.Appointment.UpdatedAt = DateTimeOffset.UtcNow;
                var result = new
                {
                    idempotencyKey = req.IdempotencyKey,
                    dispenseId = dispenseRecord.Id,
                    invoiceId = pharmInvoice.Id,
                    invoiceNumber = pharmInvoiceRef,
                    prescriptionId = prescription.Id,
                    patientName,
                    subtotal = pharmSubtotal,
                    gstAmount = pharmTotalGst,
                    totalAmount = totalAmount,
                    paidAmount = pharmInvoice.PaidAmount,
                    balanceDue = pharmInvoice.BalanceDue,
                    status = "dispensed",
                    invoiceStatus = pharmInvoice.Status.ToString(),
                    createdAt = dispenseRecord.DispensedAt,
                    itemCount = dispenseRecord.Items.Count,
                    items = plan.Select(p => new
                    {
                        prescriptionItemId = p.RxItem.Id,
                        drugId = p.Drug.Id,
                        drugBatchId = p.Batch.Id,
                        batchNumber = p.Batch.BatchNumber,
                        description = p.Drug.Name,
                        quantity = p.Quantity,
                        unitPrice = p.Batch.Mrp,
                        dispensedQuantity = p.RxItem.DispensedQuantity,
                        remainingQuantity = p.RxItem.TotalQuantity - p.RxItem.DispensedQuantity,
                        substitutedDrugId = p.Selection?.SubstitutedDrugId,
                        substitutionReason = p.Selection?.SubstitutionReason
                    }).ToArray()
                };
                dispenseRecord.ResultJson = JsonSerializer.Serialize(result);
                await _db.SaveChangesAsync(ct);
                if (transaction != null) await transaction.CommitAsync(ct);
                return StatusCode(201, result);
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "dispense_changed_concurrently", message = "Reload the active prescription and stock before retrying with the same idempotency key." });
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation() || ex.InnerException is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "dispense_changed_concurrently", message = "Reload the active prescription and stock before retrying with the same idempotency key." });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState is "40001" or "40P01")
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "dispense_changed_concurrently", message = "Reload the active prescription and stock before retrying with the same idempotency key." });
        }
    }

    /// <summary>
    /// Fetches a finalized prescription with line items, patient demographic, and doctor registration details
    /// for review and batch selection prior to dispensing.
    /// </summary>
    [HttpGet("prescriptions/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor")]
    public async Task<IActionResult> GetPrescriptionForDispense(Guid id, CancellationToken ct)
    {
        var tenantId = TenantId;
        var prescription = await _db.Prescriptions
            .Include(p => p.Items)
            .Include(p => p.Consultation)
                .ThenInclude(c => c!.Doctor)
            .Include(p => p.Consultation)
                .ThenInclude(c => c!.Appointment)
                    .ThenInclude(a => a!.Patient)
            .FirstOrDefaultAsync(p => p.Id == id
                && p.Consultation != null
                && p.Consultation.Appointment != null
                && p.Consultation.Appointment.Patient != null
                && p.Consultation.Appointment.Patient.TenantId == tenantId, ct);

        if (prescription == null)
            return NotFound(new { error = "prescription_not_found" });

        if (prescription.Consultation != null && prescription.Consultation.Status == ConsultationStatus.Draft)
            return BadRequest(new { error = "cannot_dispense_draft_prescription", message = "Prescription must be finalized by doctor before dispensing." });
        if (await _db.Consultations.AnyAsync(c => c.AppointmentId == prescription.Consultation!.AppointmentId && c.Version > prescription.Consultation.Version, ct))
            return Conflict(new { error = "prescription_superseded", message = "Dispense only the latest signed prescription." });

        return Ok(new
        {
            prescriptionId = prescription.Id,
            consultationId = prescription.ConsultationId,
            patientName = prescription.Consultation?.Appointment?.Patient?.Name ?? "Walk-in Patient",
            patientPhone = prescription.Consultation?.Appointment?.Patient?.Phone,
            doctorName = prescription.Consultation?.Doctor?.Name ?? "Treating Doctor",
            doctorRegNo = prescription.Consultation?.DoctorRegistrationNumber ?? "",
            createdAt = prescription.CreatedAt,
            items = prescription.Items.Select(i => new
            {
                id = i.Id,
                drugId = i.DrugId,
                genericName = i.GenericName,
                brandName = i.BrandName,
                dosageForm = i.DosageForm,
                strength = i.Strength,
                route = i.Route,
                frequency = i.Frequency,
                duration = i.Duration,
                durationUnit = i.DurationUnit,
                totalQuantity = i.TotalQuantity,
                scheduleClass = i.ScheduleClass.ToString(),
                isDispensed = i.DispensedQuantity >= i.TotalQuantity,
                dispensedQuantity = i.DispensedQuantity,
                remainingQuantity = Math.Max(0, i.TotalQuantity - i.DispensedQuantity)
            })
        });
    }

    /// <summary>
    /// Lists recent pending prescriptions available for pharmacy dispensing.
    /// </summary>
    [HttpGet("prescriptions/pending")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor")]
    public async Task<IActionResult> GetPendingPrescriptions([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var tenantId = TenantId;
        var pending = await _db.Prescriptions
            .Include(p => p.Items)
            .Include(p => p.Consultation)
                .ThenInclude(c => c!.Doctor)
            .Include(p => p.Consultation)
                .ThenInclude(c => c!.Appointment)
                    .ThenInclude(a => a!.Patient)
            .Where(p => p.Consultation != null &&
                        p.Consultation.Status != ConsultationStatus.Draft &&
                        !_db.Consultations.Any(c => c.AppointmentId == p.Consultation.AppointmentId && c.Version > p.Consultation.Version) &&
                        p.Items.Any(i => i.TotalQuantity > i.DispensedQuantity) &&
                        p.Consultation.Appointment != null &&
                        p.Consultation.Appointment.Patient != null &&
                        p.Consultation.Appointment.Patient.TenantId == tenantId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(p => new
            {
                prescriptionId = p.Id,
                patientName = p.Consultation!.Appointment!.Patient.Name,
                patientPhone = p.Consultation.Appointment.Patient.Phone,
                doctorName = p.Consultation.Doctor.Name,
                itemCount = p.Items.Count,
                pendingItemCount = p.Items.Count(i => i.TotalQuantity > i.DispensedQuantity),
                createdAt = p.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(pending);
    }

    [HttpGet("compliance/register")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist", "Doctor")]
    public async Task<IActionResult> GetComplianceRegister(
        [FromQuery] string? schedule,
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] bool exportCsv = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
    {
        var tenantId = TenantId;

        var q = _db.ControlledSubstanceRegisters
            .Include(r => r.Drug)
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(schedule) && Enum.TryParse<ScheduleClass>(schedule, true, out var schedClass))
        {
            q = q.Where(r => r.ScheduleClass == schedClass);
        }

        if (!string.IsNullOrWhiteSpace(startDate) && DateTimeOffset.TryParse(startDate, out var start))
        {
            q = q.Where(r => r.DispensedAt >= start.ToUniversalTime());
        }

        if (!string.IsNullOrWhiteSpace(endDate) && DateTimeOffset.TryParse(endDate, out var end))
        {
            q = q.Where(r => r.DispensedAt <= end.ToUniversalTime());
        }

        var totalCount = exportCsv ? 0 : await q.CountAsync(ct);
        var records = exportCsv
            ? await q.OrderByDescending(r => r.DispensedAt).ToListAsync(ct)
            : await q.OrderByDescending(r => r.DispensedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        if (exportCsv)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ID,Date,ScheduleClass,DrugName,GenericName,BatchNumber,Quantity,PatientName,PatientAddress,PrescribingDoctor,DoctorRegNo,DispensedBy");
            foreach (var r in records)
            {
                sb.AppendLine($"\"{r.Id}\",\"{r.DispensedAt:yyyy-MM-dd HH:mm}\",\"{r.ScheduleClass}\",\"{PharmacyCommon.EscapeCsv(r.DrugName)}\",\"{PharmacyCommon.EscapeCsv(r.Drug?.GenericName ?? string.Empty)}\",\"{r.BatchNumber}\",{r.Quantity},\"{PharmacyCommon.EscapeCsv(r.PatientName)}\",\"{PharmacyCommon.EscapeCsv(r.PatientAddress)}\",\"{PharmacyCommon.EscapeCsv(r.PrescriberName)}\",\"{r.PrescriberRegNo}\",\"{PharmacyCommon.EscapeCsv(r.DispenserName)}\"");
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", $"statutory_h1_register_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        return Ok(new
        {
            total = totalCount,
            page,
            pageSize,
            data = records.Select(r => new
            {
                id = r.Id,
                scheduleClass = r.ScheduleClass.ToString(),
                drugName = r.DrugName,
                genericName = r.Drug != null ? r.Drug.GenericName : null,
                batchNumber = r.BatchNumber,
                quantity = r.Quantity,
                patientName = r.PatientName,
                patientAddress = r.PatientAddress,
                prescriberName = r.PrescriberName,
                prescriberRegNo = r.PrescriberRegNo,
                dispenserName = r.DispenserName,
                dispensedAt = r.DispensedAt
            })
        });
    }

    [HttpGet("suppliers")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> GetSuppliers(CancellationToken ct)
    {
        var suppliers = await _db.Suppliers.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name, gstin = s.Gstin, phone = s.Phone, email = s.Email, address = s.Address })
            .ToListAsync(ct);
        return Ok(suppliers);
    }

    [HttpPost("suppliers")]
    [AuthorizeRoles("ClinicAdmin", "Pharmacist")]
    public async Task<IActionResult> CreateSupplier([FromBody] CreateSupplierRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "supplier_name_required" });

        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            TenantId = User.GetTenantId() ?? Guid.Empty,
            Name = req.Name.Trim(),
            Gstin = req.Gstin?.Trim() ?? "",
            Phone = req.Phone?.Trim() ?? "",
            Email = req.Email?.Trim() ?? "",
            Address = req.Address?.Trim() ?? "",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, supplier);
    }
}
