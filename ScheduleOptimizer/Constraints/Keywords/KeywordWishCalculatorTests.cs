// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Keywords;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords;

/// <summary>
/// The single source of the keyword wishes: tier of a broken restriction, attraction of the positive wishes and the
/// edge cases of combined commands.
/// </summary>
[TestFixture]
public class KeywordWishCalculatorTests
{
    private static KeywordDay Day(params ScheduleCommandKeyword[] keywords) => KeywordDay.From(keywords)!;

    [Test]
    public void Free_WithAnyWork_IsBrokenFree_WithoutWork_IsClean()
    {
        var day = Day(ScheduleCommandKeyword.Free);

        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.Early).ShouldBe(new KeywordDayVerdict(KeywordWishOutcome.BrokenFree, false));
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.None).IsClean.ShouldBeTrue();
    }

    [Test]
    public void OnlyEarlyAndOnlyLate_CombineToFree_AndBreakInTheFreeTier_WithoutAttraction()
    {
        var day = Day(ScheduleCommandKeyword.OnlyEarly, ScheduleCommandKeyword.OnlyLate);

        day.Combined.ShouldBe(ScheduleCommandKeyword.Free);
        day.PositiveWish.ShouldBeNull();
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.Late).Broken.ShouldBe(KeywordWishOutcome.BrokenFree);
    }

    [Test]
    public void NoEarlyAndNoLate_LeaveOnlyNight_AsANoTierRestriction_ThatDoesNotAttract()
    {
        var day = Day(ScheduleCommandKeyword.NoEarly, ScheduleCommandKeyword.NoLate);

        day.Combined.ShouldBe(ScheduleCommandKeyword.OnlyNight);
        day.PositiveWish.ShouldBeNull();
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.Early).Broken.ShouldBe(KeywordWishOutcome.BrokenNo);
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.None).IsClean.ShouldBeTrue();
    }

    [Test]
    public void FreeAndNotFree_CombineToFree_AndDoNotAttract()
    {
        var day = Day(ScheduleCommandKeyword.Free, ScheduleCommandKeyword.NotFree);

        day.Combined.ShouldBe(ScheduleCommandKeyword.Free);
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.None).IsClean.ShouldBeTrue();
    }

    [Test]
    public void OnlyNight_WithAnEarlyShift_IsBrokenAndUnfulfilled()
    {
        var verdict = KeywordWishCalculator.EvaluateDay(Day(ScheduleCommandKeyword.OnlyNight), KeywordShiftKinds.Early);

        verdict.Broken.ShouldBe(KeywordWishOutcome.BrokenOnly);
        verdict.Unfulfilled.ShouldBeTrue();
    }

    [Test]
    public void OnlyNight_WithoutShift_IsUnfulfilled_WithANight_IsClean()
    {
        var day = Day(ScheduleCommandKeyword.OnlyNight);

        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.None).ShouldBe(new KeywordDayVerdict(null, true));
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.Night).IsClean.ShouldBeTrue();
    }

    [Test]
    public void NotFree_WithoutShift_IsUnfulfilled_WithAnyShift_IsClean()
    {
        var day = Day(ScheduleCommandKeyword.NotFree);

        day.RestrictionOutcome.ShouldBeNull();
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.None).ShouldBe(new KeywordDayVerdict(null, true));
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.Late).IsClean.ShouldBeTrue();
        KeywordWishCalculator.EvaluateDay(day, KeywordShiftKinds.Unclassified).IsClean.ShouldBeTrue();
    }

    [Test]
    public void NotFreeAndNoNight_WithANight_IsBrokenNo_AndFulfilled()
    {
        var verdict = KeywordWishCalculator.EvaluateDay(
            Day(ScheduleCommandKeyword.NotFree, ScheduleCommandKeyword.NoNight), KeywordShiftKinds.Night);

        verdict.ShouldBe(new KeywordDayVerdict(KeywordWishOutcome.BrokenNo, false));
    }

    [Test]
    public void UnclassifiedCell_IsAllowedUnderNoX_ButBreaksOnlyX()
    {
        KeywordWishCalculator.EvaluateDay(Day(ScheduleCommandKeyword.NoEarly), KeywordShiftKinds.Unclassified).IsClean.ShouldBeTrue();
        KeywordWishCalculator.EvaluateDay(Day(ScheduleCommandKeyword.OnlyEarly), KeywordShiftKinds.Unclassified)
            .Broken.ShouldBe(KeywordWishOutcome.BrokenOnly);
    }

    [Test]
    public void CompareBroken_OneFreeOutweighsAnyNumberOfOnly_AndOneOnlyAnyNumberOfNo()
    {
        var oneFree = new KeywordWishTally(BrokenFree: 1, BrokenOnly: 0, BrokenNo: 0, Unfulfilled: 0);
        var manyOnly = new KeywordWishTally(BrokenFree: 0, BrokenOnly: 1000, BrokenNo: 1000, Unfulfilled: 1000);
        var oneOnly = new KeywordWishTally(BrokenFree: 0, BrokenOnly: 1, BrokenNo: 0, Unfulfilled: 0);
        var manyNo = new KeywordWishTally(BrokenFree: 0, BrokenOnly: 0, BrokenNo: 1000, Unfulfilled: 0);

        oneFree.CompareBrokenTo(manyOnly).ShouldBeGreaterThan(0);
        oneOnly.CompareBrokenTo(manyNo).ShouldBeGreaterThan(0);
        manyNo.CompareBrokenTo(oneOnly).ShouldBeLessThan(0);
    }

    [Test]
    public void CompareBroken_IgnoresTheUnfulfilledWishes()
    {
        new KeywordWishTally(0, 0, 1, Unfulfilled: 5).CompareBrokenTo(new KeywordWishTally(0, 0, 1, Unfulfilled: 0)).ShouldBe(0);
    }

    [Test]
    public void RisesInAnyTier_IsTrueAlsoWhenAHigherTierFalls()
    {
        var before = new KeywordWishTally(BrokenFree: 1, BrokenOnly: 0, BrokenNo: 0, Unfulfilled: 0);
        var after = new KeywordWishTally(BrokenFree: 0, BrokenOnly: 1, BrokenNo: 0, Unfulfilled: 0);

        after.RisesInAnyTierAbove(before).ShouldBeTrue();
        after.CompareBrokenTo(before).ShouldBeLessThan(0, "lexicographically better, still a rise of the OnlyX tier");
        before.RisesInAnyTierAbove(before).ShouldBeFalse();
    }

    [Test]
    public void Evaluate_CountsPerDayAndListsTheItems()
    {
        var days = KeywordWishCalculator.BuildDays(
        [
            ("A", KeywordWishTestData.Monday, ScheduleCommandKeyword.Free),
            ("A", KeywordWishTestData.Monday.AddDays(1), ScheduleCommandKeyword.NotFree),
            ("B", KeywordWishTestData.Monday, ScheduleCommandKeyword.NoLate),
        ]);
        var worked = new Dictionary<(string, DateOnly), int>
        {
            [("A", KeywordWishTestData.Monday)] = KeywordShiftKinds.Early | KeywordShiftKinds.Late,
            [("B", KeywordWishTestData.Monday)] = KeywordShiftKinds.Late,
        };

        var result = KeywordWishCalculator.Evaluate(days, worked, collectItems: true);

        result.Tally.ShouldBe(new KeywordWishTally(BrokenFree: 1, BrokenOnly: 0, BrokenNo: 1, Unfulfilled: 1));
        result.Items.Count.ShouldBe(3);
        result.Items.ShouldContain(new KeywordWishItem("A", KeywordWishTestData.Monday.AddDays(1), ScheduleCommandKeyword.NotFree, KeywordWishOutcome.Unfulfilled));
    }
}
