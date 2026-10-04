// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

/// <summary>
/// The scratch plan is projected incrementally (only days whose cell reference changed) and shared by the hard guard
/// and the soft fitness term. Interleaving both on one runtime - including guard calls that leave the scratch row in
/// the after-move state on the fast path - must give exactly the verdicts and penalties of a fresh runtime that
/// projects every row from scratch.
/// </summary>
[TestFixture]
public class BitmapRuleRuntimeScratchTests
{
    private const int MovesPerSeed = 300;
    private static readonly int[] Seeds = Enumerable.Range(1, 8).ToArray();

    [TestCaseSource(nameof(Seeds))]
    public void SharedIncrementalScratch_MatchesAFreshRuntime_OnInterleavedGuardAndFitnessCalls(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var shared = BitmapRuleRuntime.TryCreate(input)!;
        var sharedGuard = new PlanningRuleMoveGuard(shared);
        var sharedFitness = new RuleAwareBitmapFitnessEvaluator(new HarmonyFitnessEvaluator(new HarmonyScorer()), shared);
        var bitmap = BitmapBuilder.Build(input);
        var random = new Random(seed * 104729);

        for (var move = 0; move < MovesPerSeed; move++)
        {
            var rowA = random.Next(bitmap.RowCount);
            var rowB = random.Next(bitmap.RowCount);
            var dayA = random.Next(bitmap.DayCount);
            var dayB = random.Next(bitmap.DayCount);
            var cellA = bitmap.GetCell(rowA, dayA);
            var cellB = bitmap.GetCell(rowB, dayB);
            var freshGuard = new PlanningRuleMoveGuard(BitmapRuleRuntime.TryCreate(input)!);

            sharedGuard.Diagnose(bitmap, rowA, [(dayA, cellB)], "rowA")
                .ShouldBe(freshGuard.Diagnose(bitmap, rowA, [(dayA, cellB)], "rowA"), $"seed {seed} move {move} rowA");
            sharedGuard.Diagnose(bitmap, rowB, [(dayB, cellA)], "rowB")
                .ShouldBe(new PlanningRuleMoveGuard(BitmapRuleRuntime.TryCreate(input)!).Diagnose(bitmap, rowB, [(dayB, cellA)], "rowB"), $"seed {seed} move {move} rowB");

            if (move % 3 == 0)
            {
                sharedFitness.SoftPenalty(bitmap)
                    .ShouldBe(new RuleAwareBitmapFitnessEvaluator(new HarmonyFitnessEvaluator(new HarmonyScorer()), BitmapRuleRuntime.TryCreate(input)!).SoftPenalty(bitmap), $"seed {seed} move {move} soft");
            }

            if (random.NextDouble() < 0.5)
            {
                PlanningRuleBitmapFixture.Swap(bitmap, rowA, dayA, rowB, dayB);
            }
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public void ProjectIntoScratch_AfterGuardCalls_HoldsExactlyTheRowsProjection(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.HardRules());
        var runtime = BitmapRuleRuntime.TryCreate(input)!;
        var guard = new PlanningRuleMoveGuard(runtime);
        var bitmap = BitmapBuilder.Build(input);
        var random = new Random(seed);

        for (var move = 0; move < MovesPerSeed; move++)
        {
            var row = random.Next(bitmap.RowCount);
            var otherRow = random.Next(bitmap.RowCount);
            var day = random.Next(bitmap.DayCount);
            guard.Diagnose(bitmap, row, [(day, bitmap.GetCell(otherRow, day))], "rowA");

            var probeRow = random.Next(bitmap.RowCount);
            var agentIndex = runtime.Projection.AgentIndexOf(bitmap.Rows[probeRow]);
            var scratch = runtime.ProjectIntoScratch(bitmap, probeRow, agentIndex);
            var expected = new RulePlan(runtime.Projection.Context);
            runtime.Projection.ProjectRow(bitmap, probeRow, agentIndex, expected);
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                AssertSameDay(scratch.Get(agentIndex, d), expected.Get(agentIndex, d), $"seed {seed} move {move} day {d}");
            }

            if (random.NextDouble() < 0.5)
            {
                PlanningRuleBitmapFixture.Swap(bitmap, row, day, otherRow, day);
            }
        }
    }

    private static void AssertSameDay(RuleDay actual, RuleDay expected, string because)
    {
        actual.Flags.ShouldBe(expected.Flags, because);
        actual.SegmentCount.ShouldBe(expected.SegmentCount, because);
        actual.NightSegmentCount.ShouldBe(expected.NightSegmentCount, because);
        foreach (var minutes in new[] { 0m, 240m, 420m, 480m, 600m })
        {
            actual.CountSegmentsLongerThan(minutes).ShouldBe(expected.CountSegmentsLongerThan(minutes), because);
        }
    }
}
