using System.ComponentModel.DataAnnotations;
using Hospital_CRM.Api.Authorization;
using Hospital_CRM.Api.Extensions;
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
public class ClinicController : ControllerBase
{
    private readonly HospitalCrmDbContext _db;

    public ClinicController(HospitalCrmDbContext db)
    {
        _db = db;
    }

    [HttpPut("profile")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> UpdateProfile([FromBody] ClinicProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name_required" });

        var membership = await SelectedMembershipAsync(admin: true, ct);
        if (membership is null)
            return Forbid();

        var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.Id == membership.ClinicId && c.TenantId == membership.TenantId, ct);
        if (clinic is null)
            return NotFound(new { error = "clinic_not_found" });

        clinic.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Timezone)) clinic.Timezone = request.Timezone.Trim();
        if (!string.IsNullOrWhiteSpace(request.Currency)) clinic.Currency = request.Currency.Trim();
        if (!string.IsNullOrWhiteSpace(request.OrganizationType)) clinic.OrganizationType = request.OrganizationType.Trim();
        if (!string.IsNullOrWhiteSpace(request.LegalName)) clinic.LegalName = request.LegalName.Trim();
        if (!string.IsNullOrWhiteSpace(request.Address)) clinic.Address = request.Address.Trim();
        if (!string.IsNullOrWhiteSpace(request.Phone)) clinic.Phone = request.Phone.Trim();
        if (!string.IsNullOrWhiteSpace(request.Email)) clinic.Email = request.Email.Trim();
        if (!string.IsNullOrWhiteSpace(request.Website)) clinic.Website = request.Website.Trim();
        if (!string.IsNullOrWhiteSpace(request.DateFormat)) clinic.DateFormat = request.DateFormat.Trim();
        if (!string.IsNullOrWhiteSpace(request.TimeFormat)) clinic.TimeFormat = request.TimeFormat.Trim();
        if (!string.IsNullOrWhiteSpace(request.Language)) clinic.Language = request.Language.Trim();
        if (!string.IsNullOrWhiteSpace(request.PrimaryColor)) clinic.PrimaryColor = request.PrimaryColor.Trim();
        if (!string.IsNullOrWhiteSpace(request.SecondaryColor)) clinic.SecondaryColor = request.SecondaryColor.Trim();
        if (!string.IsNullOrWhiteSpace(request.AccentColor)) clinic.AccentColor = request.AccentColor.Trim();
        if (request.DefaultAppointmentDurationMinutes.HasValue && request.DefaultAppointmentDurationMinutes.Value > 0)
            clinic.DefaultAppointmentDurationMinutes = request.DefaultAppointmentDurationMinutes.Value;
        if (request.BufferMinutes.HasValue && request.BufferMinutes.Value >= 0)
            clinic.BufferMinutes = request.BufferMinutes.Value;
        if (request.MinAdvanceBookingHours.HasValue && request.MinAdvanceBookingHours.Value >= 0)
            clinic.MinAdvanceBookingHours = request.MinAdvanceBookingHours.Value;
        if (request.MaxAdvanceBookingDays.HasValue && request.MaxAdvanceBookingDays.Value >= 1)
            clinic.MaxAdvanceBookingDays = request.MaxAdvanceBookingDays.Value;
        if (request.SameDayBookingAllowed.HasValue)
            clinic.SameDayBookingAllowed = request.SameDayBookingAllowed.Value;
        if (request.WalkInsAllowed.HasValue)
            clinic.WalkInsAllowed = request.WalkInsAllowed.Value;
        if (request.OverbookingAllowed.HasValue)
            clinic.OverbookingAllowed = request.OverbookingAllowed.Value;
        if (request.CancellationWindowHours.HasValue && request.CancellationWindowHours.Value >= 0)
            clinic.CancellationWindowHours = request.CancellationWindowHours.Value;
        if (request.ReschedulingAllowed.HasValue)
            clinic.ReschedulingAllowed = request.ReschedulingAllowed.Value;
        if (request.NoShowHandlingEnabled.HasValue)
            clinic.NoShowHandlingEnabled = request.NoShowHandlingEnabled.Value;
        if (request.QueueEnabled.HasValue)
            clinic.QueueEnabled = request.QueueEnabled.Value;
        if (!string.IsNullOrWhiteSpace(request.TokenFormat))
            clinic.TokenFormat = request.TokenFormat.Trim();
        if (request.TokenStartNumber.HasValue && request.TokenStartNumber.Value > 0)
            clinic.TokenStartNumber = request.TokenStartNumber.Value;
        if (!string.IsNullOrWhiteSpace(request.TokenResetFrequency))
            clinic.TokenResetFrequency = request.TokenResetFrequency.Trim();
        if (!string.IsNullOrWhiteSpace(request.InvoicePrefix))
            clinic.InvoicePrefix = request.InvoicePrefix.Trim();
        if (request.DefaultConsultationFee.HasValue && request.DefaultConsultationFee.Value >= 0)
            clinic.DefaultConsultationFee = request.DefaultConsultationFee.Value;
        if (request.DefaultGstRate.HasValue && request.DefaultGstRate.Value >= 0)
            clinic.DefaultGstRate = request.DefaultGstRate.Value;
        if (request.DefaultInvoiceStatus.HasValue)
            clinic.DefaultInvoiceStatus = request.DefaultInvoiceStatus.Value;
        if (request.PharmacyLicenceNumber20 != null)
            clinic.PharmacyLicenceNumber20 = request.PharmacyLicenceNumber20.Trim();
        if (request.PharmacyLicenceNumber21 != null)
            clinic.PharmacyLicenceNumber21 = request.PharmacyLicenceNumber21.Trim();
        if (request.PharmacyGstNumber != null)
            clinic.PharmacyGstNumber = request.PharmacyGstNumber.Trim();
        clinic.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new { clinicId = clinic.Id, name = clinic.Name, timezone = clinic.Timezone, currency = clinic.Currency });
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "not_authenticated" });

        var membership = await SelectedMembershipAsync(admin: false, ct);
        if (membership is null)
            return Forbid();

        var clinic = await _db.Clinics
            .Include(c => c.WorkingHours)
            .Include(c => c.Holidays)
            .Include(c => c.SpecialHours)
            .FirstOrDefaultAsync(c => c.Id == membership.ClinicId && c.TenantId == membership.TenantId, ct);

        if (clinic is null)
            return NotFound(new { error = "clinic_not_found" });

        var workingHours = clinic.WorkingHours ?? new List<ClinicHours>();
        var holidays = clinic.Holidays ?? new List<ClinicHoliday>();
        var specialHours = clinic.SpecialHours ?? new List<ClinicSpecialHour>();

        // Entitlements come from the Tenant row (the module gate reads the same value).
        var activatedModules = ModuleRegistry.Parse(
            await _db.Tenants.AsNoTracking()
                .Where(t => t.Id == clinic.TenantId)
                .Select(t => t.ActivatedModules)
                .FirstOrDefaultAsync(ct));

        return Ok(new
        {
            clinicId = clinic.Id,
            activatedModules,
            name = clinic.Name,
            organizationType = clinic.OrganizationType,
            legalName = clinic.LegalName,
            address = clinic.Address,
            phone = clinic.Phone,
            email = clinic.Email,
            website = clinic.Website,
            timezone = clinic.Timezone,
            currency = clinic.Currency,
            dateFormat = clinic.DateFormat,
            timeFormat = clinic.TimeFormat,
            language = clinic.Language,
            logoUrl = clinic.LogoUrl,
            darkLogoUrl = clinic.DarkLogoUrl,
            lightLogoUrl = clinic.LightLogoUrl,
            faviconUrl = clinic.FaviconUrl,
            primaryColor = clinic.PrimaryColor,
            secondaryColor = clinic.SecondaryColor,
            accentColor = clinic.AccentColor,
            defaultAppointmentDurationMinutes = clinic.DefaultAppointmentDurationMinutes,
            bufferMinutes = clinic.BufferMinutes,
            minAdvanceBookingHours = clinic.MinAdvanceBookingHours,
            maxAdvanceBookingDays = clinic.MaxAdvanceBookingDays,
            sameDayBookingAllowed = clinic.SameDayBookingAllowed,
            walkInsAllowed = clinic.WalkInsAllowed,
            overbookingAllowed = clinic.OverbookingAllowed,
            cancellationWindowHours = clinic.CancellationWindowHours,
            reschedulingAllowed = clinic.ReschedulingAllowed,
            noShowHandlingEnabled = clinic.NoShowHandlingEnabled,
            queueEnabled = clinic.QueueEnabled,
            tokenFormat = clinic.TokenFormat,
            tokenStartNumber = clinic.TokenStartNumber,
            tokenResetFrequency = clinic.TokenResetFrequency,
            invoicePrefix = clinic.InvoicePrefix,
            defaultConsultationFee = clinic.DefaultConsultationFee,
            defaultGstRate = clinic.DefaultGstRate,
            defaultInvoiceStatus = clinic.DefaultInvoiceStatus,
            pharmacyLicenceNumber20 = clinic.PharmacyLicenceNumber20,
            pharmacyLicenceNumber21 = clinic.PharmacyLicenceNumber21,
            pharmacyGstNumber = clinic.PharmacyGstNumber,
            workingHours = workingHours
                .OrderBy(h => h.DayOfWeek)
                .ThenBy(h => h.ShiftIndex)
                .Select(h => new
                {
                    day = ((DayOfWeek)h.DayOfWeek).ToString().ToLower(),
                    shiftIndex = h.ShiftIndex,
                    open = h.OpenTime,
                    close = h.CloseTime
                }),
            holidays = holidays
                .OrderBy(h => h.StartDate)
                .Select(h => new
                {
                    id = h.Id,
                    name = h.Name,
                    startDate = h.StartDate,
                    endDate = h.EndDate,
                    recurringAnnually = h.RecurringAnnually,
                    internalNote = h.InternalNote
                }),
            specialHours = specialHours
                .OrderBy(s => s.Date)
                .Select(s => new
                {
                    id = s.Id,
                    date = s.Date,
                    open = s.OpenTime,
                    close = s.CloseTime,
                    reason = s.Reason
                })
        });
    }

    [HttpPut("hours")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> UpdateHours([FromBody] UpdateHoursRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedMembershipAsync(admin: true, ct);
        if (membership is null)
            return Forbid();

        var clinicId = membership.ClinicId!.Value;
        var executionStrategy = _db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            var existing = await _db.ClinicHours
                .Where(h => h.ClinicId == clinicId)
                .ToListAsync(ct);
            _db.ClinicHours.RemoveRange(existing);

            if (request.WorkingHours is not null)
            {
                foreach (var wh in request.WorkingHours)
                {
                    var dayNum = ParseDayOfWeek(wh.Day);
                    if (dayNum is null)
                        return BadRequest(new { error = $"invalid_day: {wh.Day}" });

                    _db.ClinicHours.Add(new ClinicHours
                    {
                        Id = Guid.NewGuid(),
                        ClinicId = clinicId,
                        DayOfWeek = dayNum.Value,
                        ShiftIndex = wh.ShiftIndex,
                        OpenTime = wh.Open,
                        CloseTime = wh.Close,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Ok(new { message = "working_hours_updated" });
        });
    }

    [HttpPost("holidays")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> AddHoliday([FromBody] HolidayRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedMembershipAsync(admin: true, ct);
        if (membership is null)
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.StartDate) || string.IsNullOrWhiteSpace(request.EndDate))
            return BadRequest(new { error = "start_date_and_end_date_required" });

        if (string.Compare(request.StartDate, request.EndDate, StringComparison.Ordinal) > 0)
            return BadRequest(new { error = "end_date_must_be_on_or_after_start_date" });

        var holiday = new ClinicHoliday
        {
            Id = Guid.NewGuid(),
            ClinicId = membership.ClinicId!.Value,
            Name = request.Name ?? "Holiday",
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            RecurringAnnually = request.RecurringAnnually,
            InternalNote = request.InternalNote,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.ClinicHolidays.Add(holiday);
        await _db.SaveChangesAsync(ct);

        return Ok(new { holidayId = holiday.Id, name = holiday.Name, startDate = holiday.StartDate, endDate = holiday.EndDate });
    }

    [HttpDelete("holidays/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> DeleteHoliday(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedMembershipAsync(admin: true, ct);
        if (membership is null)
            return Forbid();

        var holiday = await _db.ClinicHolidays
            .FirstOrDefaultAsync(h => h.Id == id && h.ClinicId == membership.ClinicId, ct);
        if (holiday is null)
            return NotFound(new { error = "holiday_not_found" });

        _db.ClinicHolidays.Remove(holiday);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "holiday_deleted" });
    }

    [HttpPost("special-hours")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> AddSpecialHour([FromBody] SpecialHourRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedMembershipAsync(admin: true, ct);
        if (membership is null)
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.Date))
            return BadRequest(new { error = "date_required" });

        var special = new ClinicSpecialHour
        {
            Id = Guid.NewGuid(),
            ClinicId = membership.ClinicId!.Value,
            Date = request.Date,
            OpenTime = request.Open,
            CloseTime = request.Close,
            Reason = request.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.ClinicSpecialHours.Add(special);
        await _db.SaveChangesAsync(ct);

        return Ok(new { specialId = special.Id, date = special.Date, open = special.OpenTime, close = special.CloseTime });
    }

    [HttpDelete("special-hours/{id:guid}")]
    [AuthorizeRoles("ClinicAdmin")]
    public async Task<IActionResult> DeleteSpecialHour(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized(new { error = "invalid_token" });

        var membership = await SelectedMembershipAsync(admin: true, ct);
        if (membership is null)
            return Forbid();

        var special = await _db.ClinicSpecialHours
            .FirstOrDefaultAsync(s => s.Id == id && s.ClinicId == membership.ClinicId, ct);
        if (special is null)
            return NotFound(new { error = "special_hour_not_found" });

        _db.ClinicSpecialHours.Remove(special);
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "special_hour_deleted" });
    }

    private Task<TenantMembership?> SelectedMembershipAsync(bool admin, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var tenantId = User.GetTenantId();
        var clinicId = User.GetClinicId();
        if (userId is null || tenantId is null || tenantId == Guid.Empty || clinicId is null || clinicId == Guid.Empty)
            return Task.FromResult<TenantMembership?>(null);
        return _db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(m =>
            m.UserId == userId && m.TenantId == tenantId && m.ClinicId == clinicId && m.Status == "active"
            && (!admin || m.Role == UserRole.ClinicAdmin) && m.Role != UserRole.PlatformAdmin
            && m.Clinic != null && m.Clinic.TenantId == tenantId, ct);
    }

    private static int? ParseDayOfWeek(string day)
    {
        return day.ToLower() switch
        {
            "sun" or "sunday" => 0,
            "mon" or "monday" => 1,
            "tue" or "tuesday" => 2,
            "wed" or "wednesday" => 3,
            "thu" or "thursday" => 4,
            "fri" or "friday" => 5,
            "sat" or "saturday" => 6,
            _ => null
        };
    }
}

