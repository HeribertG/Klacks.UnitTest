using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PlanningDeadlineCalculatorTests
{
    private static readonly DateOnly PeriodStart = new(2026, 2, 1);
    private static readonly DateOnly Today = new(2026, 1, 10);

    [Test]
    public void Compute_EmailWithFourteenDayAnnouncement_SubtractsAnnouncementAndReview()
    {
        var deadline = PlanningDeadlineCalculator.Compute(PeriodStart, Today, announcementDays: 14, transitDays: 0, reviewDays: 2);

        Assert.That(deadline.SendByDate, Is.EqualTo(new DateOnly(2026, 1, 18)));
        Assert.That(deadline.PlanningDoneBy, Is.EqualTo(new DateOnly(2026, 1, 16)));
        Assert.That(deadline.DaysRemaining, Is.EqualTo(6));
    }

    [Test]
    public void Compute_Post_AddsTransitBeforeSendDate()
    {
        var deadline = PlanningDeadlineCalculator.Compute(PeriodStart, Today, announcementDays: 14, transitDays: 3, reviewDays: 2);

        Assert.That(deadline.SendByDate, Is.EqualTo(new DateOnly(2026, 1, 15)));
        Assert.That(deadline.PlanningDoneBy, Is.EqualTo(new DateOnly(2026, 1, 13)));
    }

    [Test]
    public void Compute_DeadlineInThePast_YieldsNegativeRemainingDays()
    {
        var deadline = PlanningDeadlineCalculator.Compute(PeriodStart, new DateOnly(2026, 1, 25), 14, 3, 2);

        Assert.That(deadline.DaysRemaining, Is.EqualTo(-12));
    }

    [Test]
    public void Compute_ZeroInputs_DeadlineIsThePeriodStart()
    {
        var deadline = PlanningDeadlineCalculator.Compute(PeriodStart, Today, 0, 0, 0);

        Assert.That(deadline.PlanningDoneBy, Is.EqualTo(PeriodStart));
    }

    [Test]
    public void LeadDays_IsTheSumOfAllThreeInputs()
    {
        Assert.That(PlanningDeadlineCalculator.LeadDays(14, 3, 2), Is.EqualTo(19));
    }

    [TestCase(0, true)]
    [TestCase(365, true)]
    [TestCase(-1, false)]
    [TestCase(366, false)]
    public void IsValidDays_AcceptsZeroToOneYear(int days, bool expected)
    {
        Assert.That(PlanningDeadlineCalculator.IsValidDays(days), Is.EqualTo(expected));
    }

    [Test]
    public void EffectiveAnnouncement_IsNeverBelowTheComplianceMinimum()
    {
        Assert.That(PlanningDeadlineCalculator.EffectiveAnnouncement(7, 14), Is.EqualTo(14));
        Assert.That(PlanningDeadlineCalculator.EffectiveAnnouncement(21, 14), Is.EqualTo(21));
    }

    [Test]
    public void FirstReachable_WeeklyPeriodsWithLongLead_SkipsToThePeriodThatCanStillBeMet()
    {
        var firstStart = new DateOnly(2026, 2, 2);

        var deadline = PlanningDeadlineCalculator.FirstReachable(
            end => end.AddDays(1), firstStart, new DateOnly(2026, 1, 28),
            announcementDays: 14, transitDays: 0, reviewDays: 2,
            periodEnd: start => start.AddDays(6));

        Assert.That(deadline.PeriodStart, Is.EqualTo(new DateOnly(2026, 2, 16)));
        Assert.That(deadline.DaysRemaining, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void FirstReachable_NextPeriodStillReachable_ReturnsItUnchanged()
    {
        var deadline = PlanningDeadlineCalculator.FirstReachable(
            end => end.AddDays(1), new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 10),
            14, 0, 2, start => start.AddMonths(1).AddDays(-1));

        Assert.That(deadline.PeriodStart, Is.EqualTo(new DateOnly(2026, 2, 1)));
    }
}
