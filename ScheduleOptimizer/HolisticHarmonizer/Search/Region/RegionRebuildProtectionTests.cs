// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search.Region;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search.Region;

/// <summary>
/// The region rebuild drops the states that break the stage-3 protection on the last day of a region BEFORE it cuts the
/// beam. With a beam of one state the difference is visible: cutting first keeps only the best-ranked finished state,
/// so the region yields a rebuild only when that state passes the admission with the protection (equivalently: the
/// unprotected rebuild returns it and it keeps the protection); dropping first may still find a protected state. The
/// fixture is the classic plan of <see cref="RegionRebuildTests"/>.
/// </summary>
[TestFixture]
public sealed class RegionRebuildProtectionTests
{
    private const int SingleStateBeam = 1;
    private const int ProposalRounds = 30;

    [Test]
    public void Rebuild_TheBestRankedStateBreaksTheProtection_StillFindsAProtectedRebuild()
    {
        var input = HarmonyTestPlans.Input(RegionRebuildTests.ClassicPlan);
        var bitmap = BitmapBuilder.Build(input);
        var components = HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        var unprotected = new RegionAdmission(components.Validator, components.Fitness, new TargetHoursDeviationGuard());
        var protectedAdmission = new RegionAdmission(
            components.Validator, components.Fitness, new TargetHoursDeviationGuard(), components.Protection);
        var harmonyEvaluator = new GlobalHarmonyEvaluator();
        var unprotectedRebuilder = new RegionRebuilder(unprotected, harmonyEvaluator, SingleStateBeam);
        var protectedRebuilder = new RegionRebuilder(protectedAdmission, harmonyEvaluator, SingleStateBeam);
        var rhythm = new RhythmMatrix(bitmap);
        var harmony = harmonyEvaluator.Evaluate(bitmap, rhythm);
        var floor = unprotected.Fitness(bitmap);
        var rng = new Random(RegionRebuildOptions.DefaultSeed);

        var rescued = 0;
        for (var round = 0; round < ProposalRounds; round++)
        {
            foreach (var region in RegionProposer.Propose(bitmap, rhythm, RegionRebuildOptions.Default, rng))
            {
                var best = unprotectedRebuilder.Rebuild(bitmap, rhythm, floor, harmony, region);
                var cutFirstFinds = best is not null && protectedAdmission.KeepsProtection(best.Bitmap, protectedAdmission.CaptureProtection(bitmap, region.Rows));
                var rebuilt = protectedRebuilder.Rebuild(bitmap, rhythm, floor, harmony, region);
                if (rebuilt is null)
                {
                    cutFirstFinds.ShouldBeFalse("dropping refused states first never loses a rebuild cutting first would find");
                    continue;
                }

                protectedAdmission.KeepsProtection(rebuilt.Bitmap, protectedAdmission.CaptureProtection(bitmap, region.Rows)).ShouldBeTrue();
                if (!cutFirstFinds)
                {
                    rescued++;
                }
            }
        }

        rescued.ShouldBeGreaterThan(0, "some region must yield a protected rebuild only because refused states are dropped first");
    }
}
