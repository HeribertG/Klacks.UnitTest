// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * "Production deterministic" arm: the shipped DeterministicHarmonyOptimizer with the same composition the Api
 * stage uses (HolisticHarmonizerComponents, memoized fitness, untrimmed candidate pool).
 * @param options - search limits; the benchmark passes the production defaults or a pair-cap sweep value
 */

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

public static class Stage3DeterministicArm
{
    public static (HarmonyBitmap FinalBitmap, DeterministicSearchResult Result) Run(
        BitmapInput input,
        HarmonyBitmap stage2Bitmap,
        DeterministicSearchOptions options)
    {
        var working = BitmapCloner.Clone(stage2Bitmap);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        var optimizer = new DeterministicHarmonyOptimizer(components, options);
        var result = optimizer.Run(working, progress: null, CancellationToken.None);
        return (working, result);
    }
}
