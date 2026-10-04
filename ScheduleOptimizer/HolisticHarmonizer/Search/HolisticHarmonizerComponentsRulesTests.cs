// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee.Agents;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

/// <summary>
/// Gate 1 of constraint-schema stage 4: without planning rules the composition root installs no hook at all
/// (same fitness instance, no guard, plain rotation agent), so fitness and search are byte-identical on every
/// HarmonizerEvalScenarioFactory scenario whether BitmapInput.Rules is null or an empty rule set. With rules the
/// search never creates or worsens a hard finding (independent oracle recount).
/// </summary>
[TestFixture]
public class HolisticHarmonizerComponentsRulesTests
{
    private static readonly DeterministicSearchOptions Options = DeterministicSearchOptions.Default with
    {
        WallClockBudget = TimeSpan.FromMinutes(10),
    };

    private static BitmapPlanningRules EmptyRules => new([], [], PlanningRuleBitmapFixture.NightMinOverlap);

    [Test]
    public void Build_WithoutRules_KeepsThePassedFitnessInstance()
    {
        var input = DeterministicSearchFixture.BuildInput(7);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());

        var withNull = HolisticHarmonizerComponents.Build(input, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        var withEmpty = HolisticHarmonizerComponents.Build(input with { Rules = EmptyRules }, fitness, HolisticHarmonizerComponents.UntrimmedPool);

        withNull.Fitness.ShouldBeSameAs(fitness);
        withEmpty.Fitness.ShouldBeSameAs(fitness);
        withNull.Rules.ShouldBeNull();
        withEmpty.Rules.ShouldBeNull();
    }

    [Test]
    public void EvalScenarios_FitnessThroughComponents_IsBitIdenticalToPlainHarmonyFitness()
    {
        foreach (var scenario in HarmonizerEvalScenarioFactory.CreateAll())
        {
            var bitmap = RowSorter.Sort(BitmapBuilder.Build(scenario.Input));
            var plain = new HarmonyFitnessEvaluator(new HarmonyScorer()).Evaluate(bitmap);
            foreach (var input in new[] { scenario.Input, scenario.Input with { Rules = EmptyRules } })
            {
                var components = HolisticHarmonizerComponents.Build(input, new HarmonyFitnessEvaluator(new HarmonyScorer()), MoveCandidatePool.DefaultTopPerIntent);
                var result = components.Fitness.Evaluate(bitmap);

                BitConverter.DoubleToInt64Bits(result.Fitness).ShouldBe(BitConverter.DoubleToInt64Bits(plain.Fitness), scenario.Name);
                result.RowScores.Select(BitConverter.DoubleToInt64Bits).ShouldBe(plain.RowScores.Select(BitConverter.DoubleToInt64Bits), scenario.Name);
            }
        }
    }

    [Test]
    public void EvalScenarios_SearchWithEmptyRuleSet_IsIdenticalToSearchWithoutRules()
    {
        foreach (var scenario in HarmonizerEvalScenarioFactory.CreateAll())
        {
            var (plainBitmap, plainResult) = Run(scenario.Input);
            var (emptyBitmap, emptyResult) = Run(scenario.Input with { Rules = EmptyRules });

            Snapshot(emptyBitmap).ShouldBe(Snapshot(plainBitmap), scenario.Name);
            BitConverter.DoubleToInt64Bits(emptyResult.FitnessAfter).ShouldBe(BitConverter.DoubleToInt64Bits(plainResult.FitnessAfter), scenario.Name);
            emptyResult.Evaluations.ShouldBe(plainResult.Evaluations, scenario.Name);
            emptyResult.StopReason.ShouldBe(plainResult.StopReason, scenario.Name);
            emptyResult.AppliedBatches.Select(Describe).ShouldBe(plainResult.AppliedBatches.Select(Describe), scenario.Name);
        }
    }

