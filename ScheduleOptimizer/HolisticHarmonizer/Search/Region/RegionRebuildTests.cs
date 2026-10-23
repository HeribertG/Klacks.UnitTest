// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search.Region;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search.Region;

/// <summary>
/// The region rebuild may only rearrange cells within a day (coverage untouched), never move a locked cell, never
/// leave a rule-breaking cell behind, never fall below the per-row fitness it started with, and never lower the
/// global harmony. The fixture is a classic, rule-valid plan of scenario 4 (15 agents, 28 days, 3 early, 3 late and
/// 3 night shifts per day) with room for a better rhythm.
/// </summary>
[TestFixture]
public sealed class RegionRebuildTests
{
    private static readonly string[] ClassicPlan =
    [
        "EEELLL__NN___LNNNN______EEEE",
        "___ENNN____EEELLL__LLLL__NNN",
        "__NN______EENN__EENN__ELLLLL",
        "_EEEEE___EENN__LLLL__LNNN___",
        "EE___NNN___LLLL__LLLL_ELLL__",
        "LLL__EEEEL__NNN___EENN__LLLL",
        "LLLL__LLLNN______LLLNN___NNN",
        "NN__EEEL__LLLL_____EEENN__EE",
        "NNNNN__EEENN___EEEE_LLL_____",
        "____LLLNN__EE_LLL__NN__EEENN",
        "____LLL__NNN__ENNNN__NNNNN__",
        "LLLL__EEEEE_EEEE__EEEE______",
        "E___NNNNN____EEENN__EEEEN___",
        "__EEE___LLLLL___EENN__LL__LL",
        "NNNN___LLLL__NNN_______EEEEE",
    ];

    private static (HarmonyBitmap Bitmap, HolisticHarmonizerComponents Components) Build()
    {
        var input = HarmonyTestPlans.Input(ClassicPlan);
        var components = HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        return (BitmapBuilder.Build(input), components);
    }

    private static RegionRebuildPhase Phase(HolisticHarmonizerComponents components)
        => new(new RegionAdmission(components.Validator, components.Fitness, new TargetHoursDeviationGuard()),
            new GlobalHarmonyEvaluator(), RegionRebuildOptions.Default with { WallClockBudgetSeconds = 600 });

    private static string[] Columns(HarmonyBitmap bitmap)
        => Enumerable.Range(0, bitmap.DayCount)
            .Select(d => string.Concat(Enumerable.Range(0, bitmap.RowCount).Select(r => (int)bitmap.GetCell(r, d).Symbol).OrderBy(x => x)))
            .ToArray();

    [Test]
    public void Phase_RaisesTheHarmonyWithoutGivingAnythingUp()
    {
        var (bitmap, components) = Build();
        var before = BitmapCloner.Clone(bitmap);
        var fitnessBefore = components.Fitness.Evaluate(bitmap).Fitness;

        var result = Phase(components).Run(bitmap);

        result.HarmonyAfter.Total.ShouldBeGreaterThan(result.HarmonyBefore.Total);
        components.Fitness.Evaluate(bitmap).Fitness.ShouldBeGreaterThanOrEqualTo(fitnessBefore - 1e-9);
        Columns(bitmap).ShouldBe(Columns(before));
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                components.Validator.DiagnoseCellInPlace(bitmap, r, d).ShouldBeNull($"cell r{r} d{d}");
            }
        }
    }

    [Test]
    public void Phase_KeepsTargetHoursAndIsDeterministic()
    {
        var (first, components) = Build();
        var (second, componentsAgain) = Build();

        Phase(components).Run(first);
        Phase(componentsAgain).Run(second);

        for (var r = 0; r < first.RowCount; r++)
        {
            HarmonyTestPlans.Row(first, r).ShouldBe(HarmonyTestPlans.Row(second, r));
            first.Row(r).Sum(c => c.Hours).ShouldBe(first.Rows[r].TargetHours, $"row {r}");
        }
    }

    [Test]
    public void BuildSteps_TurnTheBasePlanIntoTheTarget()
    {
        var (bitmap, _) = Build();
        var region = new HarmonyRegion([0, 3, 5], 2, 6);
        var target = BitmapCloner.Clone(bitmap);
        for (var d = region.DayFrom; d <= region.DayTo; d++)
        {
            var cells = region.Rows.Select(r => bitmap.GetCell(r, d)).ToArray();
            for (var i = 0; i < region.Rows.Count; i++)
            {
                target.SetCell(region.Rows[i], d, cells[(i + d) % cells.Length]);
            }
        }

        var replay = BitmapCloner.Clone(bitmap);
        foreach (var step in RegionRebuilder.BuildSteps(bitmap, target, region))
        {
            step.DayA.ShouldBe(step.DayB);
            PlanMutationValidator.Apply(replay, step);
        }

        for (var r = 0; r < bitmap.RowCount; r++)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                ReferenceEquals(replay.GetCell(r, d), target.GetCell(r, d)).ShouldBeTrue($"cell r{r} d{d}");
            }
        }
    }

    [Test]
    public void Rebuild_NeverMovesALockedCell()
    {
        var rows = (string[])ClassicPlan.Clone();
        rows[3] = "BBBBBBB" + rows[3][7..];
        var input = HarmonyTestPlans.Input(rows);
        var components = HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        var bitmap = BitmapBuilder.Build(input);
        var locked = Enumerable.Range(0, 7).Select(d => bitmap.GetCell(3, d)).ToArray();

        Phase(components).Run(bitmap);

        for (var d = 0; d < 7; d++)
        {
            ReferenceEquals(bitmap.GetCell(3, d), locked[d]).ShouldBeTrue($"day {d}");
        }
    }

    [Test]
    public void Regions_TouchOnlyWhenTheyShareARowOnNeighbouringDays()
    {
        var a = new HarmonyRegion([1, 2], 3, 5);

        a.Touches(new HarmonyRegion([2, 7], 6, 8)).ShouldBeTrue();
        a.Touches(new HarmonyRegion([2, 7], 7, 9)).ShouldBeFalse();
        a.Touches(new HarmonyRegion([4, 7], 3, 5)).ShouldBeFalse();
    }
}
