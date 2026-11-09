using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;

namespace Hospital_CRM.Api.Services;

public record PatientVisitRecord(DateOnly VisitDate, string Status, bool HasConsultation);

/// <summary>
/// G07 No-Visit Recall Evaluation Engine.
/// Formal clinical and administrative policy rules:
/// 1. Qualifying visits: only Completed appointments or appointments with finalized consultations count.
/// 2. Never-visited handling: Patients with zero completed/finalized visits are excluded from no-visit recall.
/// 3. Calendar cutoffs: Calculated strictly using calendar month boundaries (referenceDate.AddMonths(-nMonths)).
/// 
/// NOTE: Production dispatch in NotificationRulesWorker remains fail-closed (no_visit_policy_pending)
/// until clinical director signs off on the N=1/3/6 golden fixtures and calendar cutoff policy.
/// </summary>
public static class NoVisitPolicyEngine
{
    public static bool QualifiesForNoVisitRecall(
        DateOnly referenceToday,
        int nMonths,
        IEnumerable<PatientVisitRecord> patientVisits,
        out DateOnly? lastQualifyingVisitDate)
    {
        lastQualifyingVisitDate = null;

        // 1. Qualifying visits: only Completed appointments or those with finalized consultations
        var validVisits = patientVisits
            .Where(v => v.Status == "Completed" || v.HasConsultation)
            .OrderByDescending(v => v.VisitDate)
            .ToList();

        // 2. Never-visited handling: Patients with zero completed visits are excluded
        if (validVisits.Count == 0)
        {
            return false;
        }

        var lastVisit = validVisits.First();
        lastQualifyingVisitDate = lastVisit.VisitDate;

        // 3. Calendar cutoff: Exactly N calendar months prior to reference date
        var cutoffDate = referenceToday.AddMonths(-nMonths);

        // Qualifies if last qualifying visit was on or before the N-month cutoff
        return lastVisit.VisitDate <= cutoffDate;
    }

    /// <summary>
    /// Overload operating directly on domain Appointment entities.
    /// Evaluates Completed appointments or appointments with Finalized/Amended consultations.
    /// </summary>
    public static bool QualifiesForNoVisitRecall(
        DateOnly referenceToday,
        int nMonths,
        IEnumerable<Appointment> appointments,
        out DateOnly? lastQualifyingVisitDate)
    {
        var records = appointments.Select(a => new PatientVisitRecord(
            a.Date,
            a.Status.ToString(),
            a.Status == AppointmentStatus.Completed ||
            (a.Consultations != null && a.Consultations.Any(c => c.Status is ConsultationStatus.Finalized or ConsultationStatus.Amended))
        ));
        return QualifiesForNoVisitRecall(referenceToday, nMonths, records, out lastQualifyingVisitDate);
    }
}

