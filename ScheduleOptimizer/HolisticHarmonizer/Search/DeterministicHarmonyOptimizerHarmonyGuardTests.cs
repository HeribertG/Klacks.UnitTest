// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

/// <summary>
/// "Nothing gets worse": with the default options the deterministic stage 3 never lowers the global harmony of a plan
/// that starts as a canon, and still never lowers the per-row fitness. Explicitly constructed options keep the old
/// behaviour (no guard, no region rebuild).
/// </summary>
[TestFixture]
public sealed class DeterministicHarmonyOptimizerHarmonyGuardTests
{
    private static readonly string[] CanonPlan =
    [
        "EEEE_E__LLLLLL___NN__NNNN___",
        "___EEEEE__LLLLLL___NN__NNNN_",
        "__E__NNNN_____EEEEEE__LLLLLL",
        "LLLLLL___NN__NNNN_____EEE_EE",
        "____EEEEEE__LLLLLL___NN__NNN",
        "NNN_____EEEEEE__LLLLLL___NN_",
        "_NNNN_____EEEEEE__LLLLLL___N",
        "N__NNNN_____EEEEEE__LLLLLL__",
        "NN____EEEEEE__LLLLLL___NN__N",
        "__NNN__NNNN_____EEEEEE__LLLL",
        "LL__ENN__NNNN_____EEEEEE__LL",
        "LLLLL__NN__NNNN_____EEEEEE__",
        "__LLLLLL___NN__NNNN_____EEEE",
        "EE___LLLLL___NN__NNNN____EEE",
        "EEEE__LLLLLL___NN__NNNN_____",
    ];

    private static DeterministicSearchResult Run(DeterministicSearchOptions options)
    {
        var input = HarmonyTestPlans.Input(CanonPlan);
        var components = HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        var bitmap = RowSorter.Sort(BitmapBuilder.Build(input));
        return new DeterministicHarmonyOptimizer(components, options with { WallClockBudget = TimeSpan.FromMinutes(10) })
            .Run(bitmap, null, CancellationToken.None);
    }

    [Test]
    public void HarmonizedOptions_KeepTheHarmonyOfACanon()
    {
        var result = Run(DeterministicSearchOptions.Harmonized);

        result.HarmonyAfter!.Total.ShouldBeGreaterThanOrEqualTo(result.HarmonyBefore!.Total - 1e-9);
        result.FitnessAfter.ShouldBeGreaterThanOrEqualTo(result.FitnessBefore - 1e-9);
    }

    [Test]
    public void HarmonizedOptions_SwitchTheSecondInstanceOn()
    {
        DeterministicSearchOptions.Harmonized.HarmonyGuard.ShouldBeTrue();
        DeterministicSearchOptions.Harmonized.HarmonyTieBreak.ShouldBeTrue();
        DeterministicSearchOptions.Harmonized.RegionRebuild.ShouldNotBeNull();
    }

    [Test]
    public void DefaultOptions_StayThePlainLocalSearch()
    {
        DeterministicSearchOptions.Default.HarmonyGuard.ShouldBeFalse();
        DeterministicSearchOptions.Default.RegionRebuild.ShouldBeNull();
    }

    [Test]
    public void HarmonizedOptions_FitnessChainOfTheAppliedBatchesNeverFalls()
    {
        var result = Run(DeterministicSearchOptions.Harmonized);

        var previous = result.FitnessBefore;
        foreach (var batch in result.AppliedBatches)
        {
            batch.ScoreBefore.ShouldBe(previous, 1e-9);
            batch.ScoreAfter.ShouldBeGreaterThanOrEqualTo(batch.ScoreBefore - 1e-9);
            previous = batch.ScoreAfter;
        }

        result.FitnessAfter.ShouldBe(previous, 1e-9);
    }

    [Test]
    public void ExplicitOptions_KeepTheOldBehaviour()
    {
        var options = new DeterministicSearchOptions(10, 5, 10, 7, 42, TimeSpan.FromSeconds(30), 1000);

        options.HarmonyGuard.ShouldBeFalse();
        options.RegionRebuild.ShouldBeNull();
    }
}
