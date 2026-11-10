using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using System.Security.Cryptography;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1")]
[RequireModule(ModuleRegistry.Lab)]
public class LabOrdersController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly LabFileStorage? _files;

    public LabOrdersController(HospitalCrmDbContext db, IConfiguration config, LabFileStorage? files = null)
    {
        _db = db;
        _files = files;
    }

    // ----- FR-08-01: Create Lab Order -----

    [HttpPost("consultations/{id:guid}/lab-orders")]
    [AuthorizeRoles("Doctor")]
    public async Task<IActionResult> CreateOrder(Guid id, [FromBody] CreateLabOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TestName))
            return BadRequest(new { error = "test_name_required" });

        var consultation = await _db.Consultations
            .Include(c => c.Appointment)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (consultation is null)
            return NotFound(new { error = "consultation_not_found" });

        var doctorId = User.GetUserId();
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });
        // FR-08-01 edge case: order stays linked to original consultation ID
        // (not affected by note amendments — we just store the ID, not a copy)
        var order = new LabOrder
        {
            Id = Guid.NewGuid(),
            TenantId = User.GetTenantId() ?? Guid.Empty,
            ConsultationId = consultation.Id,
            PatientId = consultation.Appointment.PatientId,
            DoctorId = doctorId.Value,
            TestName = request.TestName,
            Notes = request.Notes,
            Status = LabOrderStatus.Ordered,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.LabOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = order.Id,
            testName = order.TestName,
            status = order.Status.ToString().ToLower(),
            createdAt = order.CreatedAt
        });
    }

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".dcm", ".csv"
    };

    private const long MaxLabFileSizeBytes = 25 * 1024 * 1024; // 25 MB

    [HttpPost("lab-orders/{id:guid}/collect-sample")]
    [AuthorizeRoles("Nurse", "ClinicAdmin")]
    public async Task<IActionResult> CollectSample(Guid id, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var order = await _db.LabOrders.FirstOrDefaultAsync(o => o.Id == id && o.TenantId == tenantId, ct);
        if (order is null) return NotFound(new { error = "order_not_found" });

        order.Status = LabOrderStatus.SampleCollected;
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = order.Id, status = "sample_collected" });
    }

    // ----- FR-08-02: Enter/Upload Lab Result -----

    [HttpPatch("lab-orders/{id:guid}/result")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> EnterResult(
        Guid id,
        [FromForm] string? resultText,
        [FromForm] bool isAbnormal,
        [FromForm] string? referenceRange,
        [FromForm] string? units,
        IFormFile? file,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resultText) && file is null)
            return BadRequest(new { error = "result_text_or_file_required" });

        if (User.Identity?.IsAuthenticated != true || !User.GetUserId().HasValue) return Unauthorized();
        if (!User.GetTenantId().HasValue || User.GetTenantId() == Guid.Empty) return Forbid();
        if (!User.IsInRole("Doctor") && !User.IsInRole("ClinicAdmin") && !User.IsInRole("Owner")) return Forbid();

        if (file is not null)
        {
            if (file.Length <= 0) return BadRequest(new { error = "empty_lab_file" });
            if (file.Length > MaxLabFileSizeBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "file_too_large", maxSizeBytes = MaxLabFileSizeBytes });

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
                return BadRequest(new { error = "file_type_not_allowed", allowedExtensions = AllowedExtensions });
        }

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var order = await _db.LabOrders.FirstOrDefaultAsync(o => o.Id == id && o.TenantId == tenantId, ct);
        if (order is null)
            return NotFound(new { error = "order_not_found" });

        // FR-08-02 edge case: correction = new version, original preserved
        var latest = await _db.LabResults
            .Where(r => r.LabOrderId == id && r.TenantId == tenantId)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct);
        var version = (latest?.Version ?? 0) + 1;

        var resultId = Guid.NewGuid();
        string? fileUrl = null;
        if (file is not null)
        {
            if (_files is null) return StatusCode(503, new { error = "lab_storage_unavailable" });
            try
            {
                fileUrl = await _files.WriteAsync(tenantId, id, resultId, file, ct);
            }
            catch (InvalidDataException) { return BadRequest(new { error = "invalid_lab_file" }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            { return StatusCode(503, new { error = "lab_storage_unavailable" }); }
        }

        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var result = new LabResult
        {
            Id = resultId,
            TenantId = order.TenantId,
            LabOrderId = id,
            ResultText = resultText,
            FileUrl = fileUrl,
            IsAbnormal = isAbnormal,
            ReferenceRange = referenceRange,
            Units = units,
            Version = version,
            PreviousVersionId = latest?.Id,
            EnteredBy = userId.Value,
            EnteredAt = DateTimeOffset.UtcNow
        };
        _db.LabResults.Add(result);
        order.Status = LabOrderStatus.Resulted;
        order.IsAbnormal = isAbnormal;
        order.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            id = result.Id,
            version = result.Version,
            previousVersionId = result.PreviousVersionId,
            resultText = result.ResultText,
            fileUrl = DownloadUrl(result),
            fileExtension = DownloadExtension(result),
            isAbnormal = result.IsAbnormal,
            orderStatus = order.Status.ToString().ToLower()
        });
    }

    [HttpGet("lab-orders/{id:guid}")]
    [AuthorizeRoles("Doctor", "ClinicAdmin", "Nurse")]
    public async Task<IActionResult> GetOrder(Guid id, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var order = await _db.LabOrders
            .AsNoTracking()
            .Include(o => o.Patient)
            .FirstOrDefaultAsync(o => o.Id == id && o.TenantId == tenantId, ct);

        if (order is null) return NotFound(new { error = "order_not_found" });

        // The review step is meaningless without the result, so the detail read returns it.
        var result = await _db.LabResults
            .AsNoTracking()
            .Where(r => r.LabOrderId == id)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct);

        return Ok(new
        {
            id = order.Id,
            consultationId = order.ConsultationId,
            patientId = order.PatientId,
            patientName = order.Patient?.Name,
            doctorId = order.DoctorId,
            testName = order.TestName,
            notes = order.Notes,
            status = order.Status.ToString().ToLower(),
            isAbnormal = order.IsAbnormal,
            doctorReviewedAt = order.DoctorReviewedAt,
            doctorReviewNotes = order.DoctorReviewNotes,
            createdAt = order.CreatedAt,
            completedAt = order.CompletedAt,
            latestResult = result is null ? null : new
            {
                id = result.Id,
                version = result.Version,
                previousVersionId = result.PreviousVersionId,
                resultText = result.ResultText,
                fileUrl = DownloadUrl(result),
                fileExtension = DownloadExtension(result),
                isAbnormal = result.IsAbnormal,
                referenceRange = result.ReferenceRange,
                units = result.Units,
                enteredAt = result.EnteredAt
            }
        });
    }

    private static string? DownloadUrl(LabResult result) => string.IsNullOrEmpty(result.FileUrl)
        ? null : $"/api/v1/lab-orders/{result.LabOrderId}/results/{result.Id}/file";

    private static string? DownloadExtension(LabResult result) =>
        Path.GetExtension(result.FileUrl)?.ToLowerInvariant() is { } extension && AllowedExtensions.Contains(extension)
            ? extension : null;

    [HttpGet("lab-orders/{id:guid}/results/{resultId:guid}/file")]
    [AuthorizeRoles("Doctor", "ClinicAdmin", "Nurse")]
    public async Task<IActionResult> DownloadResult(Guid id, Guid resultId, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true || !User.GetUserId().HasValue) return Unauthorized();
        var tenantId = User.GetTenantId();
        if (!tenantId.HasValue || tenantId == Guid.Empty) return Forbid();
        if (!User.IsInRole("Doctor") && !User.IsInRole("ClinicAdmin") && !User.IsInRole("Owner") && !User.IsInRole("Nurse"))
            return Forbid();
        var result = await _db.LabResults.AsNoTracking().FirstOrDefaultAsync(r =>
            r.Id == resultId && r.LabOrderId == id && r.TenantId == tenantId &&
            _db.LabOrders.Any(o => o.Id == id && o.TenantId == tenantId), ct);
        if (string.IsNullOrEmpty(result?.FileUrl)) return NotFound(new { error = "lab_file_not_found" });
        if (_files is null) return StatusCode(503, new { error = "lab_storage_unavailable" });
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            var file = await _files.ReadAsync(tenantId.Value, id, resultId, result.FileUrl, ct);
            return File(file.Bytes, file.ContentType, $"lab-result-v{result.Version}{file.Extension}");
        }
        catch (FileNotFoundException) { return NotFound(new { error = "lab_file_not_found" }); }
        catch (InvalidDataException) { return StatusCode(503, new { error = "lab_file_unavailable" }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { return StatusCode(503, new { error = "lab_file_unavailable" }); }
    }

    [HttpPost("lab-orders/{id:guid}/doctor-review")]
    [AuthorizeRoles("Doctor")]
    public async Task<IActionResult> DoctorReview(
        Guid id,
        [FromBody] DoctorReviewLabOrderRequest request,
        CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var order = await _db.LabOrders.FirstOrDefaultAsync(o => o.Id == id && o.TenantId == tenantId, ct);
        if (order is null) return NotFound(new { error = "order_not_found" });

        // "Reviewed" with nothing to read would be a false clinical attestation.
        if (!await _db.LabResults.AnyAsync(r => r.LabOrderId == id, ct))
            return Conflict(new { error = "no_result_to_review", status = order.Status.ToString() });

        order.Status = LabOrderStatus.ReviewedByDoctor;
        order.DoctorReviewedAt = DateTimeOffset.UtcNow;
        order.DoctorReviewedByUserId = doctorId.Value;
        order.DoctorReviewNotes = request.ReviewNotes?.Trim();
        if (request.IsAbnormal.HasValue) order.IsAbnormal = request.IsAbnormal.Value;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            id = order.Id,
            status = "reviewed_by_doctor",
            doctorReviewedAt = order.DoctorReviewedAt,
            isAbnormal = order.IsAbnormal,
            reviewNotes = order.DoctorReviewNotes
        });
    }

    // ----- FR-08-03: Worklist -----

    [HttpGet("lab-orders")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> Worklist([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var query = _db.LabOrders.Where(o => o.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<LabOrderStatus>(status, true, out var statusEnum))
        {
            query = query.Where(o => o.Status == statusEnum);
        }

        // Pending list is the operationally important one — surface first
        var orders = await query
            .OrderBy(o => o.Status) // Ordered (0) before Completed (1)
            .ThenByDescending(o => o.CreatedAt)
            .Select(o => new
            {
                id = o.Id,
                consultationId = o.ConsultationId,
                patientId = o.PatientId,
                patientName = o.Patient.Name,
                doctorId = o.DoctorId,
                testName = o.TestName,
                notes = o.Notes,
                status = o.Status.ToString().ToLower(),
                isAbnormal = o.IsAbnormal,
                doctorReviewedAt = o.DoctorReviewedAt,
                createdAt = o.CreatedAt,
                completedAt = o.CompletedAt,
                latestResultVersion = _db.LabResults
                    .Where(r => r.LabOrderId == o.Id)
                    .Max(r => (int?)r.Version),
                // Latest amendment only: the alert panel must never show a superseded value.
                latestResult = _db.LabResults
                    .Where(r => r.LabOrderId == o.Id)
                    .OrderByDescending(r => r.Version)
                    .Select(r => new
                    {
                        resultText = r.ResultText,
                        referenceRange = r.ReferenceRange,
                        units = r.Units,
                        isAbnormal = r.IsAbnormal,
                        enteredAt = r.EnteredAt
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        return Ok(orders);
    }
}

public record CreateLabOrderRequest(string TestName, string? Notes);

public record DoctorReviewLabOrderRequest(string? ReviewNotes, bool? IsAbnormal);
