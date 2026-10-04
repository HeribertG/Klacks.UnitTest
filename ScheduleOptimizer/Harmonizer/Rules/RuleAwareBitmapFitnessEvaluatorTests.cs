// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

[TestFixture]
public class RuleAwareBitmapFitnessEvaluatorTests
{
    private const double Tolerance = 1e-9;
    private const int MovesPerSeed = 300;
    private static readonly int[] Seeds = Enumerable.Range(1, 8).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void SoftPenalty_EqualsPlanWideEvaluation_AcrossRandomSwaps(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var context = PlanningRuleBitmapFixture.Context(input);
        var oracle = PlanRuleEvaluatorFactory.Create(input.Rules!.Rules, context);
        var inner = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var sut = new RuleAwareBitmapFitnessEvaluator(inner, runtime);
        var bitmap = RowSorter.Sort(BitmapBuilder.Build(input));
        var random = new Random(seed);
        var positive = 0;

        for (var move = 0; move < MovesPerSeed; move++)
        {
            var expected = oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, bitmap)).SoftPenalty;
            var result = sut.Evaluate(bitmap);
            var harmony = inner.Evaluate(bitmap);

            sut.SoftPenalty(bitmap).ShouldBe(expected, Tolerance);
            result.Fitness.ShouldBe(harmony.Fitness - (RuleAwareBitmapFitnessEvaluator.DefaultSoftPenaltyWeight * expected / bitmap.RowCount), Tolerance);
            result.RowScores.ShouldBe(harmony.RowScores);
            positive += expected > 0 ? 1 : 0;

            var rowA = random.Next(bitmap.RowCount);
            var rowB = random.Next(bitmap.RowCount);
            var dayA = random.Next(bitmap.DayCount);
            var dayB = random.Next(3) == 0 ? random.Next(bitmap.DayCount) : dayA;
            if (!bitmap.GetCell(rowA, dayA).IsLocked && !bitmap.GetCell(rowB, dayB).IsLocked)
            {
                PlanningRuleBitmapFixture.Swap(bitmap, rowA, dayA, rowB, dayB);
            }
        }

        positive.ShouldBeGreaterThan(0, "the soft rules must bind on the fixture");
    }

    [Test]
    public void Evaluate_HardOnlyRuleSet_HasNoSoftRules()
    {
        var runtime = BitmapRuleRuntime.TryCreate(PlanningRuleBitmapFixture.Build(2, PlanningRuleBitmapFixture.HardRules()))!;

        runtime.HasSoftRules.ShouldBeFalse();
        runtime.HasHardRules.ShouldBeTrue();
    }

    [Test]
    public void Constructor_NegativeWeight_Throws()
    {
        var runtime = BitmapRuleRuntime.TryCreate(PlanningRuleBitmapFixture.Build(2, PlanningRuleBitmapFixture.SoftRules()))!;

        Should.Throw<ArgumentOutOfRangeException>(() => new RuleAwareBitmapFitnessEvaluator(new HarmonyFitnessEvaluator(new HarmonyScorer()), runtime, -1));
    }
}
