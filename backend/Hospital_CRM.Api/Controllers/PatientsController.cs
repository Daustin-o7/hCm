using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Api.Services.Typesense;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PatientsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly IPatientSearchService _search;

    public PatientsController(HospitalCrmDbContext db, IPatientSearchService search)
    {
        _db = db;
        _search = search;
    }

    [HttpPost]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    [RequirePermission(Permissions.PatientWrite)]
    public Task<IActionResult> Register([FromBody] PatientRegisterRequest request, CancellationToken ct) =>
        RegisterPatientAsync(request, ct);

    internal async Task<IActionResult> RegisterPatientAsync(PatientRegisterRequest request, CancellationToken ct, Guid? clientId = null)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        if (!userId.HasValue || userId == Guid.Empty || !tenantId.HasValue || tenantId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });

        if (request == null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Phone))
            return BadRequest(new { error = "name_and_phone_required" });
        request = request with { Name = request.Name.Trim(), Phone = request.Phone.Trim(), Address = request.Address?.Trim() };
        if (request.Name.Length is < 2 or > 100 || !Regex.IsMatch(request.Phone, @"^[6-9][0-9]{9}$")
            || request.ApproxAge is < 0 or > 130 || request.Address?.Length > 500
            || !string.IsNullOrWhiteSpace(request.Gender) && request.Gender.ToLowerInvariant() is not ("male" or "female" or "other" or "prefernottosay"))
            return BadRequest(new { error = "invalid_patient_demographics" });
        if (request.IdempotencyKey?.Length > 128 || !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            && !Regex.IsMatch(request.IdempotencyKey, @"^IDEMP-PAT-[a-zA-Z0-9\-]+$") && !Guid.TryParse(request.IdempotencyKey, out _))
            return BadRequest(new { error = "invalid_idempotency_key" });

        if (request.Dob is null && request.ApproxAge is null)
            return BadRequest(new { error = "either_dob_or_approxAge_required" });

        if (request.Consent is null || !request.Consent.Accepted || string.IsNullOrWhiteSpace(request.Consent.Purpose)
            || request.Consent.Purpose.Length > 200)
            return BadRequest(new { error = "consent_required" });
        if (request.Consent.Purpose.Trim() is not ("care_delivery" or "direct_care"))
            return BadRequest(new { error = "care_delivery_consent_required" });
        if (request.GuardianName?.Length > 100 || request.GuardianRelationship?.Length > 50 || request.NoticeVersion?.Length > 50
            || request.Language?.Length > 10 || request.CaptureMethod?.Length > 50)
            return BadRequest(new { error = "invalid_consent_metadata" });

        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var requestHash = RegistrationRequestHash(request);
            Patient? saved = null;
            // A registration is immutable even when its demographics or consent state later change.
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                saved = await _db.Patients.Include(p => p.Consents)
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == request.IdempotencyKey, ct);
                if (saved is not null)
                {
                    var originalHash = await _db.PatientAuditLogs.Where(a => a.PatientId == saved.Id
                        && a.FieldName == "REGISTRATION_REQUEST_V1" && a.OldValue == request.IdempotencyKey)
                        .Select(a => a.NewValue).FirstOrDefaultAsync(ct);
                    if (originalHash is not null)
                        return originalHash == requestHash
                            ? Ok(RegistrationResult(saved, idempotentReplay: true))
                            : Conflict(new { error = "idempotency_key_conflict" });
                }
            }
            var timezone = await _db.Clinics.Where(c => c.TenantId == tenantId).Select(c => c.Timezone).FirstOrDefaultAsync(ct);
            var today = new TimezoneService().GetClinicToday(timezone);
            if (request.Dob > today || request.Dob < today.AddYears(-130))
                return BadRequest(new { error = "invalid_patient_date_of_birth" });
            var age = request.ApproxAge ?? 0;
            if (request.Dob is { } dob)
            {
                age = today.Year - dob.Year;
                if (dob > today.AddYears(-age)) age--;
            }
            if (age < 18 && (string.IsNullOrWhiteSpace(request.GuardianName) || string.IsNullOrWhiteSpace(request.GuardianRelationship)))
                return BadRequest(new { error = "guardian_consent_required", message = "Record the guardian's name and relationship for a minor." });

            // A lost response must replay the registration, not trigger the family-phone warning.
            if (saved is { } existing)
            {
                if (existing.Name != request.Name || existing.Phone != request.Phone || existing.Dob != request.Dob
                    || existing.ApproxAge != request.ApproxAge || existing.Gender != ParseGender(request.Gender)
                    || existing.Address != request.Address || !existing.Consents.Any(c => c.Purpose == request.Consent.Purpose.Trim()
                        && c.GuardianName == request.GuardianName?.Trim() && c.GuardianRelationship == request.GuardianRelationship?.Trim()
                        && c.NoticeVersion == (string.IsNullOrWhiteSpace(request.NoticeVersion) ? "v1.0" : request.NoticeVersion.Trim())
                        && c.Language == (string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim())
                        && c.CaptureMethod == (string.IsNullOrWhiteSpace(request.CaptureMethod) ? "receptionist_assisted" : request.CaptureMethod.Trim()))
                    || request.WhatsAppOptIn != existing.Consents.Any(c => c.Purpose == "whatsapp_opt_in"))
                    return Conflict(new { error = "idempotency_key_conflict" });
                // shortcut: legacy registrations have no original hash; use their persisted evidence until manually reconciled.
                return Ok(RegistrationResult(existing, idempotentReplay: true));
            }
            if (clientId.HasValue && await _db.Patients.AnyAsync(p => p.Id == clientId, ct))
                return Conflict(new { error = "patient_id_conflict" });

            // 1. Same person = phone AND name AND date of birth. Name alone is not enough:
            //    Indian families share one phone and relatives can share a name.
            var samePerson = request.Dob.HasValue
                ? await _db.Patients.Include(p => p.Consents).FirstOrDefaultAsync(p => p.TenantId == tenantId
                    && p.Phone == request.Phone && p.Name.ToLower() == request.Name.ToLower() && p.Dob == request.Dob, ct)
                : null;

            if (samePerson is not null)
                return Ok(RegistrationResult(samePerson, possibleDuplicateOf: samePerson.Id));

            // 2. Family phone sharing check: allow multiple patients per phone if confirmed
            if (!request.ConfirmDifferentPersonOnSamePhone)
            {
                var familyMember = await _db.Patients.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Phone == request.Phone, ct);

                if (familyMember is not null)
                {
                    return Ok(new
                    {
                        patientId = (Guid?)null,
                        sharedPhoneDetected = true,
                        existingFamilyMemberName = familyMember.Name,
                        existingPatientId = familyMember.Id,
                        message = $"Phone number {request.Phone} is already registered under '{familyMember.Name}'. Pass confirmDifferentPersonOnSamePhone: true to register as a family member."
                    });
                }
            }

            // 4. Generate per-clinic human-readable UHID (sequential per tenant + month)
            var uhid = await NextUhidAsync(tenantId.Value, ct);

            var patient = new Patient
            {
                Id = clientId ?? Guid.NewGuid(),
                TenantId = tenantId.Value,
                Name = request.Name,
                Uhid = uhid,
                Phone = request.Phone,
                DobHasValue = request.Dob.HasValue,
                Dob = request.Dob,
                ApproxAge = request.ApproxAge,
                Gender = ParseGender(request.Gender),
                Address = request.Address,
                CreatedBy = userId.Value,
                CreatedAt = DateTimeOffset.UtcNow,
                IdempotencyKey = request.IdempotencyKey
            };

            // 5. Versioned DPDP Treatment Consent record
            var consent = new PatientConsent
            {
                Id = Guid.NewGuid(),
                PatientId = patient.Id,
                TenantId = tenantId.Value,
                Purpose = request.Consent.Purpose.Trim(),
                NoticeVersion = string.IsNullOrWhiteSpace(request.NoticeVersion) ? "v1.0" : request.NoticeVersion.Trim(),
                Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim(),
                CaptureMethod = string.IsNullOrWhiteSpace(request.CaptureMethod) ? "receptionist_assisted" : request.CaptureMethod.Trim(),
                GuardianName = request.GuardianName?.Trim(),
                GuardianRelationship = request.GuardianRelationship?.Trim(),
                CapturedBy = userId.Value,
                CapturedAt = DateTimeOffset.UtcNow
            };

            _db.Patients.Add(patient);
            _db.PatientConsents.Add(consent);

            // Separate WhatsApp opt-in consent record
            if (request.WhatsAppOptIn)
            {
                var whatsappConsent = new PatientConsent
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId.Value,
                    PatientId = patient.Id,
                    Purpose = "whatsapp_opt_in",
                    NoticeVersion = consent.NoticeVersion,
                    Language = consent.Language,
                    CaptureMethod = consent.CaptureMethod,
                    GuardianName = consent.GuardianName,
                    GuardianRelationship = consent.GuardianRelationship,
                    CapturedBy = userId.Value,
                    CapturedAt = DateTimeOffset.UtcNow
                };
                _db.PatientConsents.Add(whatsappConsent);
            }

            _db.PatientAuditLogs.Add(new PatientAuditLog
            {
                Id = Guid.NewGuid(),
                PatientId = patient.Id,
                ChangedBy = userId.Value,
                FieldName = "REGISTRATION_REQUEST_V1",
                OldValue = request.IdempotencyKey ?? "",
                NewValue = requestHash,
                ChangedAt = patient.CreatedAt
            });

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            // Sync owns its outer transaction and must index only after that transaction commits.
            if (!clientId.HasValue && (transaction != null || _db.Database.CurrentTransaction == null))
                await _search.IndexAsync(patient, ct);

            return StatusCode(201, RegistrationResult(patient));
        }
        catch (Exception ex) when (InvoiceAccounting.IsReceiptConflict(ex) || ex is DbUpdateException update && update.IsUniqueViolation())
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return Conflict(new { error = "patient_changed_concurrently", message = "Retry registration with the same key." });
        }
    }

    [HttpGet("summary")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    [RequirePermission(Permissions.PatientsRead)]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        // Counted in SQL: the list endpoint caps at 1,000 rows, so its length is not a patient count.
        var totalPatients = await _db.Patients.CountAsync(ct);
        var patientsWithActiveConsent = await _db.Patients
            .CountAsync(p => p.Consents.Any(c => c.WithdrawnAt == null), ct);
        return Ok(new { totalPatients, patientsWithActiveConsent });
    }

    [HttpGet]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    [RequirePermission(Permissions.PatientsRead)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var patients = await _db.Patients.AsNoTracking().Include(p => p.Consents)
            .OrderByDescending(p => p.CreatedAt)
            // shortcut: browse list stops at 1,000 newest; /patients/search is the unbounded lookup path.
            .Take(1000)
            .ToListAsync(ct);

        return Ok(patients.Select(patient => new
        {
            id = patient.Id,
            uhid = patient.Uhid,
            name = patient.Name,
            phone = patient.Phone,
            gender = patient.Gender.ToString(),
            dob = patient.Dob?.ToString("yyyy-MM-dd"),
            approxAge = patient.ApproxAge,
            address = patient.Address,
            createdAt = patient.CreatedAt,
            patientId = patient.Id,
            consents = ConsentEvidence(patient)
        }));
    }

    [HttpGet("search")]
    [Authorize]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist", "Nurse")]
    [RequirePermission(Permissions.PatientsRead)]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        if (!userId.HasValue || userId == Guid.Empty || !tenantId.HasValue || tenantId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { error = "query_too_short_min_2_chars", message = "Patient search query must be at least 2 characters." });
        if (tenantId != _db.CurrentTenantId)
            return StatusCode(403, new { error = "tenant_scope_mismatch" });

        var term = q.Trim();
        if (term.Length > 255) term = term[..255];

        // Search authorizes a collection read; there is no single patient FK to audit.
        _db.AuthAuditLogs.Add(new AuthAuditLog
        {
            TenantId = tenantId.Value,
            UserId = userId.Value,
            EventType = "patient_search_authorized",
            Outcome = "success"
        });
        await _db.SaveChangesAsync(ct);

        List<PatientSearchHit> hits;
        try
        {
            hits = await _search.SearchAsync(term, tenantId.Value, limit: 10, ct);
        }
        catch (SearchUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            return StatusCode(503, new { error = "search_unavailable", message = ex.Message });
        }

        // Composite dropdown format: name + DOB + last 4 phone (Zero excess PHI)
        var results = hits.Select(h => new
        {
            id = h.Id,
            name = h.Name,
            dob = h.Dob,
            phoneLast4 = h.Phone.Length >= 4 ? h.Phone.Substring(h.Phone.Length - 4) : h.Phone,
            gender = h.Gender,
            score = h.Score
        });

        return Ok(results);
    }

    [HttpPost("{id:guid}/consents/{consentId:guid}/withdraw")]
    [AuthorizeRoles("ClinicAdmin", "Receptionist")]
    public async Task<IActionResult> WithdrawConsent(Guid id, Guid consentId, CancellationToken ct)
    {
        var consent = await _db.PatientConsents
            .FirstOrDefaultAsync(c => c.Id == consentId && c.PatientId == id, ct);
        if (consent is null)
            return NotFound(new { error = "consent_not_found" });

        consent.WithdrawnAt = DateTimeOffset.UtcNow;

        // Consent withdrawal is a DPDP event: prove who withdrew it and when.
        _db.PatientAuditLogs.Add(new PatientAuditLog
        {
            Id = Guid.NewGuid(),
            PatientId = consent.PatientId,
            FieldName = "CONSENT_WITHDRAWN",
            OldValue = consent.Purpose,
            NewValue = consent.WithdrawnAt.Value.ToString("O"),
            ChangedBy = User.GetUserId()!.Value,
            ChangedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "consent_withdrawn", consentId = consent.Id, withdrawnAt = consent.WithdrawnAt });
    }

    [HttpPost("check-duplicate")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    public async Task<IActionResult> CheckDuplicate([FromBody] CheckDuplicateRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        if (!userId.HasValue || userId == Guid.Empty || !tenantId.HasValue || tenantId == Guid.Empty)
            return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name_required" });

        if (tenantId != _db.CurrentTenantId)
            return StatusCode(403, new { error = "tenant_scope_mismatch" });
        List<PatientSearchHit> hits;
        try
        {
            hits = await _search.CheckDuplicatesAsync(request.Name, request.Phone, request.Dob, tenantId.Value, ct);
        }
        catch (SearchUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            return StatusCode(503, new { error = "search_unavailable", message = ex.Message });
        }

        var potentialMatches = hits.Select(h => new
        {
            id = h.Id,
            name = h.Name,
            dob = h.Dob,
            phoneLast4 = h.Phone.Length >= 4 ? h.Phone.Substring(h.Phone.Length - 4) : h.Phone,
            phone = h.Phone,
            gender = h.Gender,
            address = h.Address,
            score = h.Score
        }).ToList();

        if (potentialMatches.Count == 0)
            return Ok(new { duplicate = false, matches = potentialMatches });

        return Ok(new
        {
            duplicate = true,
            matches = potentialMatches,
            message = "Potential duplicate patients found. Please confirm if this is the same patient."
        });
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    [RequirePermission(Permissions.PatientsRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var patient = await _db.Patients
            .Include(p => p.Consents)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (patient is null)
            return NotFound(new { error = "patient_not_found" });

        // Clinical-data scope: a doctor needs a treating relationship, not just the tenant.
        if (IsDoctor() && !await DoctorMayAccessPatientAsync(id, patient.CreatedBy, ct))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "clinical_access_denied",
                breakGlassRequired = true,
                patientId = patient.Id
            });
        }

        return Ok(await BuildPatientDetailAsync(patient, ct));
    }

    /// <summary>
    /// Emergency override for a clinician with no treating relationship. Every use is
    /// written to the append-only patient audit trail and the auth audit log.
    /// </summary>
    [HttpPost("{id:guid}/break-glass")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Nurse")]
    public async Task<IActionResult> BreakGlass(Guid id, [FromBody] BreakGlassRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(request.ReasonCode))
            return BadRequest(new { error = "reason_code_required" });

        var patient = await _db.Patients.Include(p => p.Consents).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
            return NotFound(new { error = "patient_not_found" });

        var detail = $"{request.ReasonCode.Trim()}: {request.Reason?.Trim()}";

        _db.PatientAuditLogs.Add(new PatientAuditLog
        {
            Id = Guid.NewGuid(),
            PatientId = patient.Id,
            FieldName = "BreakGlassAccess",
            OldValue = string.Empty,
            NewValue = detail,
            ChangedBy = userId.Value,
            ChangedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        await AuthAudit.LogAsync(_db, HttpContext, AuthEvent.BreakGlass, "success",
            userId.Value, User.GetTenantId(), User.FindFirst("email")?.Value, detail, ct);

        return Ok(await BuildPatientDetailAsync(patient, ct));
    }

    /// <summary>
    /// Next human-readable UHID for this tenant and month. Sequential rather than random:
    /// 9000 random 4-digit values collide long before a clinic's monthly volume.
    /// Tenant scope comes from the global query filter, not a hand-written predicate.
    /// </summary>
    private async Task<string> NextUhidAsync(Guid tenantId, CancellationToken ct)
    {
        var prefix = $"UHID-{DateTime.UtcNow:yyyyMM}-";
        var last = await _db.Patients
            .Where(p => p.TenantId == tenantId && p.Uhid != null && p.Uhid.StartsWith(prefix))
            .OrderByDescending(p => p.Uhid!.Length).ThenByDescending(p => p.Uhid)
            .Select(p => p.Uhid)
            .FirstOrDefaultAsync(ct);

        var next = 1;
        if (last is not null && int.TryParse(last[prefix.Length..], out var parsed))
            next = parsed + 1;

        return $"{prefix}{next:D4}";
    }

    private bool IsDoctor() =>
        string.Equals(User.GetUserRole(), "Doctor", StringComparison.OrdinalIgnoreCase);

    private Task<bool> DoctorMayAccessPatientAsync(Guid patientId, Guid createdBy, CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        if (!doctorId.HasValue)
            return Task.FromResult(false);

        if (createdBy == doctorId.Value)
            return Task.FromResult(true);

        return _db.Appointments.AnyAsync(
            a => a.PatientId == patientId && a.DoctorId == doctorId.Value, ct);
    }

    private async Task<Dictionary<string, object?>> BuildPatientDetailAsync(Patient patient, CancellationToken ct)
    {
        var isReceptionist = string.Equals(User.GetUserRole(), "receptionist", StringComparison.OrdinalIgnoreCase);

        var result = new Dictionary<string, object?>
        {
            ["id"] = patient.Id,
            ["uhid"] = patient.Uhid,
            ["name"] = patient.Name,
            ["phone"] = patient.Phone,
            ["dobHasValue"] = patient.DobHasValue,
            ["dob"] = patient.Dob?.ToString("yyyy-MM-dd"),
            ["approxAge"] = patient.ApproxAge,
            ["gender"] = patient.Gender.ToString().ToLower(),
            ["address"] = patient.Address,
            ["createdAt"] = patient.CreatedAt,
            ["consents"] = ConsentEvidence(patient)
        };

        if (!isReceptionist)
        {
            result["consultations"] = await _db.Consultations
                .Where(c => c.Appointment.PatientId == patient.Id)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    diagnosis = c.Diagnosis,
                    chiefComplaint = c.ChiefComplaint,
                    createdAt = c.CreatedAt
                })
                .ToListAsync(ct);
        }

        return result;
    }

    [HttpPatch("{id:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist")]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatientPatchRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
            return NotFound(new { error = "patient_not_found" });

        var now = DateTimeOffset.UtcNow;

        if (request.Phone is not null && request.Phone != patient.Phone)
        {
            var phoneTaken = await _db.Patients.AnyAsync(p => p.Phone == request.Phone && p.Id != id, ct);
            if (phoneTaken)
                return Conflict(new { error = "phone_already_in_use" });

            await AuditService.LogChangeAsync(_db, "Patient", id, userId.Value, "phone", patient.Phone, request.Phone, ct);
            patient.Phone = request.Phone;
        }

        if (request.Name is not null && request.Name != patient.Name)
        {
            await AuditService.LogChangeAsync(_db, "Patient", id, userId.Value, "name", patient.Name, request.Name, ct);
            patient.Name = request.Name;
        }

        if (request.Dob.HasValue && request.Dob.Value != patient.Dob)
        {
            var oldDob = patient.Dob?.ToString("yyyy-MM-dd") ?? "";
            var newDob = request.Dob.Value.ToString("yyyy-MM-dd");
            await AuditService.LogChangeAsync(_db, "Patient", id, userId.Value, "dob", oldDob, newDob, ct);
            patient.Dob = request.Dob;
            patient.DobHasValue = request.Dob.HasValue;
        }

        if (request.Gender is not null)
        {
            var newGender = ParseGender(request.Gender);
            if (newGender != patient.Gender)
            {
                await AuditService.LogChangeAsync(_db, "Patient", id, userId.Value, "gender", patient.Gender.ToString(), newGender.ToString(), ct);
                patient.Gender = newGender;
            }
        }

        if (request.Address is not null && request.Address != patient.Address)
        {
            await AuditService.LogChangeAsync(_db, "Patient", id, userId.Value, "address", patient.Address ?? "", request.Address, ct);
            patient.Address = request.Address;
        }

        await _db.SaveChangesAsync(ct);

        await _search.IndexAsync(patient, ct);

        return Ok(new { patientId = patient.Id, updatedAt = now });
    }

    private static Gender ParseGender(string? gender)
    {
        return gender?.ToLower() switch
        {
            "male" => Gender.Male,
            "female" => Gender.Female,
            "other" => Gender.Other,
            _ => Gender.PreferNotToSay
        };
    }

    private static string RegistrationRequestHash(PatientRegisterRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Name,
            request.Phone,
            request.Dob,
            request.ApproxAge,
            Gender = ParseGender(request.Gender),
            request.Address,
            Purpose = request.Consent!.Purpose.Trim(),
            request.WhatsAppOptIn,
            GuardianName = request.GuardianName?.Trim(),
            GuardianRelationship = request.GuardianRelationship?.Trim(),
            NoticeVersion = string.IsNullOrWhiteSpace(request.NoticeVersion) ? "v1.0" : request.NoticeVersion.Trim(),
            Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim(),
            CaptureMethod = string.IsNullOrWhiteSpace(request.CaptureMethod) ? "receptionist_assisted" : request.CaptureMethod.Trim(),
            request.ConfirmDifferentPersonOnSamePhone
        })));

    private static object ConsentEvidence(Patient patient) => patient.Consents.OrderBy(c => c.CapturedAt).ThenBy(c => c.Id).Select(c => new
    {
        id = c.Id,
        patientId = c.PatientId,
        purpose = c.Purpose,
        noticeVersion = c.NoticeVersion,
        language = c.Language,
        captureMethod = c.CaptureMethod,
        guardianName = c.GuardianName,
        guardianRelationship = c.GuardianRelationship,
        capturedBy = c.CapturedBy,
        capturedAt = c.CapturedAt,
        withdrawnAt = c.WithdrawnAt
    }).ToArray();

    private static object RegistrationResult(Patient patient, bool idempotentReplay = false, Guid? possibleDuplicateOf = null) => new
    {
        id = patient.Id,
        patientId = patient.Id,
        uhid = patient.Uhid,
        name = patient.Name,
        phone = patient.Phone,
        gender = patient.Gender.ToString(),
        dob = patient.Dob?.ToString("yyyy-MM-dd"),
        approxAge = patient.ApproxAge,
        address = patient.Address,
        createdAt = patient.CreatedAt,
        idempotencyKey = patient.IdempotencyKey,
        possibleDuplicateOf,
        idempotentReplay,
        consents = ConsentEvidence(patient)
    };
}

