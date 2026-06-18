// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.Wizard4;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Wizard4;

[TestFixture]
public class Wizard4OptimizationCoreTests
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

    [Test]
    public void Optimize_NeverReturnsAnArrangementWorseThanTheSnapshot()
    {
        var (seed, context) = Scenario();
        var core = new Wizard4OptimizationCore();
        var config = new HarmonizerEvolutionConfig(Seed: 7, MaxGenerations: 12);

        var result = core.Optimize(seed, context, new DomainAwareReplaceValidator(null), config);

        // keep-if-better: the seed is admissible against itself and carried by elitism.
        result.BestFitness.ShouldBeGreaterThanOrEqualTo(result.BaselineScalar - 1e-9);
        result.BestBitmap.RowCount.ShouldBe(2);
        result.BestBitmap.DayCount.ShouldBe(2);
        result.Baseline.ShouldNotBeNull();
        result.Best.ShouldNotBeNull();
    }

    [Test]
    public void Optimize_IsDeterministic_ForASeededConfig()
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
