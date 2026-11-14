// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

/// <summary>
/// Proves that the stage-3 protection is applied by the wiring the chain uses (HolisticHarmonizerComponents.Build and the
/// optimizer built from its components), not only by the protection class itself. The plan is <see cref="HoursFlowDownPlan"/>:
/// the one move that brings both rows closer to their target hours hands hours from the higher rank to the lower one. Each
/// test has a control without the protection that shows the move is otherwise acceptable, so the test cannot pass for
/// another reason.
/// </summary>
[TestFixture]
public class HolisticHarmonizerComponentsProtectionWiringTests
{
    private const int SearchSeed = 42;
    private static readonly TimeSpan GenerousBudget = TimeSpan.FromMinutes(10);

    [Test]
    public void Build_EvaluatorRefusesAMoveThatHandsHoursToALowerRank()
    {
        var input = HoursFlowDownPlan.Input();
        var components = Components(input);
        var batch = new MutationBatch(Guid.NewGuid(), "redistribute_load", Iteration: 0, Steps: [HoursFlowDownPlan.HandDownStep()]);

        var protectedResult = components.Evaluator.Evaluate(BitmapBuilder.Build(input), batch);
        var control = new BatchEvaluator(components.Validator, components.Fitness, committee: null, new TargetHoursDeviationGuard())
            .Evaluate(BitmapBuilder.Build(input), batch);

        control.Rejections.Any(r => r.Reason == PlanMutationRejectionReason.ProtectionBroken).ShouldBeFalse();
        control.AppliedSteps.ShouldNotBeEmpty("without the protection the move is legal and the target-hours guard accepts it");
        protectedResult.Result.ShouldBe(BatchAcceptance.Rejected);
        protectedResult.Rejections[^1].Reason.ShouldBe(PlanMutationRejectionReason.ProtectionBroken);
    }

    [Test]
    public void Run_OptimizerBuiltFromTheComponents_NeverHandsHoursToALowerRank()
    {
        var input = HoursFlowDownPlan.Input();
        var components = Components(input);
        var unprotectedEvaluator = new BatchEvaluator(
            components.Validator, components.Fitness, committee: null, new TargetHoursDeviationGuard());
        var control = new DeterministicHarmonyOptimizer(
            unprotectedEvaluator, components.Pool, components.Fitness, Options, null, components.Validator, protection: null);
        var controlPlan = BitmapBuilder.Build(input);
        var protectedPlan = BitmapBuilder.Build(input);

        control.Run(controlPlan, progress: null, CancellationToken.None);
        new DeterministicHarmonyOptimizer(components, Options).Run(protectedPlan, progress: null, CancellationToken.None);

        HoursFlowDownPlan.WorkedDays(controlPlan, HoursFlowDownPlan.HigherRow)
            .ShouldBeLessThan(HoursFlowDownPlan.HigherRowText.Length, "without the protection the search hands a shift down");
        HoursFlowDownPlan.WorkedDays(protectedPlan, HoursFlowDownPlan.HigherRow).ShouldBe(HoursFlowDownPlan.HigherRowText.Length);
        HoursFlowDownPlan.WorkedDays(protectedPlan, HoursFlowDownPlan.LowerRow).ShouldBe(HoursFlowDownPlan.LowerRowText.Count(c => c != '_'));
    }

    private static DeterministicSearchOptions Options => DeterministicSearchOptions.Default with
    {
        WallClockBudget = GenerousBudget,
        Seed = SearchSeed,
    };

    private static HolisticHarmonizerComponents Components(BitmapInput input)
        => HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
}
