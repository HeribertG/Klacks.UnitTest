// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.Objective;
using Klacks.ScheduleOptimizer.Wizard4;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Wizard4;

/// <summary>
/// Deterministic, exact-results ISOLATION tests for Wizard 4 (Background Optimizer). Pure in-memory:
/// no DB, no WebApplicationFactory — the Wizard4OptimizationCore is the Harmonizer GA with the
/// composite "Gesamtzustand" objective substituted in. Each test pins the engine Seed and sets NO
/// MaxRuntime (a wall-clock budget would truncate generations non-deterministically; the single RNG
/// is HarmonizerEvolutionLoop.cs:39).
///
/// We assert SCALAR composite fitness equality (to ~1e-12), NEVER cell-by-cell bitmap equality:
/// CompositeObjective iterates HashSet&lt;string&gt; and .NET randomizes string hashing per process, so a
/// golden bitmap is same-process-only and would FLAKE in CI. The scalar is hash-stable. The "exact
/// results" deliverable is the BEFORE/AFTER metrics block printed to TestContext.Out, so the run shows
/// what the wizard concretely improved.
/// </summary>
[TestFixture]
public class Wizard4IsolationExactResultsTests
{
    private static readonly DateOnly D1 = new(2026, 4, 20);
    private static readonly DateOnly D2 = new(2026, 4, 21);

    private static CoreAgent Agent(string id, double guaranteed) => new(
        Id: id, CurrentHours: 0, GuaranteedHours: guaranteed, MaxConsecutiveDays: 6,
        MinRestHours: 11, Motivation: 0.5, MaxDailyHours: 10, MaxWeeklyHours: 50, MaxOptimalGap: 2);

    private static CoreShift Shift(Guid id, DateOnly day, int cap) =>
        new(id.ToString(), "Shift", day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, cap, 0);

    private static Cell Work(DateOnly day, Guid shiftId, decimal hours) => new(
        CellSymbol.Early, shiftId, [], false,
        day.ToDateTime(new TimeOnly(8, 0)),
        day.ToDateTime(new TimeOnly(8, 0)).AddHours((double)hours),
        hours);