public record ClinicProfileRequest(
    [Required(ErrorMessage = "Clinic Name is required"), StringLength(150, MinimumLength = 2, ErrorMessage = "Clinic Name must be between 2 and 150 characters")]
    string Name,
    string? Timezone = null,
    string? Currency = null,
    string? OrganizationType = null,
    string? LegalName = null,
    string? Address = null,
    string? Phone = null,
    string? Email = null,
    string? Website = null,
    string? DateFormat = null,
    string? TimeFormat = null,
    string? Language = null,
    string? PrimaryColor = null,
    string? SecondaryColor = null,
    string? AccentColor = null,
    int? DefaultAppointmentDurationMinutes = null,
    int? BufferMinutes = null,
    int? MinAdvanceBookingHours = null,
    int? MaxAdvanceBookingDays = null,
    bool? SameDayBookingAllowed = null,
    bool? WalkInsAllowed = null,
    bool? OverbookingAllowed = null,
    int? CancellationWindowHours = null,
    bool? ReschedulingAllowed = null,
    bool? NoShowHandlingEnabled = null,
    bool? QueueEnabled = null,
    string? TokenFormat = null,
    int? TokenStartNumber = null,
    string? TokenResetFrequency = null,
    string? InvoicePrefix = null,
    decimal? DefaultConsultationFee = null,
    decimal? DefaultGstRate = null,
    bool? DefaultInvoiceStatus = null,
    // State drug-control formats differ (e.g. "KA/B-123/2020-21", "DL-20-2026-KA-8891"), so reject only
    // characters a licence never contains; GSTIN has one national format.
    [StringLength(100, ErrorMessage = "Form 20 licence number cannot exceed 100 characters")]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9/\- ]{3,99}$", ErrorMessage = "Form 20 licence number may only contain letters, digits, spaces, '-' and '/'")]
    string? PharmacyLicenceNumber20 = null,
    [StringLength(100, ErrorMessage = "Form 21 licence number cannot exceed 100 characters")]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9/\- ]{3,99}$", ErrorMessage = "Form 21 licence number may only contain letters, digits, spaces, '-' and '/'")]
    string? PharmacyLicenceNumber21 = null,
    [StringLength(50, ErrorMessage = "Pharmacy GST number cannot exceed 50 characters")]
    [RegularExpression(@"(?i)^\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", ErrorMessage = "Pharmacy GST number must be a valid 15-character GSTIN")]
    string? PharmacyGstNumber = null);

