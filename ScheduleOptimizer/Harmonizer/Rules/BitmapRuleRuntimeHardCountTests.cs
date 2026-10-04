// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

[TestFixture]
public class BitmapRuleRuntimeHardCountTests
{
    private static readonly int[] Seeds = Enumerable.Range(1, 6).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void CountHard_EqualsTheHardCountOfAFreshEvaluationOfEachPlan(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var before = BitmapBuilder.Build(input);
        var after = BitmapBuilder.Build(input);
        PlanningRuleBitmapFixture.Swap(after, 0, 0, 1, 1);
        var context = PlanningRuleBitmapFixture.Context(input);
        var oracle = PlanRuleEvaluatorFactory.Create(input.Rules!.Rules, context);

        var counts = runtime.CountHard(before, after);

        counts.Before.ShouldBe(oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, before)).HardCount);
        counts.After.ShouldBe(oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, after)).HardCount);
    }

    [Test]
    public void CountHard_SoftRulesOnly_CountsNoHardFinding()
    {
        var input = PlanningRuleBitmapFixture.Build(2, PlanningRuleBitmapFixture.SoftRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var bitmap = BitmapBuilder.Build(input);

        runtime.CountHard(bitmap, bitmap).ShouldBe(new PlanningRuleHardCounts(0, 0));
    }
}
