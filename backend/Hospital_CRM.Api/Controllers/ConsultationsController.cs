using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class ConsultationsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly ILogger<ConsultationsController> _logger;
    private readonly IConfiguration? _config;

    public ConsultationsController(HospitalCrmDbContext db, ILogger<ConsultationsController> logger, IConfiguration? config = null)
    {
        _db = db;
        _logger = logger;
        _config = config;
    }

    // Parameterless constructor fallback for tests if required
    public ConsultationsController(HospitalCrmDbContext db) : this(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsultationsController>.Instance)
    {
    }

    #region Queue Concurrency & Patient Claiming

    /// <summary>
    /// Atomic claim: transitions appointment to InConsultation ONLY if it is currently CheckedIn or InNursing.
    /// Prevents two doctors from claiming the same patient simultaneously.
    /// </summary>
    [HttpPost("appointments/{appointmentId:guid}/claim")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> ClaimAppointment(Guid appointmentId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await _db.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);

        if (appointment is null)
            return NotFound(new { error = "appointment_not_found" });

        // Concurrency guard: Only CheckedIn or InNursing can be claimed
        if (appointment.Status is not (AppointmentStatus.CheckedIn or AppointmentStatus.InNursing))
        {
            return Conflict(new
            {
                error = "appointment_already_claimed_or_processed",
                currentStatus = appointment.Status.ToString()
            });
        }

        // The booking's doctor owns the patient. Re-stamping DoctorId here would let any doctor
        // take over another doctor's visit and then finalize it under their own name.
        var isAdmin = string.Equals(User.GetUserRole(), "ClinicAdmin", StringComparison.OrdinalIgnoreCase);
        if (!isAdmin && appointment.DoctorId != callerId.Value)
            return Conflict(new { error = "appointment_assigned_to_another_doctor", doctorId = appointment.DoctorId });

        appointment.Status = AppointmentStatus.InConsultation;
        appointment.UpdatedAt = DateTimeOffset.UtcNow;

        Audit(appointment.PatientId, callerId.Value, "APPOINTMENT_CLAIMED", appointment.Id.ToString());

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // UpdatedAt is a concurrency token; losing the race must read as "already claimed".
            return Conflict(new
            {
                error = "appointment_already_claimed_or_processed",
                currentStatus = "concurrent_update"
            });
        }

        _logger.LogInformation("Appointment {AppointmentId} atomically claimed by Doctor {DoctorId}", appointmentId, callerId.Value);

        return Ok(new
        {
            appointmentId = appointment.Id,
            status = "in_consultation",
            doctorId = appointment.DoctorId,
            patientId = appointment.PatientId,
            patientName = appointment.Patient?.Name
        });
    }

    /// <summary>
    /// Park appointment when patient steps out (e.g. for urine sample or outside call).
    /// </summary>
    [HttpPost("appointments/{appointmentId:guid}/park")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> ParkAppointment(Guid appointmentId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await _db.Appointments.FindAsync([appointmentId], ct);
        if (appointment is null) return NotFound(new { error = "appointment_not_found" });

        if (!CanActOnVisit(appointment)) return Forbid();

        if (appointment.Status != AppointmentStatus.InConsultation)
            return BadRequest(new { error = "only_in_consultation_appointments_can_be_parked" });

        appointment.Status = AppointmentStatus.Parked;
        appointment.UpdatedAt = DateTimeOffset.UtcNow;
        Audit(appointment.PatientId, callerId.Value, "APPOINTMENT_PARKED", appointment.Id.ToString());
        await _db.SaveChangesAsync(ct);

        return Ok(new { appointmentId, status = "parked" });
    }

    /// <summary>
    /// Recall a parked patient back into consultation.
    /// </summary>
    [HttpPost("appointments/{appointmentId:guid}/recall")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> RecallAppointment(Guid appointmentId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await _db.Appointments.FindAsync([appointmentId], ct);
        if (appointment is null) return NotFound(new { error = "appointment_not_found" });

        if (!CanActOnVisit(appointment)) return Forbid();

        if (appointment.Status != AppointmentStatus.Parked)
            return BadRequest(new { error = "appointment_is_not_parked" });

        appointment.Status = AppointmentStatus.InConsultation;
        appointment.UpdatedAt = DateTimeOffset.UtcNow;
        Audit(appointment.PatientId, callerId.Value, "APPOINTMENT_RECALLED", appointment.Id.ToString());
        await _db.SaveChangesAsync(ct);

        return Ok(new { appointmentId, status = "in_consultation" });
    }

    #endregion

    #region Patient Context Panel

    /// <summary>
    /// Consolidated patient context panel for doctors: demographics, recorded allergies,
    /// today's triage vitals, chronic conditions, last consultation note, last prescription, and visit timeline.
    /// Audits clinical record read.
    /// </summary>
    [HttpGet("patients/{patientId:guid}/clinical-context")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    public async Task<IActionResult> GetPatientContextPanel(Guid patientId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (!callerId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var patient = await _db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId, ct);

        if (patient is null) return NotFound(new { error = "patient_not_found" });

        // Audit read of clinical record
        _db.PatientAuditLogs.Add(new PatientAuditLog
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            ChangedBy = callerId.Value,
            FieldName = "CONSULTATION_READ",
            OldValue = "CONTEXT_PANEL",
            NewValue = DateTimeOffset.UtcNow.ToString("O"),
            ChangedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        // Fetch latest vitals (today's precheck / triage)
        var latestVitals = await _db.PatientVitals
            .AsNoTracking()
            .Where(v => v.PatientId == patientId)
            .OrderByDescending(v => v.RecordedAt)
            .FirstOrDefaultAsync(ct);

        // Fetch last finalized or amended consultation (excluding unfinalized drafts)
        var lastConsultation = await _db.Consultations
            .AsNoTracking()
            .Include(c => c.Doctor)
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .Where(c => c.Appointment.PatientId == patientId && c.Status != ConsultationStatus.Draft)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Visit statistics
        var totalVisits = await _db.Appointments
            .CountAsync(a => a.PatientId == patientId && a.Status == AppointmentStatus.Completed, ct);

        // Consolidate allergies from precheck and vitals
        var allergySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var precheck = await _db.PrecheckSubmissions
            .Where(p => p.Appointment.PatientId == patientId)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(precheck?.Allergies))
            foreach (var a in precheck.Allergies.Split(',', StringSplitOptions.RemoveEmptyEntries))
                allergySet.Add(a.Trim());
        if (!string.IsNullOrWhiteSpace(latestVitals?.Allergies))
            foreach (var a in latestVitals.Allergies.Split(',', StringSplitOptions.RemoveEmptyEntries))
                allergySet.Add(a.Trim());

        return Ok(new
        {
            patient = new
            {
                id = patient.Id,
                name = patient.Name,
                phone = patient.Phone,
                approxAge = patient.ApproxAge,
                dob = patient.Dob,
                gender = patient.Gender,
                uhid = patient.Uhid,
                allergies = allergySet.ToList()
            },
            todayVitals = latestVitals is null ? null : new
            {
                systolicBp = latestVitals.SystolicBp,
                diastolicBp = latestVitals.DiastolicBp,
                heartRate = latestVitals.HeartRate,
                temperature = latestVitals.Temperature,
                respiratoryRate = latestVitals.RespiratoryRate,
                spO2 = latestVitals.SpO2,
                weightKg = latestVitals.WeightKg,
                heightCm = latestVitals.HeightCm,
                bmi = latestVitals.Bmi,
                bloodGlucoseMgDl = latestVitals.BloodGlucoseMgDl,
                bedsideObservations = latestVitals.BedsideObservations,
                recordedAt = latestVitals.RecordedAt
            },
            lastConsultation = lastConsultation is null ? null : new
            {
                consultationId = lastConsultation.Id,
                date = lastConsultation.CreatedAt,
                doctorName = lastConsultation.Doctor?.Name,
                chiefComplaint = lastConsultation.ChiefComplaint,
                diagnosis = lastConsultation.Diagnosis,
                observations = lastConsultation.Observations,
                prescriptions = lastConsultation.Prescriptions.SelectMany(p => p.Items).Select(i => new
                {
                    genericName = !string.IsNullOrWhiteSpace(i.GenericName) ? i.GenericName : i.MedicineText,
                    brandName = i.BrandName,
                    dosageForm = i.DosageForm,
                    strength = i.Strength,
                    frequency = i.Frequency,
                    duration = i.Duration,
                    timing = i.Timing
                }).ToList()
            },
            totalCompletedVisits = totalVisits
        });
    }

    #endregion

    #region Draft & Autosave (Non-Binding)

    /// <summary>
    /// Autosave consultation draft. Carries zero legal weight, does not complete appointment,
    /// and allows doctor to write notes safely without fear of network loss or premature finalization.
    /// </summary>
    [HttpPost("appointments/{appointmentId:guid}/consultation/draft")]
    [AuthorizeRoles("Doctor")]
    [RequirePermission(Permissions.ConsultationWrite)]
    public async Task<IActionResult> SaveDraft(Guid appointmentId, [FromBody] SaveConsultationDraftRequest request, CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await _db.Appointments.FindAsync([appointmentId], ct);
        if (appointment is null) return NotFound(new { error = "appointment_not_found" });

        if (appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
            return Conflict(new { error = "appointment_closed", status = appointment.Status.ToString() });

        // A finalized record is sealed: further edits must go through amend, never a fresh draft.
        var finalized = await _db.Consultations
            .Where(c => c.AppointmentId == appointmentId && c.Status != ConsultationStatus.Draft)
            .OrderByDescending(c => c.Version)
            .Select(c => new { c.Id, c.Version })
            .FirstOrDefaultAsync(ct);

        if (finalized is not null)
            return Conflict(new
            {
                error = "consultation_already_finalized",
                consultationId = finalized.Id,
                version = finalized.Version
            });

        if (appointment.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
            return Conflict(new { error = "appointment_closed", status = appointment.Status.ToString() });

        // Find existing draft or create new
        var draft = await _db.Consultations
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId && c.Status == ConsultationStatus.Draft, ct);

        if (draft is null)
        {
            draft = new Consultation
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointmentId,
                DoctorId = doctorId.Value,
                Status = ConsultationStatus.Draft,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Consultations.Add(draft);
            // Audit creation only: autosave fires every ~1.2s and would bury the log.
            Audit(appointment.PatientId, doctorId.Value, "CONSULTATION_DRAFT_CREATED", draft.Id.ToString());
        }

        draft.ChiefComplaint = request.ChiefComplaint;
        draft.Observations = request.Observations;
        draft.Diagnosis = request.Diagnosis;
        draft.AdvisedInvestigations = request.AdvisedInvestigations;
        draft.IcdCode = request.IcdCode;
        draft.IcdTitle = request.IcdTitle;
        draft.FollowUpDate = request.FollowUpDate;
        draft.FollowUpNotes = request.FollowUpNotes;
        draft.TemplateId = request.TemplateId;
        draft.TemplateVersion = request.TemplateVersion;
        draft.StructuredObservationsJson = request.StructuredObservationsJson;
        draft.OdontogramJson = request.OdontogramJson;
        draft.TenantId = User.GetTenantId() ?? appointment.TenantId;
        draft.UpdatedAt = DateTimeOffset.UtcNow;

        // Sync draft prescription items if provided
        if (request.PrescriptionItems != null)
        {
            var drugSchedules = await LoadDrugSchedulesAsync(request.PrescriptionItems.Select(i => i.DrugId), ct);
            var rx = draft.Prescriptions.FirstOrDefault();
            if (rx is null)
            {
                rx = new Prescription
                {
                    Id = Guid.NewGuid(),
                    TenantId = draft.TenantId,
                    ConsultationId = draft.Id,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                draft.Prescriptions.Add(rx);
            }
            else
            {
                _db.PrescriptionItems.RemoveRange(rx.Items);
                rx.Items.Clear();
            }

            foreach (var item in request.PrescriptionItems)
                rx.Items.Add(BuildItem(item, drugSchedules));
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            consultationId = draft.Id,
            status = "draft",
            version = draft.Version,
            updatedAt = draft.UpdatedAt
        });
    }

    #endregion

    #region Finalize & Sign (Single Atomic Transaction)

    /// <summary>
    /// Finalize & Sign: The single authoritative moment that locks the note, structured prescription,
    /// and ordered tests together in one atomic transaction. Advances appointment to Completed.
    /// Evaluates clinical safety checks (allergies, duplicates, pediatric weight requirement, NDPS restrictions).
    /// </summary>
    [HttpPost("appointments/{appointmentId:guid}/consultation/finalize")]
    [AuthorizeRoles("Doctor")]
    [RequirePermission(Permissions.ConsultationWrite)]
    public async Task<IActionResult> FinalizeConsultation(
        Guid appointmentId,
        [FromBody] FinalizeConsultationRequest request,
        CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        var tenantId = User.GetTenantId() ?? Guid.Empty;
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await _db.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);

        if (appointment is null) return NotFound(new { error = "appointment_not_found" });
        if (appointment.DoctorId != doctorId.Value) return Forbid();

        // Only a live encounter can be sealed. A Booked/Cancelled/Completed visit is not a consultation.
        // Checked before the status guard below so a repeat click reads as "already finalized".
        // Sealing twice would create a second Version 1 record. Signed notes are immutable.
        var alreadySigned = await _db.Consultations
            .Where(c => c.AppointmentId == appointmentId && c.Status != ConsultationStatus.Draft)
            .OrderByDescending(c => c.Version)
            .Select(c => new { c.Id, c.Version })
            .FirstOrDefaultAsync(ct);

        if (alreadySigned is not null)
            return Conflict(new
            {
                error = "consultation_already_finalized",
                consultationId = alreadySigned.Id,
                version = alreadySigned.Version,
                message = "Finalized clinical records are immutable; amend the existing record instead."
            });

        if (appointment.Status is not (AppointmentStatus.CheckedIn or AppointmentStatus.InNursing or AppointmentStatus.InConsultation))
            return Conflict(new { error = "appointment_not_in_consultation", status = appointment.Status.ToString() });

        var patient = appointment.Patient;

        // This visit's vitals first, then anything recorded today for the patient.
        var vitals = await _db.PatientVitals
            .Where(v => v.AppointmentId == appointmentId)
            .OrderByDescending(v => v.RecordedAt)
            .FirstOrDefaultAsync(ct);

        if (vitals is null)
        {
            var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            vitals = await _db.PatientVitals
                .Where(v => v.PatientId == patient.Id && v.RecordedAt >= todayStart)
                .OrderByDescending(v => v.RecordedAt)
                .FirstOrDefaultAsync(ct);
        }

        var safetyWarnings = new List<object>();

        // 1. Safety Check: Pediatric Weight Requirement (< 12 years old)
        var isPediatric = false;
        if (patient.ApproxAge.HasValue && patient.ApproxAge.Value < 12) isPediatric = true;
        if (patient.Dob.HasValue)
        {
            var ageYears = DateTime.UtcNow.Year - patient.Dob.Value.Year;
            if (ageYears < 12) isPediatric = true;
        }

        if (isPediatric && (request.PrescriptionItems?.Count ?? 0) > 0)
        {
            if (vitals is null || !vitals.WeightKg.HasValue || vitals.WeightKg.Value <= 0)
            {
                return BadRequest(new
                {
                    error = "pediatric_weight_required",
                    message = "Pediatric prescribing requires weight to be captured on today's vitals before finalizing prescription."
                });
            }
        }

        // 2. Safety Check: Allergy Check
        var allergyKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finalizePrecheck = await _db.PrecheckSubmissions
            .Where(p => p.AppointmentId == appointmentId)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(finalizePrecheck?.Allergies))
            foreach (var a in finalizePrecheck.Allergies.Split(',', StringSplitOptions.RemoveEmptyEntries))
                allergyKeywords.Add(a.Trim());
        if (!string.IsNullOrWhiteSpace(vitals?.Allergies))
            foreach (var a in vitals.Allergies.Split(',', StringSplitOptions.RemoveEmptyEntries))
                allergyKeywords.Add(a.Trim());

        if (request.PrescriptionItems != null)
        {
            foreach (var item in request.PrescriptionItems)
            {
                var medName = (item.GenericName ?? "") + " " + (item.BrandName ?? "");
                foreach (var allergen in allergyKeywords)
                {
                    if (allergen.Length >= 3 && medName.Contains(allergen, StringComparison.OrdinalIgnoreCase))
                    {
                        safetyWarnings.Add(new
                        {
                            type = "allergy_warning",
                            drug = item.GenericName,
                            allergenMatched = allergen,
                            message = $"Prescribed medicine '{item.GenericName}' matches recorded patient allergy '{allergen}'."
                        });
                    }
                }
            }
        }

        // 3. Safety Check: Duplicate Generic Drug Check
        if (request.PrescriptionItems != null)
        {
            var genericCounts = request.PrescriptionItems
                .Where(i => !string.IsNullOrWhiteSpace(i.GenericName))
                .GroupBy(i => i.GenericName.Trim().ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var dup in genericCounts)
            {
                safetyWarnings.Add(new
                {
                    type = "duplicate_drug_warning",
                    genericName = dup,
                    message = $"Duplicate prescription of generic ingredient '{dup}' detected across multiple lines."
                });
            }
        }

        // 4. Safety Check: NDPS statutory digital prescribing guard.
        // The request's schedule string is client-supplied, so the catalogue is the authority.
        var drugSchedules = await LoadDrugSchedulesAsync(request.PrescriptionItems.Select(i => i.DrugId), ct);
        var ndpsItem = request.PrescriptionItems?.FirstOrDefault(i =>
            (i.DrugId is { } drugId && drugSchedules.TryGetValue(drugId, out var sched) && sched == ScheduleClass.NDPS) ||
            string.Equals(i.ScheduleClass, "ndps", StringComparison.OrdinalIgnoreCase));

        if (ndpsItem != null)
        {
            return BadRequest(new
            {
                error = "ndps_digital_prescribing_prohibited",
                message = $"Digital prescribing of NDPS substance '{ndpsItem.GenericName}' is disabled under statutory narcotics regulations. Official physical stationery with physical wet signature required."
            });
        }

        // Fetch Doctor's Registration Number & Qualifications
        var membership = await _db.TenantMemberships
            .FirstOrDefaultAsync(m => m.UserId == doctorId.Value && m.TenantId == tenantId, ct);
        var doctorUser = await _db.Users.FindAsync([doctorId.Value], ct);

        var regNumber = membership?.RegistrationNumber ?? "NMC-VERIFIED-PRACTITIONER";
        var qualifications = membership?.Qualifications ?? "MBBS";

        // Atomic Transaction
        await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            // Find draft or create new finalized consultation
            var consultation = await _db.Consultations
                .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
                .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId && c.Status == ConsultationStatus.Draft, ct);

            if (consultation is null)
            {
                consultation = new Consultation
                {
                    Id = Guid.NewGuid(),
                    AppointmentId = appointmentId,
                    DoctorId = doctorId.Value,
                    Version = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                _db.Consultations.Add(consultation);
            }

            consultation.Status = ConsultationStatus.Finalized;
            consultation.ChiefComplaint = request.ChiefComplaint;
            consultation.Observations = request.Observations;
            consultation.Diagnosis = request.Diagnosis;
            consultation.AdvisedInvestigations = request.AdvisedInvestigations;
            consultation.IcdCode = request.IcdCode;
            consultation.IcdTitle = request.IcdTitle;
            consultation.FollowUpDate = request.FollowUpDate;
            consultation.FollowUpNotes = request.FollowUpNotes;
            consultation.TemplateId = request.TemplateId;
            consultation.TemplateVersion = request.TemplateVersion;
            consultation.StructuredObservationsJson = request.StructuredObservationsJson;
            consultation.OdontogramJson = request.OdontogramJson;
            if (tenantId != Guid.Empty) consultation.TenantId = tenantId;

            // One timestamp, second precision: SignedAt and the hash must agree exactly once
            // the row is read back from Postgres (microseconds would break reproduction).
            var signedAt = SecondPrecision(DateTimeOffset.UtcNow);
            consultation.SignedAt = signedAt;
            consultation.SignedByDoctorId = doctorId.Value;
            consultation.DoctorRegistrationNumber = regNumber;
            consultation.DoctorQualifications = qualifications;
            consultation.UpdatedAt = DateTimeOffset.UtcNow;

            // Generate Verification QR Token & Digital Signature Hash
            var qrToken = $"RX-{Guid.NewGuid():N}"[..15].ToUpperInvariant();
            consultation.VerificationQrCode = qrToken;
            consultation.DigitalSignatureHash = SignatureHash(
                doctorId.Value, appointmentId, consultation.Id, request.Diagnosis, signedAt);

            // Save Structured Prescription Items
            if (request.PrescriptionItems != null && request.PrescriptionItems.Count > 0)
            {
                var rx = consultation.Prescriptions.FirstOrDefault();
                if (rx is null)
                {
                    rx = new Prescription
                    {
                        Id = Guid.NewGuid(),
                        TenantId = consultation.TenantId,
                        ConsultationId = consultation.Id,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    consultation.Prescriptions.Add(rx);
                }
                else
                {
                    _db.PrescriptionItems.RemoveRange(rx.Items);
                    rx.Items.Clear();
                }

                foreach (var item in request.PrescriptionItems)
                    rx.Items.Add(BuildItem(item, drugSchedules));
            }

            // Create Lab Orders if requested
            if (request.OrderedLabTests != null && request.OrderedLabTests.Count > 0)
            {
                foreach (var test in request.OrderedLabTests.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    _db.LabOrders.Add(new LabOrder
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        ConsultationId = consultation.Id,
                        PatientId = patient.Id,
                        DoctorId = doctorId.Value,
                        TestName = test.Trim(),
                        Status = LabOrderStatus.Ordered,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            // Lock Appointment -> Completed
            appointment.Status = AppointmentStatus.Completed;
            appointment.UpdatedAt = DateTimeOffset.UtcNow;

            Audit(patient.Id, doctorId.Value, "CONSULTATION_FINALIZED", consultation.Id.ToString());

            // Optional Follow-up Appointment Auto-Creation
            if (request.FollowUpDate.HasValue)
            {
                var followUpExists = await _db.Appointments.AnyAsync(a =>
                    a.PatientId == patient.Id &&
                    a.DoctorId == doctorId.Value &&
                    a.Date == request.FollowUpDate.Value, ct);

                if (!followUpExists)
                {
                    _db.Appointments.Add(new Appointment
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        ClinicId = appointment.ClinicId,
                        DoctorId = doctorId.Value,
                        PatientId = patient.Id,
                        Date = request.FollowUpDate.Value,
                        TimeSlot = "10:00",
                        Type = AppointmentType.Scheduled,
                        Status = AppointmentStatus.Booked,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // Lost the race against another finalize: the unique (appointment, version) index won.
                if (tx != null) await tx.RollbackAsync(ct);
                return Conflict(new { error = "consultation_already_finalized" });
            }

            if (tx != null) await tx.CommitAsync(ct);

            _logger.LogInformation("Consultation {ConsultationId} finalized & signed by Doctor {DoctorId}. Appointment {AppointmentId} marked Completed.",
                consultation.Id, doctorId.Value, appointmentId);

            return Ok(new
            {
                consultationId = consultation.Id,
                version = consultation.Version,
                status = "finalized",
                signedAt = consultation.SignedAt,
                doctorName = doctorUser?.Name,
                doctorRegistrationNumber = consultation.DoctorRegistrationNumber,
                doctorQualifications = consultation.DoctorQualifications,
                digitalSignatureHash = consultation.DigitalSignatureHash,
                verificationQrCode = consultation.VerificationQrCode,
                verificationUrl = $"/api/v1/prescriptions/verify/{consultation.VerificationQrCode}",
                appointmentStatus = "completed",
                warnings = safetyWarnings
            });
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(ct);
            throw;
        }
    }

    #endregion

    #region Non-Destructive Clinical Amendment

    /// <summary>
    /// Amends a finalized consultation. Silently editing clinical data is forbidden (FR-14/FR-15).
    /// Creates a new immutable version with mandatory reason, author, and timestamp. Original is preserved.
    /// </summary>
    [HttpPost("consultations/{consultationId:guid}/amend")]
    [AuthorizeRoles("Doctor")]
    [RequirePermission(Permissions.ConsultationWrite)]
    public async Task<IActionResult> AmendConsultation(
        Guid consultationId,
        [FromBody] AmendConsultationRequest request,
        CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(request.AmendmentReason) || request.AmendmentReason.Trim().Length < 5)
            return BadRequest(new { error = "amendment_reason_required", message = "Mandatory clinical amendment rationale must be specified (min 5 characters)." });

        var existing = await _db.Consultations
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(c => c.Id == consultationId, ct);

        if (existing is null) return NotFound(new { error = "consultation_not_found" });

        // A draft was never signed, so there is nothing to amend — keep editing the draft.
        if (existing.Status == ConsultationStatus.Draft)
            return BadRequest(new { error = "cannot_amend_draft", message = "Drafts are editable; amendments apply to signed records." });

        var authorUser = await _db.Users.FindAsync([existing.DoctorId], ct);
        var currentUser = await _db.Users.FindAsync([doctorId.Value], ct);

        if (authorUser?.ClinicId is null || currentUser?.ClinicId is null || authorUser.ClinicId != currentUser.ClinicId)
            return Forbid();

        var newId = Guid.NewGuid();
        var signedAt = SecondPrecision(DateTimeOffset.UtcNow);
        var newVersion = new Consultation
        {
            Id = newId,
            AppointmentId = existing.AppointmentId,
            DoctorId = doctorId.Value,
            ChiefComplaint = request.ChiefComplaint ?? existing.ChiefComplaint,
            Observations = request.Observations ?? existing.Observations,
            Diagnosis = request.Diagnosis ?? existing.Diagnosis,
            AdvisedInvestigations = request.AdvisedInvestigations ?? existing.AdvisedInvestigations,
            IcdCode = request.IcdCode ?? existing.IcdCode,
            IcdTitle = request.IcdTitle ?? existing.IcdTitle,
            FollowUpDate = request.FollowUpDate ?? existing.FollowUpDate,
            FollowUpNotes = request.FollowUpNotes ?? existing.FollowUpNotes,
            TemplateId = request.TemplateId ?? existing.TemplateId,
            TemplateVersion = request.TemplateVersion ?? existing.TemplateVersion,
            StructuredObservationsJson = request.StructuredObservationsJson ?? existing.StructuredObservationsJson,
            OdontogramJson = request.OdontogramJson ?? existing.OdontogramJson,
            TenantId = User.GetTenantId() ?? existing.TenantId,
            Status = ConsultationStatus.Amended,
            Version = existing.Version + 1,
            PreviousVersionId = existing.Id,
            AmendmentReason = request.AmendmentReason.Trim(),
            SignedAt = signedAt,
            SignedByDoctorId = doctorId.Value,
            DoctorRegistrationNumber = existing.DoctorRegistrationNumber,
            DoctorQualifications = existing.DoctorQualifications,
            VerificationQrCode = existing.VerificationQrCode,
            DigitalSignatureHash = SignatureHash(doctorId.Value, existing.AppointmentId, newId,
                request.Diagnosis ?? existing.Diagnosis, signedAt),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var previousItems = existing.Prescriptions.SelectMany(p => p.Items).ToList();
        var rx = new Prescription
        {
            Id = Guid.NewGuid(),
            TenantId = newVersion.TenantId,
            ConsultationId = newVersion.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        newVersion.Prescriptions.Add(rx);

        if (request.PrescriptionItems is null)
        {
            // Omitting the list means "keep the prescription", not "delete it". An amended
            // note without its medicines was silently erasing the active e-Rx.
            foreach (var item in previousItems)
                rx.Items.Add(CloneItem(item));
        }
        else
        {
            var drugSchedules = await LoadDrugSchedulesAsync(request.PrescriptionItems.Select(i => i.DrugId), ct);
            foreach (var item in request.PrescriptionItems)
                rx.Items.Add(BuildItem(item, drugSchedules));

            // Dispensed lines are pharmacy truth and are already in the patient's hands.
            foreach (var dispensed in previousItems.Where(i => i.IsDispensed))
                if (!rx.Items.Any(i => string.Equals(i.GenericName, dispensed.GenericName, StringComparison.OrdinalIgnoreCase)))
                    rx.Items.Add(CloneItem(dispensed));
        }

        _db.Consultations.Add(newVersion);

        var patientIdForAudit = await _db.Appointments
            .Where(a => a.Id == existing.AppointmentId)
            .Select(a => a.PatientId)
            .FirstAsync(ct);
        Audit(patientIdForAudit, doctorId.Value, "CONSULTATION_AMENDED", newVersion.Id.ToString());

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Two doctors amended the same version at once; the (AppointmentId, Version) index won.
            return Conflict(new { error = "consultation_amended_concurrently", version = existing.Version });
        }

        _logger.LogInformation("Consultation {OldId} amended to Version {Version} (NewId: {NewId}) with reason: {Reason}",
            existing.Id, newVersion.Version, newVersion.Id, request.AmendmentReason);

        return StatusCode(201, new
        {
            consultationId = newVersion.Id,
            version = newVersion.Version,
            previousVersionId = newVersion.PreviousVersionId,
            status = "amended",
            amendmentReason = newVersion.AmendmentReason,
            prescriptionItemCount = rx.Items.Count,
            createdAt = newVersion.CreatedAt
        });
    }

    #endregion

    #region Public Authenticity Verification (QR Code)

    /// <summary>
    /// Authenticity verification endpoint accessed via QR code on printed/PDF prescription.
    /// Lets external chemists and patients verify prescription authenticity without exposing clinical PHI.
    /// Detects if an amendment has superseded this prescription.
    /// </summary>
    [NonAction]
    public Task<IActionResult> VerifyPrescription(string verificationCode, CancellationToken ct)
        => VerifyPrescription(verificationCode, null, ct);

    [HttpGet("consultations/verify-rx/{verificationCode}")]
    [HttpGet("prescriptions/verify/{verificationCode}")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyPrescription(
        string verificationCode,
        [FromQuery] int? version = null,
        CancellationToken ct = default,
        [FromQuery] long? exp = null,
        [FromQuery] string? sig = null)
    {
        if (string.IsNullOrWhiteSpace(verificationCode))
            return BadRequest(new { error = "invalid_verification_code" });

        // Shared links carry exp+sig. A printed code carries neither: the code itself is the secret.
        if (exp.HasValue || !string.IsNullOrWhiteSpace(sig))
        {
            if (!exp.HasValue || string.IsNullOrWhiteSpace(sig))
                return BadRequest(new { error = "share_link_signature_required" });

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp.Value)
                return StatusCode(StatusCodes.Status410Gone, new { error = "share_link_expired" });

            if (!ShareSignatureValid(verificationCode, exp.Value, sig))
                return BadRequest(new { error = "share_link_invalid_signature" });
        }

        // Every signed version of the visit, newest first. Amendments reuse the code, so the
        // printed version (not the status) decides whether a chemist is looking at a superseded copy.
        var versions = await _db.Consultations
            .Include(c => c.Appointment).ThenInclude(a => a.Clinic)
            .Include(c => c.Appointment).ThenInclude(a => a.Patient)
            .Include(c => c.Doctor)
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .Where(c => c.VerificationQrCode == verificationCode && c.Status != ConsultationStatus.Draft)
            .OrderByDescending(c => c.Version)
            .ToListAsync(ct);

        if (versions.Count == 0)
            return NotFound(new { error = "prescription_not_found_or_unverified" });

        var latestVersion = versions[0];
        var consultation = version.HasValue
            ? versions.FirstOrDefault(c => c.Version == version.Value)
            : latestVersion;

        if (consultation is null)
            return NotFound(new { error = "prescription_version_not_found", latestVersion = latestVersion.Version });

        // Only an older printed copy is superseded. The newest row is the active prescription even
        // though an amendment leaves it in status Amended.
        var isSuperseded = consultation.Version < latestVersion.Version;

        var fullContentSeal = consultation.DigitalSignatureHash?.StartsWith("v2:", StringComparison.Ordinal) == true;
        var signatureValid = fullContentSeal && ContentSealValid(consultation);
        var legacySignatureValid = !fullContentSeal && consultation.SignedAt.HasValue &&
            string.Equals(consultation.DigitalSignatureHash,
                SignatureHash(consultation.DoctorId, consultation.AppointmentId, consultation.Id, consultation.Diagnosis, SecondPrecision(consultation.SignedAt.Value)),
                StringComparison.OrdinalIgnoreCase);
        var integrity = signatureValid ? "full_content" : legacySignatureValid ? "legacy_limited" : "invalid";

        return Ok(new
        {
            verified = signatureValid,
            status = !signatureValid ? integrity == "legacy_limited" ? "legacy_limited" : "invalid" : isSuperseded ? "superseded" : "valid",
            contentIntegrity = integrity,
            legacySignatureValid,
            isSuperseded,
            supersededOn = isSuperseded ? latestVersion.CreatedAt : (DateTimeOffset?)null,
            latestVersion = latestVersion.Version,
            currentVersion = consultation.Version,
            statusDescription = !signatureValid
                ? legacySignatureValid ? "Legacy identity-only seal: medicine and note content integrity cannot be verified." : "Clinical content integrity verification failed. Do not rely on this prescription; contact the clinic."
                : isSuperseded
                ? $"This prescription was superseded on {latestVersion.CreatedAt:yyyy-MM-dd} by amended version {latestVersion.Version}."
                : "This prescription is valid and active.",
            signatureValid,
            version = consultation.Version,
            amended = consultation.Status == ConsultationStatus.Amended,
            amendmentReason = consultation.AmendmentReason,
            verificationCode = consultation.VerificationQrCode,
            clinicName = consultation.Appointment?.Clinic?.Name,
            clinicAddress = consultation.Appointment?.Clinic?.Address,
            doctorName = consultation.Doctor?.Name,
            doctorRegistrationNumber = consultation.DoctorRegistrationNumber,
            doctorQualifications = consultation.DoctorQualifications,
            date = consultation.SignedAt ?? consultation.CreatedAt,
            patientInitials = consultation.Appointment?.Patient?.Name != null ? consultation.Appointment.Patient.Name[..Math.Min(2, consultation.Appointment.Patient.Name.Length)] + "***" : null,
            patientAge = consultation.Appointment?.Patient?.ApproxAge,
            patientGender = consultation.Appointment?.Patient?.Gender,
            digitalSignatureHash = consultation.DigitalSignatureHash,
            medicines = consultation.Prescriptions.SelectMany(p => p.Items).Select(i => new
            {
                genericName = i.GenericName,
                brandName = i.BrandName,
                dosageForm = i.DosageForm,
                strength = i.Strength,
                frequency = i.Frequency,
                duration = $"{i.Duration} {i.DurationUnit}",
                timing = i.Timing,
                quantity = i.TotalQuantity
            }).ToList()
        });
    }

    [HttpGet("prescriptions/{id:guid}/print")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist", "Pharmacist", "Nurse")]
    public async Task<IActionResult> PrintPrescription(Guid id, CancellationToken ct)
    {
        var prescription = await _db.Prescriptions
            .Include(p => p.Items)
            .Include(p => p.Consultation)
                .ThenInclude(c => c.Appointment)
                    .ThenInclude(a => a.Patient)
            .Include(p => p.Consultation)
                .ThenInclude(c => c.Appointment)
                    .ThenInclude(a => a.Clinic)
            .Include(p => p.Consultation)
                .ThenInclude(c => c.Doctor)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prescription == null)
            return NotFound(new { error = "prescription_not_found" });

        var c = prescription.Consultation;
        if (c != null && c.Status == ConsultationStatus.Draft)
            return BadRequest(new { error = "cannot_print_draft_prescription", message = "Prescription must be signed and finalized before printing." });

        var tenantId = User.GetTenantId() ?? Guid.Empty;
        var currentUserId = User.GetUserId() ?? Guid.Empty;

        var existingPrintsCount = await _db.PrescriptionPrintLogs.CountAsync(p => p.PrescriptionId == id, ct);
        var isDuplicate = existingPrintsCount > 0;

        _db.PrescriptionPrintLogs.Add(new PrescriptionPrintLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConsultationId = c?.Id ?? Guid.Empty,
            PrescriptionId = id,
            PrintCount = existingPrintsCount + 1,
            IsDuplicateCopy = isDuplicate,
            PrintedByUserId = currentUserId,
            PrintedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        var patient = c?.Appointment?.Patient;
        var clinic = c?.Appointment?.Clinic;
        var doctor = c?.Doctor;

        return Ok(new
        {
            prescriptionId = prescription.Id,
            consultationId = c?.Id,
            version = c?.Version ?? 1,
            isDuplicateCopy = isDuplicate,
            printCount = existingPrintsCount + 1,
            watermark = isDuplicate ? "DUPLICATE COPY" : null,
            clinic = new
            {
                name = clinic?.Name,
                address = clinic?.Address,
                phone = clinic?.Phone,
                licence20 = clinic?.PharmacyLicenceNumber20,
                licence21 = clinic?.PharmacyLicenceNumber21,
                gstNumber = clinic?.PharmacyGstNumber
            },
            doctor = new
            {
                name = doctor?.Name,
                registrationNumber = c?.DoctorRegistrationNumber,
                qualifications = c?.DoctorQualifications
            },
            patient = new
            {
                name = patient?.Name,
                phone = patient?.Phone,
                gender = patient?.Gender.ToString(),
                approxAge = patient?.ApproxAge,
                address = patient?.Address
            },
            consultationDate = c?.SignedAt ?? c?.CreatedAt,
            diagnosis = c?.Diagnosis,
            verificationQrCode = c?.VerificationQrCode,
            verificationUrl = $"/rx-verify/{c?.VerificationQrCode}?v={c?.Version ?? 1}",
            digitalSignatureHash = c?.DigitalSignatureHash,
            medicines = prescription.Items.Select(i => new
            {
                id = i.Id,
                genericName = i.GenericName,
                brandName = i.BrandName,
                dosageForm = i.DosageForm,
                strength = i.Strength,
                frequency = i.Frequency,
                duration = $"{i.Duration} {i.DurationUnit}",
                timing = i.Timing,
                instructions = i.SpecialInstructions,
                quantity = i.TotalQuantity
            }).ToList()
        });
    }

    [HttpPost("prescriptions/{id:guid}/share-link")]
    [AuthorizeRoles("ClinicAdmin", "Doctor", "Receptionist", "Pharmacist")]
    public async Task<IActionResult> GenerateShareLink(Guid id, CancellationToken ct)
    {
        var prescription = await _db.Prescriptions
            .Include(p => p.Consultation)
                .ThenInclude(c => c.Appointment)
                    .ThenInclude(a => a.Patient)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prescription == null)
            return NotFound(new { error = "prescription_not_found" });

        var c = prescription.Consultation;
        if (c != null && c.Status == ConsultationStatus.Draft)
            return BadRequest(new { error = "cannot_share_draft_prescription" });

        var patient = c?.Appointment?.Patient;
        if (patient != null)
        {
            var hasConsent = await _db.PatientConsents
                .AnyAsync(pc => pc.PatientId == patient.Id && pc.WithdrawnAt == null, ct);
            if (!hasConsent)
            {
                return BadRequest(new { error = "patient_whatsapp_consent_missing", message = "Patient has not provided active consent for electronic communication." });
            }
        }

        var code = c?.VerificationQrCode ?? prescription.Id.ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);

        var secret = ShareSecret;
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "share_link_not_configured",
                message = "Prescription:ShareSecret is not configured; signed share links are disabled."
            });

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"{code}:{expiresAt.ToUnixTimeSeconds()}")));

        var shareUrl = $"/rx-verify/{code}?v={c?.Version ?? 1}&exp={expiresAt.ToUnixTimeSeconds()}&sig={sig}";

        return Ok(new
        {
            shareUrl,
            expiresAt,
            patientPhone = patient?.Phone,
            patientName = patient?.Name,
            message = "expiring_share_link_created"
        });
    }

    #endregion

    #region Queries & Legacy Compatibility

    [HttpGet("consultations")]
    [AuthorizeRoles("ClinicAdmin", "Doctor")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? doctorId,
        [FromQuery] Guid? patientId,
        [FromQuery] Guid? appointmentId,
        [FromQuery] string? fromDate,
        [FromQuery] string? toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var userRole = User.GetUserRole();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var query = _db.Consultations
            .Include(c => c.Appointment).ThenInclude(a => a.Patient)
            .Include(c => c.Doctor)
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .AsQueryable();

        if (string.Equals(userRole, "Doctor", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => c.DoctorId == userId.Value);
        }
        else if (string.Equals(userRole, "ClinicAdmin", StringComparison.OrdinalIgnoreCase))
        {
            var user = await _db.Users.FindAsync([userId.Value], ct);
            if (user?.ClinicId != null)
                query = query.Where(c => c.Appointment.ClinicId == user.ClinicId);
        }

        if (doctorId.HasValue) query = query.Where(c => c.DoctorId == doctorId.Value);
        if (patientId.HasValue) query = query.Where(c => c.Appointment.PatientId == patientId.Value);
        if (appointmentId.HasValue) query = query.Where(c => c.AppointmentId == appointmentId.Value);

        if (DateOnly.TryParse(fromDate, out var from))
            query = query.Where(c => c.CreatedAt >= from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (DateOnly.TryParse(toDate, out var to))
            query = query.Where(c => c.CreatedAt < to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var total = await query.CountAsync(ct);

        var consultations = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                consultationId = c.Id,
                appointmentId = c.AppointmentId,
                patientName = c.Appointment.Patient.Name,
                patientPhone = c.Appointment.Patient.Phone,
                doctorName = c.Doctor.Name,
                chiefComplaint = c.ChiefComplaint,
                diagnosis = c.Diagnosis,
                status = c.Status.ToString().ToLower(),
                version = c.Version,
                previousVersionId = c.PreviousVersionId,
                createdAt = c.CreatedAt,
                signedAt = c.SignedAt,
                prescriptionCount = c.Prescriptions.Count
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, data = consultations });
    }

    [HttpGet("consultations/{consultationId:guid}")]
    [AuthorizeRoles("ClinicAdmin", "Doctor")]
    public async Task<IActionResult> Get(Guid consultationId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var userRole = User.GetUserRole();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var consultation = await _db.Consultations
            .Include(c => c.Appointment).ThenInclude(a => a.Patient)
            .Include(c => c.Doctor)
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(c => c.Id == consultationId, ct);

        if (consultation is null) return NotFound(new { error = "consultation_not_found" });

        if (string.Equals(userRole, "Doctor", StringComparison.OrdinalIgnoreCase) && consultation.DoctorId != userId.Value)
            return Forbid();

        return Ok(new
        {
            consultationId = consultation.Id,
            appointmentId = consultation.AppointmentId,
            status = consultation.Status.ToString().ToLower(),
            patient = new
            {
                id = consultation.Appointment.Patient.Id,
                name = consultation.Appointment.Patient.Name,
                phone = consultation.Appointment.Patient.Phone
            },
            doctor = new
            {
                id = consultation.Doctor.Id,
                name = consultation.Doctor.Name,
                registrationNumber = consultation.DoctorRegistrationNumber,
                qualifications = consultation.DoctorQualifications
            },
            chiefComplaint = consultation.ChiefComplaint,
            observations = consultation.Observations,
            diagnosis = consultation.Diagnosis,
            advisedInvestigations = consultation.AdvisedInvestigations,
            icdCode = consultation.IcdCode,
            icdTitle = consultation.IcdTitle,
            followUpDate = consultation.FollowUpDate,
            followUpNotes = consultation.FollowUpNotes,
            structuredObservationsJson = consultation.StructuredObservationsJson,
            odontogramJson = consultation.OdontogramJson,
            version = consultation.Version,
            previousVersionId = consultation.PreviousVersionId,
            amendmentReason = consultation.AmendmentReason,
            signedAt = consultation.SignedAt,
            digitalSignatureHash = consultation.DigitalSignatureHash,
            verificationQrCode = consultation.VerificationQrCode,
            createdAt = consultation.CreatedAt,
            prescriptions = consultation.Prescriptions.Select(p => new
            {
                prescriptionId = p.Id,
                createdAt = p.CreatedAt,
                items = p.Items.Select(i => new
                {
                    genericName = !string.IsNullOrWhiteSpace(i.GenericName) ? i.GenericName : i.MedicineText,
                    brandName = i.BrandName,
                    dosage = !string.IsNullOrWhiteSpace(i.Strength) ? i.Strength : i.DosageText,
                    frequency = !string.IsNullOrWhiteSpace(i.Frequency) ? i.Frequency : i.FrequencyText,
                    duration = i.Duration > 0 ? $"{i.Duration} {i.DurationUnit}" : i.DurationText,
                    timing = i.Timing,
                    totalQuantity = i.TotalQuantity,
                    specialInstructions = i.SpecialInstructions,
                    scheduleClass = i.ScheduleClass.ToString()
                }).ToList()
            }).ToList()
        });
    }

    [HttpGet("consultations/{consultationId:guid}/prescriptions")]
    [AuthorizeRoles("ClinicAdmin", "Doctor")]
    public async Task<IActionResult> GetPrescriptions(Guid consultationId, CancellationToken ct)
    {
        var consultation = await _db.Consultations
            .Include(c => c.Prescriptions).ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(c => c.Id == consultationId, ct);

        if (consultation is null) return NotFound(new { error = "consultation_not_found" });

        var prescriptions = consultation.Prescriptions.Select(p => new
        {
            prescriptionId = p.Id,
            createdAt = p.CreatedAt,
            items = p.Items.Select(i => new
            {
                genericName = !string.IsNullOrWhiteSpace(i.GenericName) ? i.GenericName : i.MedicineText,
                brandName = i.BrandName,
                dosage = !string.IsNullOrWhiteSpace(i.Strength) ? i.Strength : i.DosageText,
                frequency = !string.IsNullOrWhiteSpace(i.Frequency) ? i.Frequency : i.FrequencyText,
                duration = i.Duration > 0 ? $"{i.Duration} {i.DurationUnit}" : i.DurationText,
                timing = i.Timing,
                totalQuantity = i.TotalQuantity
            }).ToList()
        }).ToList();

        return Ok(new { consultationId, prescriptions });
    }

    /// <summary>
    /// Legacy CreateConsultation endpoint for backward-compatibility with existing tests.
    /// </summary>
    [HttpPost("appointments/{appointmentId:guid}/consultation")]
    [AuthorizeRoles("Doctor")]
    [RequirePermission(Permissions.ConsultationWrite)]
    public async Task<IActionResult> CreateConsultation(Guid appointmentId, [FromBody] CreateConsultationRequest request, CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await _db.Appointments.FindAsync([appointmentId], ct);
        if (appointment is null) return NotFound(new { error = "appointment_not_found" });
        if (appointment.DoctorId != doctorId.Value) return Forbid();

        // Legacy path still creates a signed row directly, so it must honour the same one-record rule.
        if (await _db.Consultations.AnyAsync(c => c.AppointmentId == appointmentId && c.Status != ConsultationStatus.Draft, ct))
            return Conflict(new { error = "consultation_already_finalized" });

        var version = 1;
        if (request.PreviousVersionId.HasValue)
        {
            var prev = await _db.Consultations.FindAsync([request.PreviousVersionId.Value], ct);
            if (prev is null) return BadRequest(new { error = "previous_version_not_found" });
            version = prev.Version + 1;
        }

        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointmentId,
            DoctorId = doctorId.Value,
            ChiefComplaint = request.ChiefComplaint,
            Observations = request.Observations,
            Diagnosis = request.Diagnosis,
            Status = ConsultationStatus.Finalized,
            Version = version,
            PreviousVersionId = request.PreviousVersionId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Consultations.Add(consultation);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new
        {
            consultationId = consultation.Id,
            version = consultation.Version,
            createdAt = consultation.CreatedAt
        });
    }

    /// <summary>
    /// Legacy AddPrescription endpoint for backward-compatibility with existing tests.
    /// </summary>
    [HttpPost("consultations/{consultationId:guid}/prescriptions")]
    [AuthorizeRoles("Doctor")]
    public async Task<IActionResult> AddPrescription(Guid consultationId, [FromBody] AddPrescriptionRequest request, CancellationToken ct)
    {
        var doctorId = User.GetUserId();
        if (!doctorId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var consultation = await _db.Consultations.FindAsync([consultationId], ct);
        if (consultation is null) return NotFound(new { error = "consultation_not_found" });

        // Append-only: signed records accept no further lines, only a reasoned amendment.
        if (consultation.Status != ConsultationStatus.Draft)
            return Conflict(new { error = "consultation_already_finalized", message = "Use POST /api/v1/consultations/{id}/amend." });

        if (consultation.DoctorId != doctorId.Value)
        {
            var authorUser = await _db.Users.FindAsync([consultation.DoctorId], ct);
            var currentUser = await _db.Users.FindAsync([doctorId.Value], ct);
            if (authorUser?.ClinicId is null || currentUser?.ClinicId is null || authorUser.ClinicId != currentUser.ClinicId)
                return Forbid();
        }

        var prescription = new Prescription
        {
            Id = Guid.NewGuid(),
            TenantId = consultation.TenantId,
            ConsultationId = consultationId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        foreach (var item in request.Items)
        {
            prescription.Items.Add(new PrescriptionItem
            {
                Id = Guid.NewGuid(),
                PrescriptionId = prescription.Id,
                MedicineText = item.Medicine,
                DosageText = item.Dosage,
                FrequencyText = item.Frequency,
                DurationText = item.Duration,
                GenericName = item.Medicine,
                Strength = item.Dosage,
                Frequency = item.Frequency
            });
        }

        _db.Prescriptions.Add(prescription);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, new { prescriptionId = prescription.Id });
    }

    /// <summary>
    /// Append-only clinical audit row for Section 3 events (claim, park, recall, draft, sign, amend).
    /// </summary>
    private void Audit(Guid patientId, Guid userId, string field, string newValue) =>
        _db.PatientAuditLogs.Add(new PatientAuditLog
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            ChangedBy = userId,
            FieldName = field,
            OldValue = string.Empty,
            NewValue = newValue,
            ChangedAt = DateTimeOffset.UtcNow
        });

    /// <summary>The treating doctor or a clinic admin may move a visit between queue states.</summary>
    private bool CanActOnVisit(Appointment appointment) =>
        appointment.DoctorId == User.GetUserId() ||
        string.Equals(User.GetUserRole(), "ClinicAdmin", StringComparison.OrdinalIgnoreCase);

    /// <summary>One timestamp, whole seconds, so the signature can be recomputed after a DB round-trip.</summary>
    private static DateTimeOffset SecondPrecision(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    /// <summary>Set by configuration; a blank secret disables signed share links (503).</summary>
    private string? ShareSecret => _config?["Prescription:ShareSecret"];

    /// <summary>HMAC-SHA256 over "{code}:{exp}" so a forwarded link cannot be edited to outlive its expiry.</summary>
    private bool ShareSignatureValid(string verificationCode, long exp, string sig)
    {
        var secret = ShareSecret;
        if (string.IsNullOrWhiteSpace(secret)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{verificationCode}:{exp}"));
        byte[] supplied;
        try { supplied = Convert.FromHexString(sig); }
        catch (FormatException) { return false; }

        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static string SignatureHash(
        Guid doctorId, Guid appointmentId, Guid consultationId, string? diagnosis, DateTimeOffset signedAt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{doctorId}|{appointmentId}|{consultationId}|{diagnosis}|{signedAt.ToUnixTimeSeconds()}")));

    private static string ContentHash(Consultation c)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            c.Id, c.TenantId, c.AppointmentId, PatientId = c.Appointment.PatientId, c.Appointment.ClinicId,
            c.DoctorId, c.SignedByDoctorId, SignedAt = c.SignedAt?.ToUnixTimeSeconds(),
            c.DoctorRegistrationNumber, c.DoctorQualifications, c.Version, c.PreviousVersionId,
            c.Status, c.AmendmentReason, c.VerificationQrCode,
            c.ChiefComplaint, c.Observations, c.Diagnosis, c.AdvisedInvestigations, c.IcdCode, c.IcdTitle,
            c.FollowUpDate, c.FollowUpNotes, c.TemplateId, c.TemplateVersion,
            StructuredObservations = CanonicalJson(c.StructuredObservationsJson), Odontogram = CanonicalJson(c.OdontogramJson),
            Medicines = c.Prescriptions.SelectMany(p => p.Items).OrderBy(i => i.Id).Select(i => new
            {
                i.Id, i.DrugId, i.GenericName, i.BrandName, i.DosageForm, i.Strength, i.Route, i.Frequency,
                i.Duration, i.DurationUnit, i.Timing, i.TotalQuantity, i.SpecialInstructions, i.ScheduleClass,
                i.IsUnverified, i.MedicineText, i.DosageText, i.FrequencyText, i.DurationText
            })
        });
        return "v2:" + Convert.ToHexString(SHA256.HashData(content));
    }

    private static bool ContentSealValid(Consultation consultation)
    {
        if (!consultation.SignedAt.HasValue || consultation.Appointment is null) return false;
        try { return string.Equals(consultation.DigitalSignatureHash, ContentHash(consultation), StringComparison.Ordinal); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static bool StructuredJsonValid(params string?[] values)
    {
        try { foreach (var value in values) CanonicalJson(value); return true; }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static string? CanonicalJson(string? value)
    {
        if (value is null) return null;
        using var document = JsonDocument.Parse(value);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonicalJson(writer, document.RootElement);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
                    throw new JsonException("Duplicate clinical JSON fields are not supported.");
                foreach (var property in properties) { writer.WritePropertyName(property.Name); WriteCanonicalJson(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var number)) writer.WriteRawValue(number.ToString("G29", CultureInfo.InvariantCulture));
                else writer.WriteNumberValue(element.GetDouble());
                break;
            default: element.WriteTo(writer); break;
        }
    }

    private Task<TenantMembership?> SigningMembershipAsync(Guid doctorId, Guid tenantId, Guid? clinicId, CancellationToken ct) =>
        _db.TenantMemberships.FirstOrDefaultAsync(m => m.UserId == doctorId && m.TenantId == tenantId &&
            m.ClinicId == clinicId && m.Role == UserRole.Doctor && m.Status == "active" &&
            m.RegistrationNumber != null && m.RegistrationNumber.Trim() != "" &&
            m.RegistrationNumber != "NMC-VERIFIED-PRACTITIONER" && m.Qualifications != null && m.Qualifications.Trim() != "", ct);

    private async Task<List<PrescriptionItem>> EffectiveItemsAsync(List<StructuredPrescriptionItemRequest>? requested,
        IEnumerable<PrescriptionItem> previous, CancellationToken ct)
    {
        var previousItems = previous.ToList();
        var schedules = await LoadDrugSchedulesAsync(requested is null ? previousItems.Select(i => i.DrugId) : requested.Select(i => i.DrugId), ct);
        if (requested is not null) return requested.Select(i => BuildItem(i, schedules)).ToList();
        var items = previousItems.Select(CloneItem).ToList();
        foreach (var item in items)
            if (item.DrugId is { } id && schedules.TryGetValue(id, out var schedule))
                item.ScheduleClass = item.ScheduleClass == ScheduleClass.NDPS ? ScheduleClass.NDPS : schedule;
        return items;
    }

    private async Task<(IActionResult? Error, List<object> Warnings)> ValidatePrescriptionAsync(
        Appointment appointment, List<PrescriptionItem> items, CancellationToken ct)
    {
        var warnings = new List<object>();
        if (items.Any(i => string.IsNullOrWhiteSpace(i.GenericName) || i.TotalQuantity <= 0 || i.Duration <= 0 ||
            i.DispensedQuantity < 0 || i.DispensedQuantity > i.TotalQuantity))
            return (BadRequest(new { error = "invalid_prescription_item", message = "Each medicine needs a name, positive duration and quantity, and consistent supplied quantity." }), warnings);
        var ndps = items.FirstOrDefault(i => i.ScheduleClass == ScheduleClass.NDPS);
        if (ndps is not null)
            return (BadRequest(new { error = "ndps_digital_prescribing_prohibited", message = $"Digital prescribing of NDPS substance '{ndps.GenericName}' is disabled. Official physical stationery and a wet signature are required." }), warnings);

        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == appointment.ClinicId, ct);
        var timezone = new TimezoneService();
        var today = timezone.GetClinicToday(clinic?.Timezone);
        var start = new DateTimeOffset(timezone.GetStartOfDayUtc(today, clinic?.Timezone));
        var end = new DateTimeOffset(timezone.GetStartOfDayUtc(today.AddDays(1), clinic?.Timezone));
        var vitals = await _db.PatientVitals
            .Where(v => v.PatientId == appointment.PatientId && v.RecordedAt >= start && v.RecordedAt < end)
            .OrderByDescending(v => v.AppointmentId == appointment.Id).ThenByDescending(v => v.RecordedAt).FirstOrDefaultAsync(ct);
        var patient = appointment.Patient;
        var age = patient.Dob is { } dob ? today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0) : patient.ApproxAge;
        if (age is < 12 && items.Count > 0 && vitals?.WeightKg is not > 0)
            return (BadRequest(new { error = "pediatric_weight_required", message = "Pediatric prescribing requires a positive weight in today's encounter or patient vitals." }), warnings);

        var precheck = await _db.PrecheckSubmissions.FirstOrDefaultAsync(p => p.AppointmentId == appointment.Id, ct);
        var allergies = new[] { precheck?.Allergies, vitals?.Allergies }.Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => s!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            foreach (var allergy in allergies)
                if (allergy.Length >= 3 && $"{item.GenericName} {item.BrandName}".Contains(allergy, StringComparison.OrdinalIgnoreCase))
                    warnings.Add(new { type = "allergy_warning", drug = item.GenericName, allergenMatched = allergy, message = $"Prescribed medicine '{item.GenericName}' matches recorded allergy '{allergy}'." });
        foreach (var duplicate in items.GroupBy(i => i.GenericName.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            warnings.Add(new { type = "duplicate_drug_warning", genericName = duplicate.Key, message = $"Duplicate prescription of generic ingredient '{duplicate.Key}'." });
        return (null, warnings);
    }

    private static bool SameMedicine(PrescriptionItem old, PrescriptionItem item) =>
        old.DrugId == item.DrugId &&
        string.Equals(old.GenericName.Trim(), item.GenericName.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(old.BrandName?.Trim(), item.BrandName?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(old.Strength.Trim(), item.Strength.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(old.DosageForm.Trim(), item.DosageForm.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(old.Route.Trim(), item.Route.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void CarryFulfillment(PrescriptionItem source, PrescriptionItem target)
    {
        target.DispensedQuantity = source.DispensedQuantity;
        target.IsDispensed = source.IsDispensed;
        target.DispensedBatchId = source.DispensedBatchId;
        target.SubstitutedDrugId = source.SubstitutedDrugId;
        target.SubstitutionReason = source.SubstitutionReason;
    }

    /// <summary>Catalogue schedule classes for the ordered drugs; empty when nothing is linked.</summary>
    private async Task<Dictionary<Guid, ScheduleClass>> LoadDrugSchedulesAsync(
        IEnumerable<Guid?> drugIds, CancellationToken ct)
    {
        var ids = drugIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await _db.Drugs
            .AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.ScheduleClass, ct);
    }

    /// <summary>
    /// Single mapping for every prescription write path (draft, finalize, amend). The schedule
    /// class comes from the drug catalogue when the line is linked, so a client cannot omit or
    /// downplay it.
    /// </summary>
    private static PrescriptionItem BuildItem(
        StructuredPrescriptionItemRequest item, IReadOnlyDictionary<Guid, ScheduleClass> drugSchedules)
    {
        // An open-source (CDCI) hit has no Drugs row. Keeping its fabricated id would violate
        // FK_PrescriptionItems_Drugs_Id on finalize, so the line is stored unlinked and unverified.
        var catalogued = item.DrugId is { } id && drugSchedules.ContainsKey(id);

        return new()
        {
            Id = Guid.NewGuid(),
            DrugId = catalogued ? item.DrugId : null,
            GenericName = item.GenericName?.Trim() ?? string.Empty,
            BrandName = item.BrandName?.Trim(),
            DosageForm = item.DosageForm?.Trim() ?? "Tablet",
            Strength = item.Strength?.Trim() ?? string.Empty,
            Route = item.Route?.Trim() ?? "Oral",
            Frequency = item.Frequency?.Trim() ?? "1-0-1",
            Duration = item.Duration ?? 5,
            DurationUnit = item.DurationUnit?.Trim() ?? "Days",
            Timing = item.Timing?.Trim() ?? "After food",
            TotalQuantity = item.TotalQuantity ?? ((item.Duration ?? 5) * 2),
            SpecialInstructions = item.SpecialInstructions?.Trim(),
            ScheduleClass = ParseScheduleClass(item.ScheduleClass) == ScheduleClass.NDPS ? ScheduleClass.NDPS : catalogued
                ? drugSchedules[item.DrugId!.Value]
                : ParseScheduleClass(item.ScheduleClass),
            IsUnverified = item.IsUnverified ?? (!catalogued && item.DrugId.HasValue),
            MedicineText = $"{item.GenericName} {item.Strength}".Trim(),
            DosageText = item.Strength ?? string.Empty,
            FrequencyText = item.Frequency ?? "1-0-1",
            DurationText = $"{item.Duration ?? 5} {item.DurationUnit ?? "Days"}"
        };
    }

    /// <summary>Copy of a prior line for a new version; dispensed state is carried, identity is not.</summary>
    private static PrescriptionItem CloneItem(PrescriptionItem item) => new()
    {
        Id = Guid.NewGuid(),
        DrugId = item.DrugId,
        GenericName = item.GenericName,
        BrandName = item.BrandName,
        DosageForm = item.DosageForm,
        Strength = item.Strength,
        Route = item.Route,
        Frequency = item.Frequency,
        Duration = item.Duration,
        DurationUnit = item.DurationUnit,
        Timing = item.Timing,
        TotalQuantity = item.TotalQuantity,
        SpecialInstructions = item.SpecialInstructions,
        ScheduleClass = item.ScheduleClass,
        IsUnverified = item.IsUnverified,
        IsDispensed = item.IsDispensed,
        DispensedQuantity = item.DispensedQuantity,
        DispensedBatchId = item.DispensedBatchId,
        SubstitutedDrugId = item.SubstitutedDrugId,
        SubstitutionReason = item.SubstitutionReason,
        MedicineText = item.MedicineText,
        DosageText = item.DosageText,
        FrequencyText = item.FrequencyText,
        DurationText = item.DurationText
    };

    private static ScheduleClass ParseScheduleClass(string? sched)
    {
        if (string.IsNullOrWhiteSpace(sched)) return ScheduleClass.General;
        if (Enum.TryParse<ScheduleClass>(sched, true, out var result)) return result;
        return ScheduleClass.General;
    }

    #endregion

    #region Server-Side PDF & Print Verification

    /// <summary>
    /// Generates server-side PDF 1.4 for a finalized prescription with vector QR verification code.
    /// First print is marked original; subsequent prints are marked with DUPLICATE COPY watermark.
    /// Every print event is logged to the append-only PrescriptionPrintLogs table.
    /// </summary>
    [HttpGet("prescriptions/{id:guid}/pdf")]
    [Authorize]
    public async Task<IActionResult> GetPrescriptionPdf(Guid id, [FromServices] IPdfService pdfService, CancellationToken ct)
    {
        var prescription = await _db.Prescriptions
            .Include(p => p.Items)
            .Include(p => p.Consultation)
                .ThenInclude(c => c!.Doctor)
            .Include(p => p.Consultation)
                .ThenInclude(c => c!.Appointment)
                    .ThenInclude(a => a!.Patient)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prescription is null)
            return NotFound(new { error = "prescription_not_found" });

        if (prescription.Consultation != null && prescription.Consultation.Status == ConsultationStatus.Draft)
        {
            return BadRequest(new
            {
                error = "cannot_print_draft_prescription",
                message = "Prescription must be finalized before printing or generating PDF."
            });
        }

        var tenantId = prescription.Consultation?.Appointment?.TenantId ?? User.GetTenantId() ?? Guid.Empty;
        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var priorPrints = await _db.PrescriptionPrintLogs
            .CountAsync(l => l.PrescriptionId == id, ct);

        bool isDuplicate = priorPrints > 0;

        var printLog = new PrescriptionPrintLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConsultationId = prescription.ConsultationId,
            PrescriptionId = prescription.Id,
            PrintCount = priorPrints + 1,
            IsDuplicateCopy = isDuplicate,
            PrintedByUserId = User.GetUserId() ?? Guid.Empty,
            PrintedAt = DateTimeOffset.UtcNow
        };
        _db.PrescriptionPrintLogs.Add(printLog);
        await _db.SaveChangesAsync(ct);

        var pdfBytes = pdfService.GeneratePrescriptionPdf(prescription, prescription.Consultation!, clinic, isDuplicate);
        var filename = $"Prescription_{prescription.Id:N}_{(isDuplicate ? "DUPLICATE" : "ORIGINAL")}.pdf";

        return File(pdfBytes, "application/pdf", filename);
    }

    /// <summary>
    /// Alternate route: lookup prescription by consultation ID and return PDF.
    /// </summary>
    [HttpGet("consultations/{consultationId:guid}/prescription/pdf")]
    [Authorize]
    public async Task<IActionResult> GetConsultationPrescriptionPdf(Guid consultationId, [FromServices] IPdfService pdfService, CancellationToken ct)
    {
        var prescription = await _db.Prescriptions
            .Where(p => p.ConsultationId == consultationId)
            .OrderByDescending(p => p.Id)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (prescription == Guid.Empty)
            return NotFound(new { error = "prescription_not_found_for_consultation" });

        return await GetPrescriptionPdf(prescription, pdfService, ct);
    }

    #endregion
}

#region Request Contracts

public record StructuredPrescriptionItemRequest(
    Guid? DrugId,
    [Required] string GenericName,
    string? BrandName,
    string? DosageForm,
    string? Strength,
    string? Route,
    string? Frequency,
    int? Duration,
    string? DurationUnit,
    string? Timing,
    int? TotalQuantity,
    string? SpecialInstructions,
    string? ScheduleClass,
    bool? IsUnverified,
    Guid? PrescriptionItemId = null
);

public record SaveConsultationDraftRequest(
    string? ChiefComplaint,
    string? Observations,
    string? Diagnosis,
    string? AdvisedInvestigations,
    string? IcdCode,
    string? IcdTitle,
    DateOnly? FollowUpDate,
    string? FollowUpNotes,
    Guid? TemplateId,
    int? TemplateVersion,
    string? StructuredObservationsJson,
    List<StructuredPrescriptionItemRequest>? PrescriptionItems,
    string? OdontogramJson = null
);

public record FinalizeConsultationRequest(
    string? ChiefComplaint,
    string? Observations,
    string? Diagnosis,
    string? AdvisedInvestigations,
    string? IcdCode,
    string? IcdTitle,
    DateOnly? FollowUpDate,
    string? FollowUpNotes,
    Guid? TemplateId,
    int? TemplateVersion,
    string? StructuredObservationsJson,
    List<StructuredPrescriptionItemRequest>? PrescriptionItems,
    List<string>? OrderedLabTests,
    string? OdontogramJson = null
);

public record AmendConsultationRequest(
    [Required] string AmendmentReason,
    string? ChiefComplaint,
    string? Observations,
    string? Diagnosis,
    string? AdvisedInvestigations,
    string? IcdCode,
    string? IcdTitle,
    DateOnly? FollowUpDate,
    string? FollowUpNotes,
    Guid? TemplateId,
    int? TemplateVersion,
    string? StructuredObservationsJson,
    List<StructuredPrescriptionItemRequest>? PrescriptionItems,
    string? OdontogramJson = null
);

public record CreateConsultationRequest(
    [StringLength(1000, MinimumLength = 3, ErrorMessage = "Chief complaint must be between 3 and 1000 characters")]
    string? ChiefComplaint,

    [StringLength(2500, ErrorMessage = "Observations cannot exceed 2500 characters")]
    string? Observations,

    [StringLength(500, MinimumLength = 2, ErrorMessage = "Diagnosis must be between 2 and 500 characters")]
    string? Diagnosis,

    Guid? PreviousVersionId);

public record AddPrescriptionRequest(
    [Required, MinLength(1, ErrorMessage = "At least one prescription item is required"), MaxLength(30, ErrorMessage = "Maximum 30 prescription items")]
    List<PrescriptionItemRequest> Items);

public record PrescriptionItemRequest(
    [Required(ErrorMessage = "Medicine name is required"), StringLength(200, MinimumLength = 2, ErrorMessage = "Medicine name must be 2–200 characters")]
    string Medicine,

    [Required(ErrorMessage = "Dosage is required"), StringLength(100, MinimumLength = 1)]
    string Dosage,

    [Required(ErrorMessage = "Frequency is required"), StringLength(100, MinimumLength = 1)]
    string Frequency,

    [Required(ErrorMessage = "Duration is required"), StringLength(100, MinimumLength = 1)]
    string Duration);

#endregion
