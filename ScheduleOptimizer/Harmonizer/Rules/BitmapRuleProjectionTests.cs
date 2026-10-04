// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

[TestFixture]
public class BitmapRuleProjectionTests
{
    private static readonly int[] Seeds = Enumerable.Range(1, 12).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void Project_InitialBitmap_EqualsFromBitmapInputCellByCell(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var bitmap = BitmapBuilder.Build(input);

        var projected = runtime.Projection.Project(bitmap);
        var exact = RulePlanFactory.FromBitmapInput(runtime.Projection.Context, input);

        for (var agent = 0; agent < projected.AgentCount; agent++)
        {
            for (var day = 0; day < projected.DayCount; day++)
            {
                AssertSameDay(projected.Get(agent, day), exact.Get(agent, day));
            }
        }

        runtime.Evaluator.Evaluate(projected).ShouldBeEquivalentTo(runtime.Evaluator.Evaluate(exact));
    }

    [Test]
    public void DayOf_MovedNightCell_IsClassifiedWithTheReceivingAgentsNightWindow()
    {
        var input = PlanningRuleBitmapFixture.Build(3, PlanningRuleBitmapFixture.MixedRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var nightAt = new DateTime(2026, 3, 4, 23, 0, 0, DateTimeKind.Utc);
        var dawn = new Cell(CellSymbol.Early, Guid.NewGuid(), [], false, nightAt.AddHours(-17.5), nightAt.AddHours(-9.5), 8m);
        var night = new Cell(CellSymbol.Night, Guid.NewGuid(), [], false, nightAt, nightAt.AddHours(8), 8m);

        var withWindow = runtime.Projection.DayOf(night, 1);
        var withoutWindow = runtime.Projection.DayOf(night, 0);
        var shortDawn = runtime.Projection.DayOf(dawn, 1);

        withWindow.Has(RuleShiftKind.Night).ShouldBeTrue();
        withoutWindow.Has(RuleShiftKind.Night).ShouldBeTrue("fallback to the night symbol without a window");
        shortDawn.Has(RuleShiftKind.Night).ShouldBeFalse("05:30 start overlaps the window by 30 min, not more than 60");
        shortDawn.NightSegmentCount.ShouldBe(1, "PeriodCount night counting keeps any overlap");
    }

    [Test]
    public void DayOf_IgnoredWorks_DoNotCount()
    {
        var probe = PlanningRuleBitmapFixture.Build(5, PlanningRuleBitmapFixture.MixedRules());
        var worked = probe.Assignments.First(a => a.Symbol == CellSymbol.Night);
        var ignored = new HashSet<Guid>(worked.WorkIds);
        var input = PlanningRuleBitmapFixture.Build(5, PlanningRuleBitmapFixture.MixedRules(), ignored);
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var bitmap = BitmapBuilder.Build(input);
        var row = bitmap.Rows.ToList().FindIndex(a => a.Id == worked.AgentId);
        var day = worked.Date.DayNumber - input.StartDate.DayNumber;

        var ruleDay = runtime.Projection.DayOf(bitmap.GetCell(row, day), runtime.Projection.AgentIndexOf(bitmap.Rows[row]));

        ruleDay.NightSegmentCount.ShouldBe(0);
    }

    [Test]
    public void TryCreate_WithoutRules_ReturnsNull()
    {
        BitmapRuleRuntime.TryCreate(PlanningRuleBitmapFixture.Build(1, null)).ShouldBeNull();
        BitmapRuleRuntime.TryCreate(PlanningRuleBitmapFixture.Build(1, [])).ShouldBeNull();
    }

    private static void AssertSameDay(RuleDay actual, RuleDay expected)
    {
        actual.Flags.ShouldBe(expected.Flags);
        actual.SegmentCount.ShouldBe(expected.SegmentCount);
        actual.NightSegmentCount.ShouldBe(expected.NightSegmentCount);
        foreach (var minutes in new[] { 0m, 240m, 420m, 480m, 600m })
        {
            actual.CountSegmentsLongerThan(minutes).ShouldBe(expected.CountSegmentsLongerThan(minutes));
        }
    }
}
