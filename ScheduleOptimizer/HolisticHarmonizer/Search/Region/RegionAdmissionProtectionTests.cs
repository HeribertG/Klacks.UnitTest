// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search.Region;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search.Region;

/// <summary>
/// The region admission refuses a rebuild that breaks the stage-3 protection: on <see cref="HoursFlowDownPlan"/> the
/// rebuilt plan passes every other check (hard rules, target hours, fitness floor), so the protection is the only thing
/// that can turn it down. Without the protection the same rebuild is admitted.
/// </summary>
[TestFixture]
public class RegionAdmissionProtectionTests
{
    private static readonly int[] TouchedRows = [HoursFlowDownPlan.HigherRow, HoursFlowDownPlan.LowerRow];

    [Test]
    public void Admit_RebuildHandingHoursToALowerRank_RefusedOnlyWithTheProtection()
    {
        var input = HoursFlowDownPlan.Input();
        var components = Components(input);
        var original = BitmapBuilder.Build(input);
        var rebuilt = Rebuilt(original);
        IReadOnlyList<PlanCellSwap> steps = [HoursFlowDownPlan.HandDownStep()];

        var withoutProtection = Admission(components, protection: null).Admit(original, rebuilt, TouchedRows, steps, double.NegativeInfinity);
        var withProtection = Admission(components, components.Protection).Admit(original, rebuilt, TouchedRows, steps, double.NegativeInfinity);

        withoutProtection.ShouldNotBeNull("every other check admits the rebuild");
        withProtection.ShouldBeNull();
    }

    private static HarmonyBitmap Rebuilt(HarmonyBitmap original)
    {
        var rebuilt = BitmapCloner.Clone(original);
        PlanMutationValidator.Apply(rebuilt, HoursFlowDownPlan.HandDownStep());
        return rebuilt;
    }

    private static RegionAdmission Admission(HolisticHarmonizerComponents components, BundleProtection? protection)
        => new(components.Validator, components.Fitness, new TargetHoursDeviationGuard(), protection);

    private static HolisticHarmonizerComponents Components(BitmapInput input)
        => HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
}
