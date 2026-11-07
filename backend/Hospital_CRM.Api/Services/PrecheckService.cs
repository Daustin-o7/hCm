using System.Security.Cryptography;
using Hospital_CRM.Api.Extensions;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Hospital_CRM.Api.Services;

public class PrecheckService
{
    private readonly HospitalCrmDbContext _db;
    private readonly IConfiguration _config;

    public PrecheckService(HospitalCrmDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>
    /// Returns (plaintextToken, submission) for the caller to add to the DbContext
    /// and SaveChanges in the same transaction. The caller owns the commit.
    /// </summary>
    public async Task<(string? plaintextToken, PrecheckSubmission? submission)> GenerateForAppointmentAsync(
        Guid appointmentId, Guid tenantId, CancellationToken ct = default)
    {
        var appointment = await _db.Appointments
            .Include(a => a.Clinic)
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.TenantId == tenantId && tenantId != Guid.Empty &&
                a.Patient.TenantId == tenantId && a.Clinic.TenantId == tenantId, ct);
        if (appointment is null) throw new InvalidOperationException("appointment_not_found");

        if (appointment.Type == AppointmentType.WalkIn)
            return (null, null);

        var slotLocal = appointment.Date.ToDateTime(TimeOnly.Parse(appointment.TimeSlot));
        var timezone = new TimezoneService().ResolveTimeZone(null, appointment.Clinic.Timezone);
        var slotDateTime = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(slotLocal, timezone));
        if (slotDateTime - DateTimeOffset.UtcNow < TimeSpan.FromHours(2))
            return (null, null);

        var (plaintext, hash) = GenerateToken();

        var submission = new PrecheckSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            TokenHash = hash,
            ExpiresAt = slotDateTime,
            CreatedAt = DateTimeOffset.UtcNow
        };
        // Caller adds to DbContext and calls SaveChanges — no SaveChanges here.
        return (plaintext, submission);
    }

    // The patient-facing page is the SPA route /intake/:token (see frontend/src/App.tsx).
    public string BuildLink(string token) =>
        $"{_config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5173"}/intake/{token}";

    private static (string plaintext, string hash) GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = plaintext.HashToken();
        return (plaintext, hash);
    }
}
