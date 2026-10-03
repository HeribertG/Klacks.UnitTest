// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

[TestFixture]
public class MemoizedHarmonyFitnessEvaluatorTests
{
    private const int FixtureSeed = 11;
    private const int SwapSeed = 5;
    private const int SwapCount = 300;
    private const int TinyCache = 4;

    [TestCase(MemoizedHarmonyFitnessEvaluator.DefaultMaxEntries)]
    [TestCase(TinyCache)]
    public void Evaluate_RandomSwapSequence_IsBitIdenticalToUncachedEvaluator(int maxEntries)
    {
        // Arrange
        var bitmap = DeterministicSearchFixture.BuildBitmap(DeterministicSearchFixture.BuildInput(FixtureSeed));
        var reference = new HarmonyFitnessEvaluator(new HarmonyScorer());
        var memo = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer(), maxEntries);
        var random = new Random(SwapSeed);

        // Act & Assert
        for (var i = 0; i < SwapCount; i++)
        {
            var day = random.Next(bitmap.DayCount);
            var swap = new PlanCellSwap(random.Next(bitmap.RowCount), day, random.Next(bitmap.RowCount), day, string.Empty);
            PlanMutationValidator.Apply(bitmap, swap);
            var expected = reference.Evaluate(bitmap);
            var actual = memo.Evaluate(bitmap);
            actual.Fitness.ShouldBe(expected.Fitness);
            actual.RowScores.ShouldBe(expected.RowScores);
            if (i % 2 == 0)
            {
                PlanMutationValidator.Apply(bitmap, swap);
                memo.Evaluate(bitmap).Fitness.ShouldBe(reference.Evaluate(bitmap).Fitness);
            }
        }
        if (maxEntries > RowsTimesTwo(bitmap))
        {
            memo.Hits.ShouldBeGreaterThan(0);
        }
    }

    // A cache smaller than one bitmap evicts before any row can be reused; only the bigger one must hit.
    private static int RowsTimesTwo(Klacks.ScheduleOptimizer.Harmonizer.Bitmap.HarmonyBitmap bitmap) => bitmap.RowCount * 2;

    [Test]
    public void Evaluate_ClonedBitmap_ReusesCachedRows()
    {
        // Arrange
        var bitmap = DeterministicSearchFixture.BuildBitmap(DeterministicSearchFixture.BuildInput(FixtureSeed));
        var memo = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        memo.Evaluate(bitmap);
        var missesAfterFirst = memo.Misses;

        // Act
        memo.Evaluate(BitmapCloner.Clone(bitmap));

        // Assert
        memo.Misses.ShouldBe(missesAfterFirst);
        memo.Hits.ShouldBe(bitmap.RowCount);
    }

    [Test]
    public void Constructor_NonPositiveCacheSize_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer(), 0));
    }
}