public record PatientRegisterRequest(
    [Required(ErrorMessage = "Patient Name is required"), StringLength(100, MinimumLength = 2, ErrorMessage = "Patient Name must be between 2 and 100 characters")]
    string Name,

    [Required(ErrorMessage = "Phone number is required"), RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Phone number must be a valid 10-digit mobile number")]
    string Phone,

    DateOnly? Dob,

    [Range(0, 130, ErrorMessage = "Approximate Age must be between 0 and 130")]
    int? ApproxAge,

    string? Gender,

    [StringLength(500, ErrorMessage = "Address cannot exceed 500 characters")]
    string? Address,

    [Required(ErrorMessage = "Patient consent is mandatory for registration")]
    ConsentRequest? Consent,

    [MaxLength(128), RegularExpression(@"^(IDEMP-PAT-[a-zA-Z0-9\-]+|[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})$", ErrorMessage = "IdempotencyKey must follow format IDEMP-PAT-{UUID} or be a UUID")]
    string? IdempotencyKey,

    bool WhatsAppOptIn = false,
    [StringLength(100, ErrorMessage = "Guardian name cannot exceed 100 characters")]
    string? GuardianName = null,
    [StringLength(50, ErrorMessage = "Guardian relationship cannot exceed 50 characters")]
    string? GuardianRelationship = null,
    [StringLength(50, ErrorMessage = "Notice version cannot exceed 50 characters")]
    string? NoticeVersion = null,
    [StringLength(10, ErrorMessage = "Language code cannot exceed 10 characters")]
    string? Language = null,
    [StringLength(50, ErrorMessage = "Capture method cannot exceed 50 characters")]
    string? CaptureMethod = null,
    bool ConfirmDifferentPersonOnSamePhone = false);

public record ConsentRequest(
    [Required, Range(typeof(bool), "true", "true", ErrorMessage = "Patient consent must be accepted")]
    bool Accepted,

    [StringLength(200, ErrorMessage = "Consent purpose cannot exceed 200 characters")]
    string Purpose);

public record PatientPatchRequest(
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Patient Name must be between 2 and 100 characters")]
    string? Name,

    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Phone number must be a valid 10-digit mobile number")]
    string? Phone,

    DateOnly? Dob,

    string? Gender,

    [StringLength(500, ErrorMessage = "Address cannot exceed 500 characters")]
    string? Address);

public record CheckDuplicateRequest(
    [Required(ErrorMessage = "Name is required"), StringLength(100, MinimumLength = 2)]
    string Name,

    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Phone number must be a valid 10-digit mobile number")]
    string? Phone,

    DateOnly? Dob);

public record BreakGlassRequest(
    [Required(ErrorMessage = "A reason code is required")]
    string ReasonCode,

    [StringLength(500)]
    string? Reason = null);

