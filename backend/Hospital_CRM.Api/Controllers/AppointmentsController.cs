using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Constants;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Services;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly PrecheckService _precheckService;
    private readonly ITimezoneService _timezoneService;
    private readonly IModuleGateService _moduleGate;
    private readonly ITenantContext? _tenantContext;

    public AppointmentsController(
        HospitalCrmDbContext db,
        INotificationService notificationService,
        PrecheckService precheckService,
        ITimezoneService timezoneService,
        IModuleGateService moduleGate,
        ITenantContext? tenantContext = null)
    {
        _db = db;
        _notificationService = notificationService;
        _precheckService = precheckService;
        _timezoneService = timezoneService;
        _moduleGate = moduleGate;
        _tenantContext = tenantContext;
    }

    [HttpPost]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> Book([FromBody] BookAppointmentRequest request, CancellationToken ct)
    {
        if (!DateOnly.TryParse(request.Date, out var appointmentDate))
            return BadRequest(new { error = "invalid_date_format" });

        var membership = await SelectedMembershipAsync(ct);
        if (membership?.Clinic is not { } clinic || membership.Role == UserRole.Nurse)
            return Forbid();

        var executionStrategy = _db.Database.CreateExecutionStrategy();
        Guid? intentId = null;
        var result = await executionStrategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            var validationError = await ValidateBookingAsync(_db, clinic.TenantId, clinic.Id,
                request.PatientId, request.DoctorId, appointmentDate, request.TimeSlot, null, ct);
            if (validationError != null)
                return BookingError(validationError);

            var typeEnum = string.Equals(request.Type, "walkin", StringComparison.OrdinalIgnoreCase)
                ? AppointmentType.WalkIn
                : AppointmentType.Scheduled;

            var appointment = new Appointment
            {
                Id = Guid.NewGuid(),
                TenantId = clinic.TenantId,
                PatientId = request.PatientId,
                DoctorId = request.DoctorId,
                ClinicId = clinic.Id,
                Date = appointmentDate,
                TimeSlot = request.TimeSlot,
                Type = typeEnum,
                Status = AppointmentStatus.Booked,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.Appointments.Add(appointment);

            // Save appointment first so PrecheckService can query it (it checks type and lead time)
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // Two receptionists booked the same slot: the DB index is the referee.
                return Conflict(new { error = "slot_already_booked" });
            }

            // FR-23-01: pre-check token generation inside the transaction,
            // committed atomically with the appointment via the single transaction.
            string? precheckPlaintext = null;
            if (await _moduleGate.IsModuleEnabledAsync(clinic.TenantId, ModuleRegistry.Precheck, ct))
            {
                var (plaintext, precheckSubmission) = await _precheckService.GenerateForAppointmentAsync(
                    appointment.Id, clinic.TenantId, ct);
                precheckPlaintext = plaintext;
                if (precheckSubmission is not null)
                {
                    _db.PrecheckSubmissions.Add(precheckSubmission);
                }
            }
            var hasWhatsAppOptIn = await _db.PatientConsents.AnyAsync(c => c.TenantId == clinic.TenantId &&
                c.PatientId == appointment.PatientId && c.Purpose == "whatsapp_opt_in" && c.WithdrawnAt == null, ct);
            NotificationIntent? intent = null;
            if (hasWhatsAppOptIn)
            {
                intent = _notificationService.CreateConfirmationIntent(appointment,
                    precheckPlaintext is null ? null : _precheckService.BuildLink(precheckPlaintext));
                _db.Set<NotificationIntent>().Add(intent);
            }
            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            intentId = intent?.Id;

            return StatusCode(201, new
            {
                appointmentId = appointment.Id,
                status = "booked",
                queueToken = (int?)null
            });
        });
        // Handoff errors must never replay a committed booking through the execution strategy.
        if (intentId.HasValue)
        {
            using var handoff = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handoff.CancelAfter(TimeSpan.FromSeconds(5));
            await _notificationService.HandoffConfirmationAsync(intentId.Value, handoff.Token);
        }
        return result;
    }

    [HttpPost("{id:guid}/check-in")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> CheckIn(Guid id, CancellationToken ct)
    {
        var membership = await SelectedMembershipAsync(ct);
        if (membership is null || membership.Role == UserRole.Nurse) return Forbid();
        var executionStrategy = _db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;

            var appointment = await SelectedAppointments(membership).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (appointment is null)
                return NotFound(new { error = "appointment_not_found" });

            // Idempotent: return already assigned tokens on double check-in
            if (appointment.QueueToken.HasValue)
            {
                return Ok(new
                {
                    status = formatStatus(appointment.Status),
                    queueToken = appointment.QueueToken,
                    trackingToken = appointment.QueueTrackingToken
                });
            }

            if (appointment.Status == AppointmentStatus.Completed ||
                appointment.Status == AppointmentStatus.Cancelled ||
                appointment.Status == AppointmentStatus.NoShow)
            {
                return BadRequest(new { error = "cannot_check_in_terminal_appointment" });
            }

            var clinic = await _db.Clinics.FindAsync([appointment.ClinicId], ct);
            var today = _timezoneService.GetClinicToday(clinic?.Timezone, HttpContext);

            int nextToken;
            if (_db.Database.IsRelational())
            {
                // Atomic counter per (tenant, doctor, date) via UPDATE RETURNING
                var tokens = await _db.Database.SqlQuery<int>($@"
                        INSERT INTO ""DoctorDailyQueueCounters"" (""Id"", ""TenantId"", ""DoctorId"", ""Date"", ""LastToken"", ""UpdatedAt"")
                        VALUES ({Guid.NewGuid()}, {appointment.TenantId}, {appointment.DoctorId}, {today}, 1, now())
                        ON CONFLICT (""TenantId"", ""DoctorId"", ""Date"")
                        DO UPDATE SET ""LastToken"" = ""DoctorDailyQueueCounters"".""LastToken"" + 1, ""UpdatedAt"" = now()
                        RETURNING ""LastToken"" AS ""Value""")
                    .ToListAsync(ct);
                nextToken = tokens.Single();
            }
            else
            {
                var counter = await _db.DoctorDailyQueueCounters
                    .FirstOrDefaultAsync(c => c.TenantId == appointment.TenantId && c.DoctorId == appointment.DoctorId && c.Date == today, ct);
                if (counter is null)
                {
                    counter = new DoctorDailyQueueCounter
                    {
                        Id = Guid.NewGuid(),
                        TenantId = appointment.TenantId,
                        DoctorId = appointment.DoctorId,
                        Date = today,
                        LastToken = 1,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    _db.DoctorDailyQueueCounters.Add(counter);
                }
                else
                {
                    counter.LastToken++;
                    counter.UpdatedAt = DateTimeOffset.UtcNow;
                }
                nextToken = counter.LastToken;
            }

            var trackingBytes = RandomNumberGenerator.GetBytes(16);
            var trackingToken = "qtrk_" + Convert.ToHexString(trackingBytes).ToLowerInvariant();

            appointment.Status = AppointmentStatus.CheckedIn;
            appointment.QueueToken = nextToken;
            appointment.QueueTrackingToken = trackingToken;
            appointment.UpdatedAt = DateTimeOffset.UtcNow;
            appointment.QueueTrackingExpiresAt = appointment.UpdatedAt.AddHours(24);

            await _db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);

            return Ok(new
            {
                status = "checked_in",
                queueToken = appointment.QueueToken,
                trackingToken = appointment.QueueTrackingToken
            });
        });
    }

    private static readonly Dictionary<AppointmentStatus, HashSet<AppointmentStatus>> AllowedTransitions = new()
    {
        [AppointmentStatus.Booked] = [AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.Confirmed] = [AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.CheckedIn] = [AppointmentStatus.InNursing, AppointmentStatus.InConsultation, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.InNursing] = [AppointmentStatus.InConsultation, AppointmentStatus.Completed, AppointmentStatus.Cancelled, AppointmentStatus.Parked],
        [AppointmentStatus.InConsultation] = [AppointmentStatus.Completed, AppointmentStatus.Parked, AppointmentStatus.Cancelled],
        [AppointmentStatus.Parked] = [AppointmentStatus.InConsultation, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
        [AppointmentStatus.Completed] = [], // Terminal
        [AppointmentStatus.Cancelled] = [], // Terminal
        [AppointmentStatus.NoShow] = []     // Terminal
    };

    [HttpPut("{id:guid}")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Nurse")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAppointmentRequest request, CancellationToken ct)
    {
        var membership = await SelectedMembershipAsync(ct);
        if (membership is null) return Forbid();
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var appointment = await SelectedAppointments(membership).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appointment is null)
            return NotFound(new { error = "appointment_not_found" });

        var newStatus = parseStatus(request.Status);
        if (newStatus is null)
            return BadRequest(new { error = "invalid_status" });

        // A nurse runs the nursing station (ready for doctor, back to queue), not completion.
        if (string.Equals(User.GetUserRole(), "Nurse", StringComparison.OrdinalIgnoreCase) &&
            newStatus is not (AppointmentStatus.InNursing or AppointmentStatus.InConsultation))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "insufficient_permissions" });
        }

        if (appointment.Status != newStatus.Value)
        {
            if (!AllowedTransitions.TryGetValue(appointment.Status, out var allowed) || !allowed.Contains(newStatus.Value))
            {
                return BadRequest(new
                {
                    error = "invalid_status_transition",
                    from = formatStatus(appointment.Status),
                    to = formatStatus(newStatus.Value),
                    message = $"Status transition from '{formatStatus(appointment.Status)}' to '{formatStatus(newStatus.Value)}' is not allowed."
                });
            }

            var history = new AppointmentHistory
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointment.Id,
                PreviousDate = appointment.Date,
                PreviousTimeSlot = appointment.TimeSlot,
                PreviousStatus = appointment.Status,
                ChangedBy = userId.Value,
                ChangedAt = DateTimeOffset.UtcNow
            };

            _db.AppointmentHistories.Add(history);
            appointment.Status = newStatus.Value;
            appointment.UpdatedAt = DateTimeOffset.UtcNow;
            if (appointment.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
                appointment.EndQueueTracking(history.ChangedAt);

            await _db.SaveChangesAsync(ct);
        }

        return Ok(new { appointmentId = appointment.Id, status = formatStatus(appointment.Status) });
    }

    [HttpPost("{id:guid}/reschedule")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin")]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleAppointmentRequest request, CancellationToken ct)
    {
        var membership = await SelectedMembershipAsync(ct);
        if (membership is null || membership.Role == UserRole.Nurse) return Forbid();
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized(new { error = "invalid_token" });

        var appointment = await SelectedAppointments(membership).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appointment is null) return NotFound(new { error = "appointment_not_found" });

        if (appointment.Status == AppointmentStatus.Completed ||
            appointment.Status == AppointmentStatus.Cancelled)
        {
            return BadRequest(new { error = "cannot_reschedule_terminal_appointment" });
        }

        if (!DateOnly.TryParse(request.NewDate, out var newDate))
            return BadRequest(new { error = "invalid_date_format" });

        var validationError = await ValidateBookingAsync(_db, appointment.TenantId, appointment.ClinicId,
            appointment.PatientId, appointment.DoctorId, newDate, request.NewTimeSlot, appointment.Id, ct);
        if (validationError != null)
            return BookingError(validationError);

        _db.AppointmentHistories.Add(new AppointmentHistory
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            PreviousDate = appointment.Date,
            PreviousTimeSlot = appointment.TimeSlot,
            PreviousStatus = appointment.Status,
            ChangedBy = userId.Value,
            ChangedAt = DateTimeOffset.UtcNow
        });

        appointment.Date = newDate;
        appointment.TimeSlot = request.NewTimeSlot;
        appointment.QueueToken = null;
        appointment.QueueTrackingToken = null;
        appointment.QueueTrackingExpiresAt = null;
        appointment.Status = AppointmentStatus.Booked;
        appointment.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Lost the race to another reschedule/booking for the same slot.
            return Conflict(new { error = "slot_already_booked" });
        }
        return Ok(new
        {
            appointmentId = appointment.Id,
            date = appointment.Date.ToString("yyyy-MM-dd"),
            timeSlot = appointment.TimeSlot,
            status = "booked"
        });
    }

    [HttpGet]
    [Authorize]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Nurse")]
    public async Task<IActionResult> List([FromQuery] string? date, [FromQuery] Guid? doctorId, [FromQuery] Guid? patientId, CancellationToken ct)
    {
        var membership = await SelectedMembershipAsync(ct);
        if (membership is null) return Forbid();
        var query = SelectedAppointments(membership)
            .Include(a => a.Patient)
            .Include(a => a.Doctor)
            .AsQueryable();

        if (DateOnly.TryParse(date, out var parsedDate))
            query = query.Where(a => a.Date == parsedDate);

        if (doctorId.HasValue)
            query = query.Where(a => a.DoctorId == doctorId.Value);

        if (patientId.HasValue)
            query = query.Where(a => a.PatientId == patientId.Value);

        var list = await query
            .OrderByDescending(a => a.Priority) // emergency (1) before normal (0)
            .ThenBy(a => a.Date)
            .ThenBy(a => a.TimeSlot)
            .Select(a => new
            {
                appointmentId = a.Id,
                patientId = a.PatientId,
                patientUhid = a.Patient.Uhid,
                patientName = a.Patient.Name,
                patientPhone = a.Patient.Phone,
                patientGender = a.Patient.Gender.ToString(),
                patientDob = a.Patient.Dob,
                patientAge = a.Patient.ApproxAge,
                doctorId = a.DoctorId,
                doctorName = a.Doctor.Name,
                date = a.Date,
                time = a.TimeSlot,
                status = formatStatus(a.Status),
                priority = a.Priority.ToString().ToLower(),
                queueToken = a.QueueToken,
                queueTrackingToken = a.QueueTrackingToken,
                triageReasonCode = a.TriageReasonCode,
                triageConfirmed = a.TriageConfirmedBy.HasValue,
                type = a.Type.ToString().ToLower()
            })
            .ToListAsync(ct);

        return Ok(list);
    }

    private static AppointmentStatus? parseStatus(string status)
    {
        return status.ToLowerInvariant().Replace("-", "_") switch
        {
            "booked" => AppointmentStatus.Booked,
            "confirmed" => AppointmentStatus.Confirmed,
            "checked_in" or "checkedin" => AppointmentStatus.CheckedIn,
            "in_nursing" or "innursing" => AppointmentStatus.InNursing,
            "in_consultation" or "inconsultation" => AppointmentStatus.InConsultation,
            "completed" => AppointmentStatus.Completed,
            "cancelled" => AppointmentStatus.Cancelled,
            "noshow" or "no_show" => AppointmentStatus.NoShow,
            "parked" => AppointmentStatus.Parked,
            _ => null
        };
    }

    private static string formatStatus(AppointmentStatus status) => status switch
    {
        AppointmentStatus.Booked => "booked",
        AppointmentStatus.Confirmed => "confirmed",
        AppointmentStatus.CheckedIn => "checked_in",
        AppointmentStatus.InNursing => "in_nursing",
        AppointmentStatus.InConsultation => "in_consultation",
        AppointmentStatus.Parked => "parked",
        AppointmentStatus.Completed => "completed",
        AppointmentStatus.Cancelled => "cancelled",
        AppointmentStatus.NoShow => "no_show",
        _ => status.ToString().ToLowerInvariant()
    };

    private static readonly HashSet<string> SevereTriageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chest_pain", "breathing_difficulty", "severe_trauma", "loss_of_consciousness", "severe_allergic_reaction"
    };

    private static readonly HashSet<string> ValidTriageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chest_pain", "breathing_difficulty", "severe_trauma", "loss_of_consciousness",
        "acute_abdomen", "high_fever_infant", "severe_allergic_reaction", "other_clinical_urgency"
    };

    [HttpPatch("{id:guid}/priority")]
    [AuthorizeRoles("Receptionist", "Doctor", "ClinicAdmin", "Nurse")]
    public async Task<IActionResult> UpdatePriority(Guid id, [FromBody] UpdatePriorityRequest request, CancellationToken ct)
    {
        var membership = await SelectedMembershipAsync(ct);
        if (membership is null) return Forbid();
        var appointment = await SelectedAppointments(membership).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appointment is null)
            return NotFound(new { error = "appointment_not_found" });

        if (!string.Equals(request.Priority, "emergency", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Priority, "urgent", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Priority, "normal", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "priority_must_be_emergency_urgent_or_normal" });

        var newPriority = string.Equals(request.Priority, "emergency", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(request.Priority, "urgent", StringComparison.OrdinalIgnoreCase)
            ? AppointmentPriority.Emergency
            : AppointmentPriority.Normal;

        var role = User.GetUserRole();
        var userId = User.GetUserId();

        // Front desk picks a structured clinical code. Free text was validation theater:
        // ten characters of anything used to pass as an emergency justification.
        if (newPriority == AppointmentPriority.Emergency &&
            string.Equals(role, nameof(UserRole.Receptionist), StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(request.TriageReasonCode))
        {
            return BadRequest(new { error = "triage_reason_code_required", validCodes = ValidTriageCodes });
        }

        if (!string.IsNullOrWhiteSpace(request.TriageReasonCode) && !ValidTriageCodes.Contains(request.TriageReasonCode))
        {
            return BadRequest(new { error = "invalid_triage_reason_code", validCodes = ValidTriageCodes });
        }

        appointment.Priority = newPriority;
        if (!string.IsNullOrWhiteSpace(request.TriageReasonCode))
        {
            appointment.TriageReasonCode = request.TriageReasonCode.ToLowerInvariant();
        }

        if (string.Equals(role, "Doctor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Nurse", StringComparison.OrdinalIgnoreCase) ||
            request.ConfirmTriage)
        {
            appointment.TriageConfirmedBy = userId;
            appointment.TriageConfirmedAt = DateTimeOffset.UtcNow;
        }

        _db.PriorityLogs.Add(new PriorityLog
        {
            Id = Guid.NewGuid(),
            AppointmentId = id,
            ChangedBy = userId!.Value,
            ChangedTo = newPriority.ToString().ToLower(),
            Reason = request.Reason?.Trim() ?? request.TriageReasonCode,
            ChangedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        string? referralWarning = null;
        if (appointment.TriageReasonCode != null && SevereTriageCodes.Contains(appointment.TriageReasonCode))
        {
            referralWarning = "Critical emergency condition suspected. Immediate hospital ER referral recommended.";
        }

        return Ok(new
        {
            appointmentId = id,
            priority = newPriority.ToString().ToLower(),
            triageReasonCode = appointment.TriageReasonCode,
            triageConfirmed = appointment.TriageConfirmedBy.HasValue,
            hospitalReferralAdvisory = referralWarning,
            unchanged = false
        });
    }

    /// <summary>FR-23-03 — Doctor reviews pre-check submission inline in consult flow.</summary>
    [HttpGet("{id:guid}/precheck")]
    [AuthorizeRoles("Doctor", "ClinicAdmin")]
    [RequireModule(ModuleRegistry.Precheck)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPrecheckSubmission(Guid id, CancellationToken ct)
    {
        var membership = await SelectedMembershipAsync(ct);
        if (membership is null || membership.Role is not (UserRole.Doctor or UserRole.ClinicAdmin))
            return Forbid();
        if (!await SelectedAppointments(membership).AnyAsync(a => a.Id == id &&
            (membership.Role == UserRole.ClinicAdmin || a.DoctorId == membership.UserId), ct))
            return NotFound(new { error = "appointment_not_found" });
        var submission = await _db.PrecheckSubmissions
            .FirstOrDefaultAsync(s => s.AppointmentId == id && s.TenantId == membership.TenantId, ct);

        if (submission is null || submission.SubmittedAt is null)
            return Ok(new { submitted = false });

        return Ok(new
        {
            submitted = true,
            submittedAt = submission.SubmittedAt,
            chiefComplaint = submission.ChiefComplaint,
            symptomDuration = submission.SymptomDuration,
            medications = submission.Medications,
            allergies = submission.Allergies
        });
    }

    /// <summary>FR-23-02 — Patient submits pre-check form (unauthenticated, tokenized with DOB verification).</summary>
    [HttpPost("/api/v1/precheck/{token}")]
    [PublicTokenEndpoint(PublicTokenPurpose.Precheck)]
    [AllowAnonymous]
    [EnableRateLimiting("precheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitPrecheck(string token, [FromBody] PrecheckSubmissionRequest request, CancellationToken ct)
    {
        if (_tenantContext is null || !PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Precheck, token))
            return BadRequest(new { error = "invalid_token" });

        var binding = await PublicTokenRouting.ResolveAsync(_db, PublicTokenPurpose.Precheck, token, ct);
        if (binding is null) return BadRequest(new { error = "invalid_token" });
        using var tenantScope = _tenantContext.PushTenant(binding.TenantId);
        var hash = token.HashToken();
        var submission = await _db.PrecheckSubmissions
            .AsNoTracking()
            .Include(s => s.Appointment)
                .ThenInclude(a => a.Patient)
            .FirstOrDefaultAsync(s => s.Id == binding.ResourceId && s.TenantId == binding.TenantId &&
                s.TokenHash == hash && s.Appointment.TenantId == binding.TenantId &&
                s.Appointment.Patient.TenantId == binding.TenantId, ct);

        if (submission?.Appointment?.Patient is null)
            return BadRequest(new { error = "invalid_token" });

        if (!await _moduleGate.IsModuleEnabledAsync(submission.TenantId, ModuleRegistry.Precheck, ct))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "module_not_activated", module = ModuleRegistry.Precheck });

        if (submission.SubmittedAt is not null)
            return Ok(new { message = "already_submitted" });

        if (DateTimeOffset.UtcNow >= submission.ExpiresAt)
            return BadRequest(new { error = "token_expired" });

        // P2-C1 Identity binding: Verify submitted DOB matches patient record
        var patient = submission.Appointment?.Patient;
        if (patient != null && patient.DobHasValue && patient.Dob.HasValue)
        {
            if (string.IsNullOrWhiteSpace(request.DateOfBirth) ||
                !DateOnly.TryParse(request.DateOfBirth, out var submittedDob) ||
                submittedDob != patient.Dob.Value)
            {
                return BadRequest(new { error = "identity_verification_failed" });
            }
        }

        // Conditional update is the single-use boundary, including concurrent submissions.
        var now = DateTimeOffset.UtcNow;
        var updated = await _db.PrecheckSubmissions.Where(s => s.Id == binding.ResourceId &&
                s.TenantId == binding.TenantId && s.TokenHash == hash && s.SubmittedAt == null && s.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.SubmittedAt, now)
                .SetProperty(s => s.ChiefComplaint, request.ChiefComplaint)
                .SetProperty(s => s.SymptomDuration, request.SymptomDuration)
                .SetProperty(s => s.Medications, request.Medications)
                .SetProperty(s => s.Allergies, request.Allergies), ct);
        if (updated == 0)
        {
            var alreadySubmitted = await _db.PrecheckSubmissions.AnyAsync(s => s.Id == binding.ResourceId &&
                s.TenantId == binding.TenantId && s.TokenHash == hash && s.SubmittedAt != null, ct);
            return alreadySubmitted ? Ok(new { message = "already_submitted" }) : BadRequest(new { error = "token_expired" });
        }

        return Ok(new { message = "submitted" });
    }

    /// <summary>FR-23-02 - Validate a pre-check link without submitting (unauthenticated, same tokenisation).</summary>
    [HttpGet("/api/v1/precheck/{token}")]
    [PublicTokenEndpoint(PublicTokenPurpose.Precheck)]
    [AllowAnonymous]
    [EnableRateLimiting("precheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPrecheckStatus(string token, CancellationToken ct)
    {
        if (_tenantContext is null || !PublicTokenRouting.IsWellFormed(PublicTokenPurpose.Precheck, token))
            return BadRequest(new { error = "invalid_token" });

        var binding = await PublicTokenRouting.ResolveAsync(_db, PublicTokenPurpose.Precheck, token, ct);
        if (binding is null) return BadRequest(new { error = "invalid_token" });
        using var tenantScope = _tenantContext.PushTenant(binding.TenantId);
        var hash = token.HashToken();
        var submission = await _db.PrecheckSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == binding.ResourceId && s.TenantId == binding.TenantId &&
                s.TokenHash == hash && s.Appointment.TenantId == binding.TenantId &&
                s.Appointment.Patient.TenantId == binding.TenantId, ct);

        if (submission is null)
            return BadRequest(new { error = "invalid_token" });

        if (!await _moduleGate.IsModuleEnabledAsync(submission.TenantId, ModuleRegistry.Precheck, ct))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "module_not_activated", module = ModuleRegistry.Precheck });

        if (submission.SubmittedAt is not null)
            return Ok(new { message = "already_submitted" });

        if (DateTimeOffset.UtcNow >= submission.ExpiresAt)
            return BadRequest(new { error = "token_expired" });

        return Ok(new { message = "valid" });
    }

    private Task<TenantMembership?> SelectedMembershipAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        if (userId is null || tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty ||
            !Enum.TryParse<UserRole>(User.GetUserRole(), out var role) ||
            role is not (UserRole.Receptionist or UserRole.Doctor or UserRole.ClinicAdmin or UserRole.Nurse))
            return Task.FromResult<TenantMembership?>(null);
        return _db.TenantMemberships.AsNoTracking().Include(m => m.Clinic).SingleOrDefaultAsync(m =>
            m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active" &&
            m.Role == role && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
    }

    private IQueryable<Appointment> SelectedAppointments(TenantMembership membership) =>
        _db.Appointments.Where(a => a.TenantId == membership.TenantId && a.ClinicId == membership.ClinicId);

    private IActionResult BookingError(string error) => error switch
    {
        "slot_already_booked" => Conflict(new { error }),
        "patient_not_found" or "doctor_not_found" => NotFound(new { error }),
        _ => BadRequest(new { error })
    };

    /// <summary>Shared booking/follow-up safeguards; the caller owns the transaction and unique-slot conflict handling.</summary>
    public static async Task<string?> ValidateBookingAsync(HospitalCrmDbContext db, Guid tenantId, Guid clinicId,
        Guid patientId, Guid doctorId, DateOnly date, string timeSlot, Guid? excludingAppointmentId = null,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || clinicId == Guid.Empty)
            return "no_clinic_configured";
        if (!TimeOnly.TryParseExact(timeSlot, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            return "invalid_time_slot";
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.TenantId == tenantId, ct))
            return "patient_not_found";
        if (!await db.TenantMemberships.AnyAsync(m => m.UserId == doctorId && m.TenantId == tenantId &&
            m.ClinicId == clinicId && m.Status == "active" && m.Role == UserRole.Doctor, ct))
            return "doctor_not_found";
        var clinic = await db.Clinics.AsNoTracking().Include(c => c.WorkingHours).Include(c => c.Holidays)
            .Include(c => c.SpecialHours).SingleOrDefaultAsync(c => c.Id == clinicId && c.TenantId == tenantId, ct);
        if (clinic is null) return "no_clinic_configured";

        var requestDate = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var monthDay = date.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var holiday = clinic.Holidays.Any(h =>
        {
            if (h.RecurringAnnually && h.StartDate.Length >= 10 && h.EndDate.Length >= 10)
            {
                var start = h.StartDate.Substring(5, 5);
                var end = h.EndDate.Substring(5, 5);
                return string.CompareOrdinal(start, end) <= 0
                    ? string.CompareOrdinal(monthDay, start) >= 0 && string.CompareOrdinal(monthDay, end) <= 0
                    : string.CompareOrdinal(monthDay, start) >= 0 || string.CompareOrdinal(monthDay, end) <= 0;
            }
            return string.CompareOrdinal(requestDate, h.StartDate) >= 0 && string.CompareOrdinal(requestDate, h.EndDate) <= 0;
        });
        if (holiday) return "clinic_closed_on_selected_date";

        var special = clinic.SpecialHours.FirstOrDefault(s => s.Date == requestDate);
        if (special != null)
        {
            if (string.CompareOrdinal(timeSlot, special.OpenTime) < 0 || string.CompareOrdinal(timeSlot, special.CloseTime) >= 0)
                return "time_slot_outside_special_working_hours";
        }
        else if (clinic.WorkingHours.Count > 0)
        {
            var shifts = clinic.WorkingHours.Where(h => h.DayOfWeek == (int)date.DayOfWeek).ToList();
            if (shifts.Count == 0) return "clinic_closed_on_selected_day";
            if (!shifts.Any(s => string.CompareOrdinal(timeSlot, s.OpenTime) >= 0 && string.CompareOrdinal(timeSlot, s.CloseTime) < 0))
                return "time_slot_outside_working_hours";
        }
        else if (string.CompareOrdinal(timeSlot, "08:00") < 0 || string.CompareOrdinal(timeSlot, "20:00") >= 0)
            return "time_slot_outside_working_hours";

        return await db.Appointments.AnyAsync(a => a.TenantId == tenantId && a.ClinicId == clinicId &&
            a.DoctorId == doctorId && a.Date == date && a.TimeSlot == timeSlot && a.Id != excludingAppointmentId &&
            a.Status != AppointmentStatus.Cancelled, ct) ? "slot_already_booked" : null;
    }
}

public record PrecheckSubmissionRequest(
    string? DateOfBirth,
    string? ChiefComplaint,
    string? SymptomDuration,
    string? Medications,
    string? Allergies
);

public record BookAppointmentRequest(
    [Required(ErrorMessage = "Patient ID is required")]
    Guid PatientId,

    [Required(ErrorMessage = "Doctor ID is required")]
    Guid DoctorId,

    [Required(ErrorMessage = "Appointment Date is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be YYYY-MM-DD")]
    string Date,

    [Required(ErrorMessage = "TimeSlot is required"), RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "TimeSlot must be HH:mm format")]
    string TimeSlot,

    [Required(ErrorMessage = "Appointment Type is required")]
    string Type);

public record UpdateAppointmentRequest(
    [Required(ErrorMessage = "Status is required")]
    string Status);

public record RescheduleAppointmentRequest(
    [Required(ErrorMessage = "New appointment date is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be YYYY-MM-DD")]
    string NewDate,

    [Required(ErrorMessage = "New time slot is required"), RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "TimeSlot must be HH:mm format")]
    string NewTimeSlot);

public record UpdatePriorityRequest(
    [Required(ErrorMessage = "Priority is required"), RegularExpression("(?i)^(normal|emergency|urgent)$", ErrorMessage = "Priority must be normal, urgent, or emergency")]
    string Priority,

    [StringLength(300, ErrorMessage = "Reason cannot exceed 300 characters")]
    string? Reason = null,

    string? TriageReasonCode = null,

    bool ConfirmTriage = false);

