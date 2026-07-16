// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WarmStartWeekdayMapperTests
{
    private static readonly Guid ShiftRef = Guid.NewGuid();

    private static WarmStartSourceWork Work(
        Guid agent,
        DateOnly date,
        int startHour = 8,
        int endHour = 16,
        decimal hours = 8m)
    {
        return new WarmStartSourceWork(
            AgentId: agent,
            Date: date,
            StartTime: new TimeOnly(startHour, 0),
            EndTime: new TimeOnly(endHour, 0),
            WorkTime: hours,
            ShiftId: ShiftRef);
    }

    [Test]
    public void Map_EqualLengthAlignedPeriods_MapsWeekdayTrueOneToOne()
    {
        var agent = Guid.NewGuid();
        var prevFrom = new DateOnly(2026, 3, 2);   // Monday
        var prevUntil = new DateOnly(2026, 3, 29);  // Sunday, 4 full weeks
        var targetFrom = new DateOnly(2026, 6, 1);  // Monday
        var targetUntil = new DateOnly(2026, 6, 28); // Sunday, 4 full weeks

        // One work in each of weeks 0, 2 and 3 -> lastFullWeekIndex = 3 covers every target week,
        // so there is no overhang and the mapping is strictly 1:1 weekday-true.
        var source = new[]
        {
            Work(agent, new DateOnly(2026, 3, 4)),  // Wednesday, week 0
            Work(agent, new DateOnly(2026, 3, 16)), // Monday, week 2
            Work(agent, new DateOnly(2026, 3, 27)), // Friday, week 3
        };

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        result.Count.ShouldBe(3);
        var wednesday = result.Single(a => a.Date.DayOfWeek == DayOfWeek.Wednesday);
        wednesday.Date.ShouldBe(new DateOnly(2026, 6, 3));
        var monday = result.Single(a => a.Date.DayOfWeek == DayOfWeek.Monday);
        monday.Date.ShouldBe(new DateOnly(2026, 6, 15));
        var friday = result.Single(a => a.Date.DayOfWeek == DayOfWeek.Friday);
        friday.Date.ShouldBe(new DateOnly(2026, 6, 26));
        result.ShouldAllBe(a => a.AgentId == agent.ToString());
        result.ShouldAllBe(a => a.ShiftRefId == ShiftRef);
        result.ShouldAllBe(a => a.TotalHours == 8m);
    }

    [Test]
    public void Map_TargetLongerThanSource_OverhangRepeatsLastFullSourceWeek()
    {
        var agent = Guid.NewGuid();
        var prevFrom = new DateOnly(2026, 3, 2);   // Monday
        var prevUntil = new DateOnly(2026, 3, 15);  // Sunday, 2 full weeks (0,1)
        var targetFrom = new DateOnly(2026, 6, 1);  // Monday
        var targetUntil = new DateOnly(2026, 6, 28); // Sunday, 4 full weeks (0..3)

        var source = new[]
        {
            Work(agent, new DateOnly(2026, 3, 3)),  // Tuesday, week 0
            Work(agent, new DateOnly(2026, 3, 10)), // Tuesday, week 1 (last full week)
        };

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        var dates = result.Select(a => a.Date).OrderBy(d => d).ToList();
        dates.ShouldBe(new[]
        {
            new DateOnly(2026, 6, 2),  // week 0 -> source week 0
            new DateOnly(2026, 6, 9),  // week 1 -> source week 1
            new DateOnly(2026, 6, 16), // week 2 overhang -> last full source week 1
            new DateOnly(2026, 6, 23), // week 3 overhang -> last full source week 1
        });
        result.ShouldAllBe(a => a.Date.DayOfWeek == DayOfWeek.Tuesday);
    }

    [Test]
    public void Map_SourceLongerThanTarget_ExtraSourceWeeksAreDropped()
    {
        var agent = Guid.NewGuid();
        var prevFrom = new DateOnly(2026, 3, 2);   // Monday
        var prevUntil = new DateOnly(2026, 3, 29);  // Sunday, 4 full weeks
        var targetFrom = new DateOnly(2026, 6, 1);  // Monday
        var targetUntil = new DateOnly(2026, 6, 14); // Sunday, 2 full weeks

        var source = new[]
        {
            Work(agent, new DateOnly(2026, 3, 3)),  // Tuesday, week 0
            Work(agent, new DateOnly(2026, 3, 10)), // Tuesday, week 1
            Work(agent, new DateOnly(2026, 3, 17)), // Tuesday, week 2 (excess)
            Work(agent, new DateOnly(2026, 3, 24)), // Tuesday, week 3 (excess)
        };

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        var dates = result.Select(a => a.Date).OrderBy(d => d).ToList();
        dates.ShouldBe(new[]
        {
            new DateOnly(2026, 6, 2),
            new DateOnly(2026, 6, 9),
        });
    }

    [Test]
    public void Map_FirstPartialWeekWithoutSourcePendant_ReceivesNoSeed()
    {
        // Previous period starts on a Wednesday, so its Monday-anchor week (03-02..03-08) is only partial:
        // the target Monday 06-01's weekday-equal source date is 03-02, which lies BEFORE the previous
        // period start (03-04). Documented, accepted property: that first target day gets no seed.
        var agent = Guid.NewGuid();
        var prevFrom = new DateOnly(2026, 3, 4);   // Wednesday
        var prevUntil = new DateOnly(2026, 3, 17);  // Tuesday
        var targetFrom = new DateOnly(2026, 6, 1);  // Monday
        var targetUntil = new DateOnly(2026, 6, 14); // Sunday

        var source = new[]
        {
            Work(agent, new DateOnly(2026, 3, 9)),  // Monday of the first FULL source week (week 1)
        };

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        var dates = result.Select(a => a.Date).ToList();
        dates.ShouldContain(new DateOnly(2026, 6, 8));       // full-week Monday gets a seed
        dates.ShouldNotContain(new DateOnly(2026, 6, 1));    // first partial-week Monday: no seed
        result.Count.ShouldBe(1);
    }

    [Test]
    public void Map_OvernightWork_EndsOnFollowingDayOfTargetDate()
    {
        var agent = Guid.NewGuid();
        var prevFrom = new DateOnly(2026, 3, 2);   // Monday
        var prevUntil = new DateOnly(2026, 3, 8);   // Sunday, 1 full week
        var targetFrom = new DateOnly(2026, 6, 1);  // Monday
        var targetUntil = new DateOnly(2026, 6, 7);  // Sunday, 1 full week

        var source = new[]
        {
            Work(agent, new DateOnly(2026, 3, 4), startHour: 22, endHour: 6, hours: 8m), // Wednesday, 22:00-06:00
        };

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        result.Count.ShouldBe(1);
        var assignment = result[0];
        assignment.StartAt.ShouldBe(new DateOnly(2026, 6, 3).ToDateTime(new TimeOnly(22, 0)));
        assignment.EndAt.ShouldBe(new DateOnly(2026, 6, 4).ToDateTime(new TimeOnly(6, 0)));
        assignment.EndAt.Date.ShouldBe(assignment.StartAt.Date.AddDays(1));
    }

    [Test]
    public void Map_EmptySource_ReturnsEmpty()
    {
        var result = WarmStartWeekdayMapper.Map(
            [],
            new DateOnly(2026, 3, 2),
            new DateOnly(2026, 3, 29),
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 28));

        result.ShouldBeEmpty();
    }

    private static readonly object[] MonthDivergenceCases =
    {
        // 31 -> 30
        new object[] { new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30) },
        // 30 -> 31
        new object[] { new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31) },
        // February (28 days)
        new object[] { new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31) },
        // Leap February (29 days)
        new object[] { new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 31) },
    };

    [TestCaseSource(nameof(MonthDivergenceCases))]
    public void Map_MonthLengthDivergence_KeepsWeekdayTruthAndStaysWithinTarget(
        DateOnly prevFrom, DateOnly prevUntil, DateOnly targetFrom, DateOnly targetUntil)
    {
        var agent = Guid.NewGuid();
        // Source works only on Wednesdays: weekday-true mapping must place every result on a Wednesday.
        var source = new List<WarmStartSourceWork>();
        for (var d = prevFrom; d <= prevUntil; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Wednesday)
            {
                source.Add(Work(agent, d));
            }
        }

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        result.ShouldNotBeEmpty();
        result.ShouldAllBe(a => a.Date.DayOfWeek == DayOfWeek.Wednesday);
        result.ShouldAllBe(a => a.Date >= targetFrom && a.Date <= targetUntil);
    }

    private static readonly object[] WithinTargetCases =
    {
        new object[] { new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30) },
        new object[] { new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30) },
        new object[] { new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 15), new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 28) },
        new object[] { new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 31), new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31) },
    };

    [TestCaseSource(nameof(WithinTargetCases))]
    public void Map_DenseSource_AllResultDatesLieWithinTargetPeriod(
        DateOnly prevFrom, DateOnly prevUntil, DateOnly targetFrom, DateOnly targetUntil)
    {
        var agent = Guid.NewGuid();
        var source = new List<WarmStartSourceWork>();
        for (var d = prevFrom; d <= prevUntil; d = d.AddDays(1))
        {
            source.Add(Work(agent, d));
        }

        var result = WarmStartWeekdayMapper.Map(source, prevFrom, prevUntil, targetFrom, targetUntil);

        result.ShouldNotBeEmpty();
        result.ShouldAllBe(a => a.Date >= targetFrom && a.Date <= targetUntil);
    }
}