public record WorkingHoursRequest(
    [Required(ErrorMessage = "Day is required")]
    string Day,

    [Range(0, 10)]
    int ShiftIndex,

    [Required(ErrorMessage = "Open time is required"), RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Open time must be in HH:mm format")]
    string Open,

    [Required(ErrorMessage = "Close time is required"), RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Close time must be in HH:mm format")]
    string Close);

public record UpdateHoursRequest(
    List<WorkingHoursRequest>? WorkingHours);

public record HolidayRequest(
    [StringLength(100, ErrorMessage = "Holiday name cannot exceed 100 characters")]
    string? Name,

    [Required(ErrorMessage = "StartDate is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "StartDate must be YYYY-MM-DD")]
    string StartDate,

    [Required(ErrorMessage = "EndDate is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "EndDate must be YYYY-MM-DD")]
    string EndDate,

    bool RecurringAnnually,

    [StringLength(300, ErrorMessage = "Internal note cannot exceed 300 characters")]
    string? InternalNote);

public record SpecialHourRequest(
    [Required(ErrorMessage = "Date is required"), RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be YYYY-MM-DD")]
    string Date,

    [Required(ErrorMessage = "Open time is required"), RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Open time must be in HH:mm format")]
    string Open,

    [Required(ErrorMessage = "Close time is required"), RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Close time must be in HH:mm format")]
    string Close,

    [StringLength(200, ErrorMessage = "Reason cannot exceed 200 characters")]
    string? Reason);
