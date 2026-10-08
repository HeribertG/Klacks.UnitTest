// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Common.Rotation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Common.Rotation;

/// <summary>
/// Pins the shared rotation rule of all autofill engines (tests/autofill/SPEC-ROTATION-2026-10-08.md) on hand-made days.
/// </summary>
[TestFixture]
public sealed class ShiftRotationTests
{
    private const int Early = 0;
    private const int Late = 1;
    private const int Night = 2;

    private static readonly DateOnly Day1 = new(2026, 3, 2);

    private static readonly Func<int, bool> AllAllowed = _ => true;

    [Test]
    public void ARestOfFortyHours_KeepsTheBlock_FortyEightHoursSplitIt()
    {
        var early = Shift(1, Early);
        var afterOneFreeDay = Shift(3, Early);
        var afterTwoFreeDays = Shift(4, Early);

        ShiftRotation.IsBlockBoundary(early, afterOneFreeDay).ShouldBeFalse("14:00 to 06:00 two days later is 40 h.");
        ShiftRotation.IsBlockBoundary(early, afterTwoFreeDays).ShouldBeTrue("14:00 to 06:00 three days later is 64 h.");
    }

    [TestCase(Early, Late)]
    [TestCase(Late, Night)]
    [TestCase(Night, Early)]
    public void TheIdealSuccessor_FollowsEarlyLateNightEarly(int previous, int expected)
    {
        ShiftRotation.IdealSuccessor(previous, longPause: false, AllAllowed).ShouldBe(expected);
    }

    [Test]
    public void ADisallowedKind_IsSkipped()
    {
        ShiftRotation.IdealSuccessor(Late, longPause: false, kind => kind != Night).ShouldBe(Early);
    }

    [Test]
    public void AtMostOneAllowedKind_OwesNoRotation()
    {
        ShiftRotation.IdealSuccessor(Early, longPause: false, kind => kind == Early).ShouldBeNull();
    }

    [Test]
    public void AfterALongPause_TheCycleRestartsAtTheFirstAllowedKindFromEarly()
    {
        ShiftRotation.IdealSuccessor(Early, longPause: true, AllAllowed).ShouldBe(Early);
        ShiftRotation.IdealSuccessor(Night, longPause: true, kind => kind != Early).ShouldBe(Late);
    }

    [Test]
    public void ALongPause_StartsAtSevenFreeCalendarDays()
    {
        var night = Shift(1, Night);

        ShiftRotation.IsLongPause(night, Shift(9, Early)).ShouldBeFalse("The night ends on day 2; days 3 to 8 are six free days.");
        ShiftRotation.IsLongPause(night, Shift(10, Early)).ShouldBeTrue("Days 3 to 9 are seven free days.");
    }

    [Test]
    public void Assess_CountsInBlockChangesAgainstTheBlockStart_AndNonIdealTransitions()
    {
        var days = new List<RotationDay>
        {
            Shift(1, Early), Shift(2, Late), Shift(3, Late),
            Shift(7, Late),
        };

        var assessment = ShiftRotation.Assess(days, Day1, (_, _) => true);

        assessment.InBlockSteps.ShouldBe(2);
        assessment.InBlockChanges.ShouldBe(2, "Both late days depart from the block's early start.");
        assessment.Transitions.ShouldBe(1);
        assessment.NonIdealTransitions.ShouldBe(1, "After a block ending late the ideal is night.");
    }

    [Test]
    public void Assess_JudgesTheFirstPlannedBlockAgainstTheCarryIn_WithoutCountingTheCarryInItself()
    {
        var carryIn = Shift(-3, Night);
        var days = new List<RotationDay> { carryIn, Shift(1, Early), Shift(2, Early) };

        var assessment = ShiftRotation.Assess(days, Day1, (_, _) => true);

        assessment.Transitions.ShouldBe(1);
        assessment.NonIdealTransitions.ShouldBe(0, "Night to early is the normal cycle step.");
        assessment.InBlockSteps.ShouldBe(1);
    }

    [Test]
    public void DaysOf_KeepsTheEarliestAndTheLatestKindOfASplitDuty()
    {
        var date = Day1;
        var shifts = new[]
        {
            (date, Night, date.ToDateTime(new TimeOnly(22, 0)), date.ToDateTime(new TimeOnly(22, 0)).AddHours(8)),
            (date, Early, date.ToDateTime(new TimeOnly(6, 0)), date.ToDateTime(new TimeOnly(10, 0))),
        };

        var day = ShiftRotation.DaysOf(shifts).Single();

        day.FirstKindIndex.ShouldBe(Early);
        day.LastKindIndex.ShouldBe(Night);
    }

    private static RotationDay Shift(int dayNumber, int kind)
    {
        var date = Day1.AddDays(dayNumber - 1);
        var start = date.ToDateTime(new TimeOnly(6, 0)).AddHours(kind * 8);
        return new RotationDay(date, kind, kind, start, start.AddHours(8));
    }
}