    [Test]
    public void EvalScenarios_ValidationWithEmptyRuleSet_IsIdenticalForEverySwap()
    {
        foreach (var scenario in HarmonizerEvalScenarioFactory.CreateAll())
        {
            var bitmap = RowSorter.Sort(BitmapBuilder.Build(scenario.Input));
            var plain = HolisticHarmonizerComponents.Build(scenario.Input, new HarmonyFitnessEvaluator(new HarmonyScorer()), MoveCandidatePool.DefaultTopPerIntent);
            var empty = HolisticHarmonizerComponents.Build(scenario.Input with { Rules = EmptyRules }, new HarmonyFitnessEvaluator(new HarmonyScorer()), MoveCandidatePool.DefaultTopPerIntent);
            for (var rowA = 0; rowA < bitmap.RowCount; rowA++)
            {
                for (var dayA = 0; dayA < bitmap.DayCount; dayA++)
                {
                    for (var rowB = 0; rowB < bitmap.RowCount; rowB++)
                    {
                        for (var dayB = 0; dayB < bitmap.DayCount; dayB++)
                        {
                            var swap = new PlanCellSwap(rowA, dayA, rowB, dayB, string.Empty);
                            empty.Validator.Validate(bitmap, swap).ShouldBe(plain.Validator.Validate(bitmap, swap));
                        }
                    }
                }
            }
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Run_WithHardRules_NeverCreatesOrWorsensAHardFinding(int seed)
    {
        var input = PlanningRuleBitmapFixture.Build(seed, PlanningRuleBitmapFixture.MixedRules());
        var context = PlanningRuleBitmapFixture.Context(input);
        var oracle = PlanRuleEvaluatorFactory.Create(input.Rules!.Rules, context);
        var start = RowSorter.Sort(BitmapBuilder.Build(input));
        var before = PlanningRuleBitmapFixture.HardExcess(oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, start)));

        var (final, result) = Run(input, start);
        var after = PlanningRuleBitmapFixture.HardExcess(oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, final)));

        result.AppliedBatches.Count.ShouldBeGreaterThan(0);
        foreach (var (key, excess) in after)
        {
            excess.ShouldBeLessThanOrEqualTo(before.GetValueOrDefault(key), $"rule {key.RuleId} agent {key.AgentId}");
        }
    }

    [Test]
    public void RotationAgent_AbstainsForASymbolGovernedByAMaxConsecutiveRule()
    {
        var agents = new[] { "a", "b" }
            .Select(id => new BitmapAgent(id, id, 0m, new HashSet<CellSymbol>()))
            .ToList();
        var early = new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false);
        var late = new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false);
        var cells = new Cell[2, 3];
        cells[0, 0] = early;
        cells[0, 1] = early;
        cells[0, 2] = late;
        cells[1, 0] = late;
        cells[1, 1] = late;
        cells[1, 2] = early;
        var days = new[] { new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 4) };
        var bitmap = new HarmonyBitmap(agents, days, cells);
        var swap = new PlanCellSwap(0, 2, 1, 2, string.Empty);
        var governed = new PlanningRuleRunGovernance([new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Early, 5)]);

        new RotationConstraintAgent().Evaluate(bitmap, swap).Vote.ShouldBe(ConstraintAgentVote.Veto);
        new RotationConstraintAgent(governed).Evaluate(bitmap, swap).Vote.ShouldBe(ConstraintAgentVote.Veto, "row b still builds L L L, Late is not governed");
        var governsBoth = new PlanningRuleRunGovernance([new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Soft, 1, RuleShiftKind.Work, 5)]);
        new RotationConstraintAgent(governsBoth).Evaluate(bitmap, swap).Vote.ShouldBe(ConstraintAgentVote.Abstain);
    }

    private static (HarmonyBitmap Final, DeterministicSearchResult Result) Run(BitmapInput input, HarmonyBitmap? start = null)
    {
        var working = BitmapCloner.Clone(start ?? RowSorter.Sort(BitmapBuilder.Build(input)));
        var components = HolisticHarmonizerComponents.Build(input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        var result = new DeterministicHarmonyOptimizer(components, Options).Run(working, progress: null, CancellationToken.None);
        return (working, result);
    }

    private static string Snapshot(HarmonyBitmap bitmap)
        => string.Join('|', Enumerable.Range(0, bitmap.RowCount).SelectMany(r => Enumerable.Range(0, bitmap.DayCount)
            .Select(d => $"{r}:{d}:{bitmap.GetCell(r, d).Symbol}:{string.Join('+', bitmap.GetCell(r, d).WorkIds)}")));

    private static string Describe(BatchEvaluation batch)
        => $"{batch.BatchId}:{batch.Intent}:{string.Join(',', batch.AppliedSteps.Select(s => $"{s.RowA}/{s.DayA}-{s.RowB}/{s.DayB}"))}:{BitConverter.DoubleToInt64Bits(batch.ScoreAfter)}";
}
