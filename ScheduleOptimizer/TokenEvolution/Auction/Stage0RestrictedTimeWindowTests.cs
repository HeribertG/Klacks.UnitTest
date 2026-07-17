// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// K16 restricted-time-window veto mirrored in the Stage-0 hard-constraint checker: a restricted shift
/// inside the seasonal daily window is vetoed with the RestrictedTimeWindow rule name; an untagged shift
/// is not.
/// </summary>

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction;

[TestFixture]
public class Stage0RestrictedTimeWindowTests
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

    private static CoreShift Slot(Guid shiftId) =>
        new(shiftId.ToString(), string.Empty, "2026-07-01", "12:00", "16:00", 4, 1, 0);

    private static CoreWizardContext Context() => new()
    {
        Agents = [Agent()],
        RestrictedTimeWindows =
        [
            new CoreRestrictedTimeWindow(6, 15, 9, 15, (12 * 60) + 30, 15 * 60, new HashSet<Guid> { RestrictedShift }),
        ],
    };

    [Test]
    public void RestrictedShiftInsideWindow_IsVetoed()
    {
        var verdict = new Stage0HardConstraintChecker().Check(Agent(), Slot(RestrictedShift), [], Context());

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("RestrictedTimeWindow");
    }

    [Test]
    public void UntaggedShiftInsideWindow_IsNotVetoed()
    {
        var verdict = new Stage0HardConstraintChecker().Check(Agent(), Slot(OtherShift), [], Context());

        verdict.ShouldBeNull();
    }
}
