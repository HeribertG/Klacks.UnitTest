// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Loop;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;

[TestFixture]
public class DeterministicHarmonyOptimizerTests
{
    private const int FixtureSeed = 11;
    private const int OtherFixtureSeed = 23;
    private const int SearchSeed = 42;
    private static readonly TimeSpan GenerousBudget = TimeSpan.FromMinutes(10);

    private static DeterministicSearchOptions Options => DeterministicSearchOptions.Default with
    {
        WallClockBudget = GenerousBudget,
        Seed = SearchSeed,
    };

    [TestCase(FixtureSeed)]
    [TestCase(OtherFixtureSeed)]
    public void Run_SameInputAndSeed_ProducesIdenticalPlanAndBatches(int fixtureSeed)
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(fixtureSeed);

        // Act
        var (first, firstResult) = Run(input, Options);
        var (second, secondResult) = Run(input, Options);

        // Assert
        firstResult.StopReason.ShouldNotBe(DeterministicSearchStopReason.WallClockBudget);
        firstResult.AppliedBatches.Count.ShouldBeGreaterThan(0);
        Snapshot(second).ShouldBe(Snapshot(first));
        secondResult.FitnessAfter.ShouldBe(firstResult.FitnessAfter);
        secondResult.StopReason.ShouldBe(firstResult.StopReason);
        secondResult.Evaluations.ShouldBe(firstResult.Evaluations);
        secondResult.AppliedBatches.Select(Describe).ShouldBe(firstResult.AppliedBatches.Select(Describe));
    }

    [Test]
    public void Run_ExhaustiveSameDayNeighbourhood_IsDeterministicAndAtLeastAsGoodAsThePool()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var full = Options with { IncludeAllSameDaySwaps = true };
        var before = Stage3BenchmarkMetrics.CountHardViolations(DeterministicSearchFixture.BuildBitmap(input));

        // Act
        var (first, firstResult) = Run(input, full);
        var (second, secondResult) = Run(input, full);

        // Assert
        firstResult.StopReason.ShouldNotBe(DeterministicSearchStopReason.WallClockBudget);
        Snapshot(second).ShouldBe(Snapshot(first));
        secondResult.Evaluations.ShouldBe(firstResult.Evaluations);
        firstResult.FitnessAfter.ShouldBeGreaterThan(firstResult.FitnessBefore);
        Stage3BenchmarkMetrics.CountHardViolations(first).ShouldBeLessThanOrEqualTo(before);
    }

    [Test]
    public void Constructor_AlternatingNeighbourhoodsWithoutValidator_Throws()
    {
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, int.MaxValue);

        Options.AlternateNeighbourhoods.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => new DeterministicHarmonyOptimizer(
            components.Evaluator, components.Pool, fitness, Options));
    }

    [TestCase(FixtureSeed)]
    [TestCase(OtherFixtureSeed)]
    public void Run_AlternatingNeighbourhoods_NeverEndWorseThanEitherNeighbourhoodAlone(int fixtureSeed)
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(fixtureSeed);
        var twoPasses = Options with { Restarts = 2 };

        // Act
        var (_, poolOnly) = Run(input, twoPasses with { Restarts = 1, AlternateNeighbourhoods = false });
        var (_, fullOnly) = Run(input, twoPasses with { Restarts = 1, AlternateNeighbourhoods = false, IncludeAllSameDaySwaps = true });
        var (_, alternating) = Run(input, twoPasses);

        // Assert
        alternating.RestartsRun.ShouldBe(2);
        alternating.FitnessAfter.ShouldBe(Math.Max(poolOnly.FitnessAfter, fullOnly.FitnessAfter));
    }

    [Test]
    public void Constructor_ExhaustiveNeighbourhoodWithoutValidator_Throws()
    {
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, int.MaxValue);

        Should.Throw<ArgumentNullException>(() => new DeterministicHarmonyOptimizer(
            components.Evaluator, components.Pool, fitness, Options with { IncludeAllSameDaySwaps = true }));
    }

    [Test]
    public void Run_ScoreNeverDrops_AndFinalFitnessMatchesUncachedEvaluator()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);

        // Act
        var (final, result) = Run(input, Options);

        // Assert
        result.FitnessAfter.ShouldBeGreaterThan(result.FitnessBefore);
        var previous = result.FitnessBefore;
        foreach (var batch in result.AppliedBatches)
        {
            batch.Result.ShouldBe(BatchAcceptance.Accepted);
            batch.ScoreBefore.ShouldBe(previous);
            batch.ScoreAfter.ShouldBeGreaterThanOrEqualTo(batch.ScoreBefore);
            previous = batch.ScoreAfter;
        }
        new HarmonyFitnessEvaluator(new HarmonyScorer()).Evaluate(final).Fitness.ShouldBe(result.FitnessAfter);
    }

    [TestCase(FixtureSeed)]
    [TestCase(OtherFixtureSeed)]
    public void Run_IntroducesNoNewHardViolations(int fixtureSeed)
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(fixtureSeed);
        var before = Stage3BenchmarkMetrics.CountHardViolations(DeterministicSearchFixture.BuildBitmap(input));

        // Act
        var (final, _) = Run(input, Options);

        // Assert
        Stage3BenchmarkMetrics.CountHardViolations(final).ShouldBeLessThanOrEqualTo(before);
    }

    [Test]
    public void Run_TrialEvaluationsAreReverted_OnlyTheAppliedBatchChangesThePlan()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var original = DeterministicSearchFixture.BuildBitmap(input);

        // Act
        var (final, result) = Run(input, Options with { MaxIterations = 1 });

        // Assert
        result.StopReason.ShouldBe(DeterministicSearchStopReason.IterationLimit);
        var replay = BitmapCloner.Clone(original);
        foreach (var step in result.AppliedBatches.Single().AppliedSteps)
        {
            Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation.PlanMutationValidator.Apply(replay, step);
        }
        Snapshot(final).ShouldBe(Snapshot(replay));
    }

    [Test]
    public void Run_EvaluationLimit_StopsWithoutLosingScore()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        const long evaluationCap = 3;

        // Act
        var (_, result) = Run(input, Options with { MaxEvaluations = evaluationCap, Restarts = 1 });

        // Assert
        result.StopReason.ShouldBe(DeterministicSearchStopReason.EvaluationLimit);
        result.Evaluations.ShouldBeInRange(evaluationCap, evaluationCap + 1);
        result.AppliedBatches.Count.ShouldBeLessThanOrEqualTo(1);
        result.FitnessAfter.ShouldBeGreaterThanOrEqualTo(result.FitnessBefore);
    }

    [Test]
    public void Run_WallClockBudgetExhausted_StopsAndKeepsPlan()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var original = DeterministicSearchFixture.BuildBitmap(input);

        // Act
        var (final, result) = Run(input, Options with { WallClockBudget = TimeSpan.Zero });

        // Assert
        result.StopReason.ShouldBe(DeterministicSearchStopReason.WallClockBudget);
        result.AppliedBatches.ShouldBeEmpty();
        result.FitnessAfter.ShouldBe(result.FitnessBefore);
        Snapshot(final).ShouldBe(Snapshot(original));
    }

    [Test]
    public void Run_WallClockBudgetHitsMidSearch_StopsWithWallClockReason()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        const int budgetTicks = 50;
        var clock = new SteppingTimeProvider();

        // Act
        var (_, result) = Run(input, Options with { WallClockBudget = TimeSpan.FromSeconds(budgetTicks) }, clock);

        // Assert
        result.StopReason.ShouldBe(DeterministicSearchStopReason.WallClockBudget);
        result.FitnessAfter.ShouldBeGreaterThanOrEqualTo(result.FitnessBefore);
    }

    [Test]
    public void Run_NoImprovementPossible_StopsAtLocalOptimumWithUnchangedScore()
    {
        // Arrange: one neighbourhood only - with alternation the winning pass is a local optimum of its own
        // neighbourhood, and a pool-pass winner can still improve in the larger same-day neighbourhood.
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var poolOnly = Options with { AlternateNeighbourhoods = false };
        var (optimised, first) = Run(input, poolOnly);
        first.StopReason.ShouldBeOneOf(DeterministicSearchStopReason.LocalOptimum, DeterministicSearchStopReason.NoImprovementLimit);

        // Act
        var (_, second) = RunOn(input, optimised, poolOnly);

        // Assert
        second.FitnessAfter.ShouldBe(second.FitnessBefore);
        second.AppliedBatches.ShouldBeEmpty();
    }

    [TestCase(FixtureSeed)]
    [TestCase(OtherFixtureSeed)]
    public void Run_KeptBatchesEndWithAStrictImprovement(int fixtureSeed)
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(fixtureSeed);

        // Act
        var (final, result) = Run(input, Options);

        // Assert
        result.AppliedBatches[^1].ScoreAfter.ShouldBeGreaterThan(result.AppliedBatches[^1].ScoreBefore);
        result.AppliedBatches[^1].ScoreAfter.ShouldBe(result.FitnessAfter);
        var replay = DeterministicSearchFixture.BuildBitmap(input);
        foreach (var step in result.AppliedBatches.SelectMany(b => b.AppliedSteps))
        {
            Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation.PlanMutationValidator.Apply(replay, step);
        }
        Snapshot(final).ShouldBe(Snapshot(replay));
    }

    [Test]
    public void Run_AppliedSwapIsNotReappliedWithinTabuTenureUnlessItStrictlyImproves()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(OtherFixtureSeed);
        var options = Options;

        // Act
        var (_, result) = Run(input, options);

        // Assert
        // Exactly one batch is applied per iteration, so the list index is the iteration.
        var appliedAt = new List<(int Iteration, ForbiddenSwapKey Key)>();
        for (var iteration = 0; iteration < result.AppliedBatches.Count; iteration++)
        {
            foreach (var step in result.AppliedBatches[iteration].AppliedSteps.Where(s => s.DayA == s.DayB))
            {
                var key = ForbiddenSwapKey.From(step);
                var batch = result.AppliedBatches[iteration];
                if (batch.ScoreAfter <= batch.ScoreBefore)
                {
                    appliedAt
                        .Where(a => a.Key == key && iteration - a.Iteration <= options.TabuTenure)
                        .ShouldBeEmpty();
                }
                appliedAt.Add((iteration, key));
            }
        }
    }

    [TestCase(FixtureSeed)]
    [TestCase(OtherFixtureSeed)]
    public void Run_MoreRestarts_NeverEndWorseThanTheFirstPassAlone(int fixtureSeed)
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(fixtureSeed);

        // Act
        var (_, single) = Run(input, Options with { Restarts = 1 });
        var (_, multi) = Run(input, Options);

        // Assert
        multi.RestartsRun.ShouldBe(Options.Restarts);
        multi.FitnessAfter.ShouldBeGreaterThanOrEqualTo(single.FitnessAfter);
        multi.Evaluations.ShouldBeGreaterThan(single.Evaluations);
    }

    [Test]
    public void Run_Cancelled_Throws()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, int.MaxValue);
        var optimizer = new DeterministicHarmonyOptimizer(components, Options);

        // Act & Assert
        Should.Throw<OperationCanceledException>(() =>
            optimizer.Run(DeterministicSearchFixture.BuildBitmap(input), progress: null, cts.Token));
    }

    [Test]
    public void Run_ReportsProgressPerAppliedBatch()
    {
        // Arrange
        var input = DeterministicSearchFixture.BuildInput(FixtureSeed);
        var reports = new List<HolisticHarmonizerProgress>();
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, int.MaxValue);
        var optimizer = new DeterministicHarmonyOptimizer(components, Options);

        // Act
        var result = optimizer.Run(DeterministicSearchFixture.BuildBitmap(input), new SynchronousProgress(reports), CancellationToken.None);

        // Assert
        reports.Count.ShouldBe(Options.Restarts + 1);
        reports.Select(r => r.IterationIndex).ShouldBe(Enumerable.Range(0, Options.Restarts + 1));
        reports.Select(r => r.BestFitness).ShouldBeInOrder(SortDirection.Ascending);
        reports[^1].BestFitness.ShouldBe(result.FitnessAfter);
        reports[^1].AcceptedBatchCount.ShouldBe(result.AppliedBatches.Count);
        reports.ShouldAllBe(r => r.MaxIterations == Options.Restarts && r.RejectedBatchCount == 0);
    }

    private static (HarmonyBitmap Final, DeterministicSearchResult Result) Run(
        BitmapInput input, DeterministicSearchOptions options, TimeProvider? clock = null)
        => RunOn(input, DeterministicSearchFixture.BuildBitmap(input), options, clock);

    private static (HarmonyBitmap Final, DeterministicSearchResult Result) RunOn(
        BitmapInput input, HarmonyBitmap start, DeterministicSearchOptions options, TimeProvider? clock = null)
    {
        var working = BitmapCloner.Clone(start);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(input, fitness, int.MaxValue);
        var optimizer = new DeterministicHarmonyOptimizer(components, options, clock);
        return (working, optimizer.Run(working, progress: null, CancellationToken.None));
    }

    private static string Snapshot(HarmonyBitmap bitmap)
    {
        var cells = new List<string>();
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                var cell = bitmap.GetCell(r, d);
                cells.Add($"{r}:{d}:{cell.Symbol}:{string.Join('+', cell.WorkIds)}");
            }
        }
        return string.Join('|', cells);
    }

    private static string Describe(BatchEvaluation batch)
        => $"{batch.BatchId}:{batch.Intent}:{string.Join(',', batch.AppliedSteps.Select(s => $"{s.RowA}/{s.DayA}-{s.RowB}/{s.DayB}"))}:{batch.ScoreAfter:R}";

    private sealed class SynchronousProgress(List<HolisticHarmonizerProgress> sink) : IProgress<HolisticHarmonizerProgress>
    {
        public void Report(HolisticHarmonizerProgress value) => sink.Add(value);
    }

    /// <summary>Advances one second on every read, so a budget of N seconds expires after N clock reads.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks += TimeSpan.TicksPerSecond;
    }
}
