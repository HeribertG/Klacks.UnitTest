// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/**
 * Computes the harmonizer research score and exposes its individual components so the
 * autoresearch loop can reason about *why* a change is better or worse, not just *that*
 * it is. Lower total = better.
 */

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Research;

public sealed record ResearchScoreBreakdown(
    double Total,
    double FragmentationPenalty,
    double BlockShorteningPenalty,
    double ViolationPenalty,
    double FairnessPenalty,
    double ScorerLiePenalty,
    double FragmentationDelta,
    double BlockLengthDelta,
    int ViolationsAfter,
    double FairnessDelta,
    bool ScorerLies);

public static class ResearchScore
{
    public const double WeightFragmentation = 1.0;
    public const double WeightBlockLength = 1.0;
    public const double WeightViolation = 5.0;
    public const double WeightFairness = 0.05;
    public const double WeightLie = 2.0;

    public static ResearchScoreBreakdown Compute(
        ScheduleQualityReport before,
        ScheduleQualityReport after,
        double harmonyBefore,
        double harmonyAfter)
    {
        var fragmentationDelta = SafeRelative(after.TotalBlocks, before.TotalBlocks);
        var blockLenDelta = SafeRelative(before.AvgBlockLength, after.AvgBlockLength);

        var fragmentationPenalty = WeightFragmentation * Math.Max(0.0, fragmentationDelta);
        var blockShorteningPenalty = WeightBlockLength * Math.Max(0.0, blockLenDelta);
        var violationPenalty = WeightViolation * after.ConsecutiveDayViolations;

        var fairnessAfter = (double)after.TargetHoursAbsoluteDeviation;
        var fairnessBefore = (double)before.TargetHoursAbsoluteDeviation;
        var fairnessDelta = Math.Max(0.0, fairnessAfter - fairnessBefore);
        var fairnessPenalty = WeightFairness * fairnessDelta;

        var qualityImproved =
            after.TotalBlocks <= before.TotalBlocks
            && after.AvgBlockLength >= before.AvgBlockLength
            && after.ConsecutiveDayViolations <= before.ConsecutiveDayViolations;
        var harmonyClaimsBetter = harmonyAfter > harmonyBefore + 1e-6;
        var scorerLies = harmonyClaimsBetter && !qualityImproved;
        var scorerLiePenalty = scorerLies ? WeightLie : 0.0;

        var total = fragmentationPenalty
                  + blockShorteningPenalty
                  + violationPenalty
                  + fairnessPenalty
                  + scorerLiePenalty;

        return new ResearchScoreBreakdown(
            Total: total,
            FragmentationPenalty: fragmentationPenalty,
            BlockShorteningPenalty: blockShorteningPenalty,
            ViolationPenalty: violationPenalty,
            FairnessPenalty: fairnessPenalty,
            ScorerLiePenalty: scorerLiePenalty,
            FragmentationDelta: fragmentationDelta,
            BlockLengthDelta: blockLenDelta,
            ViolationsAfter: after.ConsecutiveDayViolations,
            FairnessDelta: fairnessDelta,
            ScorerLies: scorerLies);
    }

    private static double SafeRelative(double after, double before)
    {
        if (before <= 0)
        {
            return after > 0 ? 1.0 : 0.0;
        }
        return (after - before) / before;
    }
}