    // Snapshot: agent A carries both shifts (16h, over target), agent B is idle (0h, under target).
    // Copied verbatim from Wizard4OptimizationCoreTests (private static helpers are not reachable cross-class).
    private static (HarmonyBitmap Seed, CoreWizardContext Context) Scenario()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = D1, PeriodUntil = D2,
            Agents = [Agent("A", 8), Agent("B", 8)],
            Shifts = [Shift(s1, D1, cap: 1), Shift(s2, D2, cap: 1)],
        };

        var cells = new Cell[2, 2];
        for (var r = 0; r < 2; r++)
        {
            for (var d = 0; d < 2; d++)
            {
                cells[r, d] = Cell.Free();
            }
        }
        cells[0, 0] = Work(D1, s1, 8);
        cells[0, 1] = Work(D2, s2, 8);

        var bitmap = new HarmonyBitmap(
            [new BitmapAgent("A", "A", 8m, new HashSet<CellSymbol>()), new BitmapAgent("B", "B", 8m, new HashSet<CellSymbol>())],
            [D1, D2],
            cells);
        return (bitmap, context);
    }

    private static void ReportComposite(string label, ObjectiveResult r)
    {
        TestContext.Out.WriteLine($"  [{label}]");
        TestContext.Out.WriteLine($"    Composite scalar      : {r.Scalar:F12}");
        TestContext.Out.WriteLine($"    SubScore Fehler        : {r.SubScores.Fehler:F12}");
        TestContext.Out.WriteLine($"    SubScore Stundenabgleich: {r.SubScores.Stundenabgleich:F12}");
        TestContext.Out.WriteLine($"    SubScore Praeferenzen   : {r.SubScores.Praeferenzen:F12}");
        TestContext.Out.WriteLine($"    Gate MandatoryQualMiss : {r.Gate.MandatoryQualMissing}");
        TestContext.Out.WriteLine($"    Gate Legality          : {r.Gate.Legality}");
        TestContext.Out.WriteLine($"    Gate UnderSupply       : {r.Gate.UnderSupply}");
        TestContext.Out.WriteLine($"    Gate OverSupply        : {r.Gate.OverSupply}");
    }

    /// <summary>
    /// Evaluates the composite objective on the SEED (BEFORE), runs the W4 core with a fixed Seed and
    /// fixed MaxGenerations and NO MaxRuntime, evaluates the RESULT (AFTER), and prints both metric
    /// blocks to TestContext.Out. Asserts the composite scalar AFTER &gt;= BEFORE (SCALAR only).
    ///
    /// Why the no-regression assertion is guaranteed (CompositeBitmapFitnessEvaluator): an admissible
    /// candidate scores its [0,1] composite scalar; an inadmissible one scores a strictly negative value.
    /// The seed is admissible against itself (baseline penalty 0), so its loop fitness equals its scalar.
    /// Elitism carries the seed, hence BestFitness &gt;= BaselineScalar &gt;= 0; a non-negative winner is
    /// therefore admissible, so Best.Scalar == BestFitness &gt;= Baseline.Scalar. We read the engine's own
    /// Baseline/Best (computed on the row-sorted seed and the re-scored winner) to avoid a sorted-vs-
    /// unsorted discrepancy.
    /// </summary>
    [Test]
    public void Wizard4_Optimize_ImprovesComposite_AndReports()
    {
        var (seed, context) = Scenario();
        var core = new Wizard4OptimizationCore();
        var config = new HarmonizerEvolutionConfig(Seed: 42, MaxGenerations: 25);

        var result = core.Optimize(seed, context, new DomainAwareReplaceValidator(null), config);

        TestContext.Out.WriteLine("Wizard 4 (Background Optimizer) — composite 'Gesamtzustand' BEFORE vs AFTER:");
        ReportComposite("BEFORE (snapshot / seed)", result.Baseline);
        ReportComposite("AFTER (best arrangement)", result.Best);
        TestContext.Out.WriteLine(
            $"  Scalar delta (AFTER - BEFORE): {(result.Best.Scalar - result.Baseline.Scalar):F12}");

        // SCALAR-only no-regression assertion (elitism guarantee). The -1e-9 absorbs FP noise when the
        // tiny 2x2 scenario yields no improvement and elitism returns the seed (equality holds).
        result.Best.Scalar.ShouldBeGreaterThanOrEqualTo(result.Baseline.Scalar - 1e-9);
    }

    /// <summary>
    /// Mirrors Wizard4OptimizationCoreTests.Optimize_IsDeterministic_ForASeededConfig: two runs with
    /// Seed:42, MaxGenerations:10, no MaxRuntime; asserts BestFitness and the composite Best.Scalar are
    /// equal to ~1e-12.
    ///
    /// NOTE: this is SAME-PROCESS determinism only. CompositeObjective iterates HashSet&lt;string&gt; and .NET
    /// randomizes string hashing per process, so the winning arrangement is not guaranteed bit-identical
    /// across processes / CI runs. That is precisely why we assert the hash-stable SCALAR fitness, never
    /// a golden bitmap.
    /// </summary>
    [Test]
    public void Wizard4_SameSeed_IsScalarReproducible()
    {
        var (seed1, context1) = Scenario();
        var (seed2, context2) = Scenario();
        var core = new Wizard4OptimizationCore();

        var a = core.Optimize(seed1, context1, new DomainAwareReplaceValidator(null), new HarmonizerEvolutionConfig(Seed: 42, MaxGenerations: 10));
        var b = core.Optimize(seed2, context2, new DomainAwareReplaceValidator(null), new HarmonizerEvolutionConfig(Seed: 42, MaxGenerations: 10));

        b.BestFitness.ShouldBe(a.BestFitness, 1e-12);
        b.Best.Scalar.ShouldBe(a.Best.Scalar, 1e-12);
    }
}
