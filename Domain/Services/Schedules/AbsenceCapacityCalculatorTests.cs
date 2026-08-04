// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class AbsenceCapacityCalculatorTests
{
    private const double MaxUtilization = 0.8;

    private static readonly DateOnly Monday = new(2026, 8, 3);

    private static List<CapacityDay> Week(
        double desiredReadiness,
        double demand,
        double existingAbsence = 0,
        double weekendReadiness = 0,
        double weekendDemand = 0)
    {
        var days = new List<CapacityDay>();
        for (var i = 0; i < 21; i++)
        {
            var date = Monday.AddDays(i - 7);
            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            days.Add(new CapacityDay(
                date,
                isWeekend ? weekendReadiness : desiredReadiness,
                isWeekend ? weekendDemand : demand,
                existingAbsence));
        }

        return days;
    }

    [Test]
    public void QuietWeekendWithoutReadiness_ProducesNoFinding()
    {
        var saturday = Monday.AddDays(5);
        var days = Week(desiredReadiness: 10, demand: 5);

        var findings = AbsenceCapacityCalculator.Evaluate(days, saturday, saturday, requestedDailyValue: 1);

        findings.Any(f => f.Kind == CapacityWindowKind.Day).ShouldBeFalse(
            "a day without readiness and without shifts is not a gap, it is simply not a working day");
    }

    [Test]
    public void NoCapacityLeftButShiftsRemain_IsReportedWithoutRatio()
    {
        var days = Week(desiredReadiness: 2, demand: 4);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday, requestedDailyValue: 2);

        var day = findings.Single(f => f.Kind == CapacityWindowKind.Day && f.From == Monday);
        day.NoCapacityLeft.ShouldBeTrue();
        day.Utilization.ShouldBeNull();
        day.Demand.ShouldBe(4);
    }

    [Test]
    public void UtilizationBelowCeiling_IsNotCritical()
    {
        var days = Week(desiredReadiness: 10, demand: 5);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday, requestedDailyValue: 1);
        var critical = AbsenceCapacityCalculator.CriticalOnly(findings, MaxUtilization);

        var day = findings.Single(f => f.Kind == CapacityWindowKind.Day && f.From == Monday);
        day.Available.ShouldBe(9);
        day.Utilization!.Value.ShouldBe(5.0 / 9.0, 0.0001);
        critical.ShouldBeEmpty();
    }

    [Test]
    public void UtilizationAboveCeiling_IsCritical()
    {
        var days = Week(desiredReadiness: 10, demand: 8);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday, requestedDailyValue: 1);
        var critical = AbsenceCapacityCalculator.CriticalOnly(findings, MaxUtilization);

        var day = findings.Single(f => f.Kind == CapacityWindowKind.Day && f.From == Monday);
        day.Utilization!.Value.ShouldBe(8.0 / 9.0, 0.0001);
        critical.ShouldContain(f => f.Kind == CapacityWindowKind.Day && f.From == Monday);
    }

    [Test]
    public void ExistingAbsencesReduceAvailableCapacity()
    {
        var days = Week(desiredReadiness: 10, demand: 5, existingAbsence: 3);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday, requestedDailyValue: 1);

        var day = findings.Single(f => f.Kind == CapacityWindowKind.Day && f.From == Monday);
        day.Available.ShouldBe(6);
    }

    [Test]
    public void RequestedValueOnlyReducesDaysInsideTheRequestedPeriod()
    {
        var tuesday = Monday.AddDays(1);
        var days = Week(desiredReadiness: 10, demand: 5);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday, requestedDailyValue: 4);

        findings.Single(f => f.Kind == CapacityWindowKind.Day && f.From == Monday).Available.ShouldBe(6);
        findings.Any(f => f.Kind == CapacityWindowKind.Day && f.From == tuesday).ShouldBeFalse(
            "only days inside the requested period are evaluated as single days");
    }

    [Test]
    public void WindowRatioUsesSums_NotTheAverageOfDailyRatios()
    {
        var days = new List<CapacityDay>();
        for (var i = 0; i < 21; i++)
        {
            var date = Monday.AddDays(i - 7);
            var demand = date == Monday ? 20 : 1;
            days.Add(new CapacityDay(date, 10, demand, 0));
        }

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday.AddDays(2), requestedDailyValue: 0);

        var threeDay = findings.First(f => f.Kind == CapacityWindowKind.ThreeDay && f.From == Monday);
        threeDay.Demand.ShouldBe(22);
        threeDay.Available.ShouldBe(30);
        threeDay.Utilization!.Value.ShouldBe(22.0 / 30.0, 0.0001);
    }

    [Test]
    public void WorkWeekExcludesDaysWithoutReadiness()
    {
        var days = Week(desiredReadiness: 10, demand: 5, weekendReadiness: 0, weekendDemand: 0);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday.AddDays(4), requestedDailyValue: 0);

        var workWeek = findings.Single(f => f.Kind == CapacityWindowKind.WorkWeek && f.From == Monday);
        workWeek.Until.ShouldBe(Monday.AddDays(4));
        workWeek.Available.ShouldBe(50);
        workWeek.Demand.ShouldBe(25);
    }

    [Test]
    public void CalendarWeekCoversAllSevenDays()
    {
        var days = Week(desiredReadiness: 10, demand: 5, weekendReadiness: 0, weekendDemand: 0);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday, Monday.AddDays(4), requestedDailyValue: 0);

        var calendarWeek = findings.Single(f => f.Kind == CapacityWindowKind.CalendarWeek && f.From == Monday);
        calendarWeek.Until.ShouldBe(Monday.AddDays(6));
        calendarWeek.Available.ShouldBe(50);
    }

    [Test]
    public void ReversedDates_AreNormalized()
    {
        var days = Week(desiredReadiness: 10, demand: 8);

        var findings = AbsenceCapacityCalculator.Evaluate(days, Monday.AddDays(2), Monday, requestedDailyValue: 1);

        findings.Count(f => f.Kind == CapacityWindowKind.Day).ShouldBe(3);
    }

    [Test]
    public void EmptyInput_ReturnsNoFindings()
    {
        var findings = AbsenceCapacityCalculator.Evaluate([], Monday, Monday, requestedDailyValue: 1);

        findings.ShouldBeEmpty();
    }

    private static List<CapacityDay> Year(double desiredReadiness, Func<DateOnly, double> demand)
    {
        var days = new List<CapacityDay>();
        for (var i = -14; i < 120; i++)
        {
            var date = Monday.AddDays(i);
            days.Add(new CapacityDay(date, desiredReadiness, demand(date), 0));
        }

        return days;
    }

    [Test]
    public void EveryPeriodReportedAsFitting_IsAlsoAcceptedByADirectCheck()
    {
        var busyFrom = Monday.AddDays(10);
        var busyUntil = Monday.AddDays(24);
        var days = Year(10, date => date >= busyFrom && date <= busyUntil ? 9 : 4);

        var candidates = AbsenceCapacityCalculator.FindFittingPeriods(
            days, Monday, Monday.AddDays(60), durationDays: 7, requestedDailyValue: 1, MaxUtilization);

        candidates.Where(c => c.Fits).ShouldNotBeEmpty();

        foreach (var candidate in candidates.Where(c => c.Fits))
        {
            var direct = AbsenceCapacityCalculator.Evaluate(days, candidate.From, candidate.Until, 1);
            AbsenceCapacityCalculator.CriticalOnly(direct, MaxUtilization).ShouldBeEmpty(
                $"period {candidate.From}..{candidate.Until} was suggested as fitting but a direct check rejects it");
        }
    }

    [Test]
    public void PeriodsOverlappingHighDemand_DoNotFit()
    {
        var busyFrom = Monday.AddDays(10);
        var busyUntil = Monday.AddDays(24);
        var days = Year(10, date => date >= busyFrom && date <= busyUntil ? 9 : 4);

        var candidates = AbsenceCapacityCalculator.FindFittingPeriods(
            days, Monday, Monday.AddDays(60), durationDays: 7, requestedDailyValue: 1, MaxUtilization);

        var overlapping = candidates.Where(c => c.From <= busyUntil && c.Until >= busyFrom).ToList();
        overlapping.ShouldNotBeEmpty();
        overlapping.ShouldAllBe(c => !c.Fits);
    }

    [Test]
    public void NoPeriodFits_WhenDemandIsHighEverywhere()
    {
        var days = Year(10, _ => 9);

        var candidates = AbsenceCapacityCalculator.FindFittingPeriods(
            days, Monday, Monday.AddDays(30), durationDays: 5, requestedDailyValue: 1, MaxUtilization);

        candidates.ShouldNotBeEmpty();
        candidates.ShouldAllBe(c => !c.Fits);
        candidates.ShouldAllBe(c => c.BlockingWindowCount > 0);
    }

    [Test]
    public void CandidatesCoverTheWholeSearchRange_AndRespectTheDuration()
    {
        var days = Year(10, _ => 4);

        var candidates = AbsenceCapacityCalculator.FindFittingPeriods(
            days, Monday, Monday.AddDays(9), durationDays: 3, requestedDailyValue: 1, MaxUtilization);

        candidates.Count.ShouldBe(8);
        candidates[0].From.ShouldBe(Monday);
        candidates[^1].Until.ShouldBe(Monday.AddDays(9));
        candidates.ShouldAllBe(c => c.Until.DayNumber - c.From.DayNumber + 1 == 3);
    }

    [Test]
    public void DurationBelowOne_IsRejected()
    {
        var days = Year(10, _ => 4);

        Should.Throw<ArgumentOutOfRangeException>(() => AbsenceCapacityCalculator.FindFittingPeriods(
            days, Monday, Monday.AddDays(9), durationDays: 0, requestedDailyValue: 1, MaxUtilization));
    }
}
