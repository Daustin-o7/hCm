using Hospital_CRM.Api.Services;
using Xunit;

namespace Hospital_CRM.Tests;

/// <summary>
/// G07 No-Visit Policy Validation:
/// Validates qualifying visit states, timestamps, never-visited handling,
/// and calendar cutoffs for N=1, 3, 6 months against the production NoVisitPolicyEngine.
/// 
/// NOTE: The background worker dispatch (NotificationRulesWorker) remains disabled
/// with warning 'no_visit_policy_pending' pending clinical director sign-off.
/// </summary>
public class NoVisitPolicyValidationTests
{
    [Theory]
    [InlineData(1, "2026-10-09", true)]   // Exactly 1 month ago (qualifies for N=1)
    [InlineData(1, "2026-10-15", false)]  // 25 days ago (does not qualify yet)
    [InlineData(3, "2026-08-09", true)]   // Exactly 3 months ago (qualifies for N=3)
    [InlineData(3, "2026-09-01", false)]  // 2 months ago (does not qualify for N=3)
    [InlineData(6, "2026-05-09", true)]   // Exactly 6 months ago (qualifies for N=6)
    [InlineData(6, "2026-06-01", false)]  // 5 months ago (does not qualify for N=6)
    public void NoVisit_EvaluatesAccurateCalendarCutoffs_ForNMonths(int nMonths, string visitDateStr, bool expectedQualifies)
    {
        var today = new DateOnly(2026, 11, 9);
        var visitDate = DateOnly.Parse(visitDateStr);
        var visits = new[] { new PatientVisitRecord(visitDate, "Completed", true) };

        var qualifies = NoVisitPolicyEngine.QualifiesForNoVisitRecall(today, nMonths, visits, out var lastDate);

        Assert.Equal(expectedQualifies, qualifies);
        Assert.Equal(visitDate, lastDate);
    }

    [Fact]
    public void NoVisit_IgnoresCancelledAndNoShowAppointments()
    {
        var today = new DateOnly(2026, 11, 9);
        var visits = new[]
        {
            new PatientVisitRecord(new DateOnly(2026, 11, 1), "NoShow", false),       // 8 days ago (NoShow)
            new PatientVisitRecord(new DateOnly(2026, 10, 15), "Cancelled", false),    // 25 days ago (Cancelled)
            new PatientVisitRecord(new DateOnly(2026, 05, 01), "Completed", true)      // 6 months ago (Completed)
        };

        // For N=3 months, patient qualifies because last Completed visit was May 1, 2026
        var qualifiesN3 = NoVisitPolicyEngine.QualifiesForNoVisitRecall(today, 3, visits, out var lastDate);
        Assert.True(qualifiesN3);
        Assert.Equal(new DateOnly(2026, 5, 1), lastDate);

        // For N=6 months, patient also qualifies (May 1 <= May 9)
        var qualifiesN6 = NoVisitPolicyEngine.QualifiesForNoVisitRecall(today, 6, visits, out _);
        Assert.True(qualifiesN6);
    }

    [Fact]
    public void NoVisit_ExcludesNeverVisitedPatients()
    {
        var today = new DateOnly(2026, 11, 9);
        // Patient registered and has only Cancelled appointments
        var visits = new[]
        {
            new PatientVisitRecord(new DateOnly(2026, 01, 15), "Cancelled", false),
            new PatientVisitRecord(new DateOnly(2026, 02, 10), "NoShow", false)
        };

        var qualifies = NoVisitPolicyEngine.QualifiesForNoVisitRecall(today, 1, visits, out var lastDate);
        Assert.False(qualifies);
        Assert.Null(lastDate);
    }

    [Fact]
    public void NoVisit_HandlesMonthEndAndLeapYearBoundaries()
    {
        // Leap year: 2028-02-29
        var today = new DateOnly(2028, 3, 31);
        var leapVisit = new PatientVisitRecord(new DateOnly(2028, 2, 29), "Completed", true);

        // N=1 from March 31 evaluates to Feb 29 in leap year 2028
        var qualifies = NoVisitPolicyEngine.QualifiesForNoVisitRecall(today, 1, new[] { leapVisit }, out var lastDate);
        Assert.True(qualifies);
        Assert.Equal(new DateOnly(2028, 2, 29), lastDate);
    }
}
