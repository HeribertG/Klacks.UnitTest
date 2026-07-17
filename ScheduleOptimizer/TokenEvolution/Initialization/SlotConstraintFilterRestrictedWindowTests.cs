// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// K16 restricted-time-window veto in the Wizard-1 slot filter: a restricted shift inside the seasonal
/// daily window is rejected, the same shift outside the season is allowed, an untagged shift is never
/// vetoed, and the year-boundary wrap season is honoured.
/// </summary>

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class SlotConstraintFilterRestrictedWindowTests
{
    private static readonly Guid RestrictedShift = Guid.NewGuid();
    private static readonly Guid OtherShift = Guid.NewGuid();

    private static CoreAgent Agent() => new(
        Id: "A", CurrentHours: 0, GuaranteedHours: 0, MaxConsecutiveDays: 0,
        MinRestHours: 0, Motivation: 0.5, MaxDailyHours: 0, MaxWeeklyHours: 0, MaxOptimalGap: 0)
    {
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext Context(params CoreRestrictedTimeWindow[] windows) => new()
    {
        RestrictedTimeWindows = windows,
    };

    private static CoreRestrictedTimeWindow MiddayBan() =>
        new(6, 15, 9, 15, (12 * 60) + 30, 15 * 60, new HashSet<Guid> { RestrictedShift });

    private static CoreRestrictedTimeWindow WinterBan() =>
        new(11, 15, 2, 15, 12 * 60, 14 * 60, new HashSet<Guid> { RestrictedShift });

    [Test]
    public void RestrictedShiftInsideSeasonAndWindow_IsRejected()
    {
        var valid = SlotConstraintFilter.IsValidAssignment(
            Agent(), new DateOnly(2026, 7, 1), 1, RestrictedShift, 4m, Context(MiddayBan()), [],
            new DateTime(2026, 7, 1, 12, 0, 0), new DateTime(2026, 7, 1, 16, 0, 0));

        valid.ShouldBeFalse();
    }

    [Test]
    public void RestrictedShiftOutsideSeason_IsAllowed()
    {
        var valid = SlotConstraintFilter.IsValidAssignment(
            Agent(), new DateOnly(2026, 10, 1), 1, RestrictedShift, 4m, Context(MiddayBan()), [],
            new DateTime(2026, 10, 1, 12, 0, 0), new DateTime(2026, 10, 1, 16, 0, 0));

        valid.ShouldBeTrue();
    }

    [Test]
    public void UntaggedShiftInsideWindow_IsAllowed()
    {
        var valid = SlotConstraintFilter.IsValidAssignment(
            Agent(), new DateOnly(2026, 7, 1), 1, OtherShift, 4m, Context(MiddayBan()), [],
            new DateTime(2026, 7, 1, 12, 0, 0), new DateTime(2026, 7, 1, 16, 0, 0));

        valid.ShouldBeTrue();
    }

    [Test]
    public void RestrictedShiftInsideWrapSeason_IsRejected()
    {
        var valid = SlotConstraintFilter.IsValidAssignment(
            Agent(), new DateOnly(2026, 12, 21), 1, RestrictedShift, 4m, Context(WinterBan()), [],
            new DateTime(2026, 12, 21, 12, 0, 0), new DateTime(2026, 12, 21, 16, 0, 0));

        valid.ShouldBeFalse();
    }
}
