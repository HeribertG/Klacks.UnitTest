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
/// create a rule-breaking cell (a violation already in the base plan is tolerated, not passed on), never fall below the
/// per-row fitness it started with, and never lower the global harmony. The fixture is a classic, rule-valid plan of
/// scenario 4 (15 agents, 28 days, 3 early, 3 late and 3 night shifts per day) with room for a better rhythm.
/// </summary>
[TestFixture]
public sealed class RegionRebuildTests
{
    internal static readonly string[] ClassicPlan =
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

    private const int NonShiftWorkerRow = 15;
    private const int NonShiftWorkerLockedLateDay = 3;
    private const int NonShiftWorkerLateInterval = 4;
    private const string NonShiftWorkerPlan = "___L___L___L___L___L___L___L";
    private const int ProposalRounds = 6;

    private static (HarmonyBitmap Bitmap, HolisticHarmonizerComponents Components) Build()
        => Build(HarmonyTestPlans.Input(ClassicPlan));

    private static (HarmonyBitmap Bitmap, HolisticHarmonizerComponents Components) Build(BitmapInput input)
    {
        var components = HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        return (BitmapBuilder.Build(input), components);
    }

    private static RegionAdmission Admission(HolisticHarmonizerComponents components)
        => new(components.Validator, components.Fitness, new TargetHoursDeviationGuard());

    private static RegionRebuildPhase Phase(HolisticHarmonizerComponents components)
        => new(Admission(components), new GlobalHarmonyEvaluator(), RegionRebuildOptions.Default with { WallClockBudgetSeconds = 600 });

    /// <summary>
    /// The classic plan plus a 16th agent without shift work whose only duties are late duties every fourth day, so
    /// every window of four or more days holds one pre-existing violation.
    /// </summary>
    /// <param name="locked">True locks the late duties of the agent without shift work.</param>
    private static BitmapInput ClassicPlanWithLatesOfANonShiftWorker(bool locked)
    {
        var input = HarmonyTestPlans.Input([.. ClassicPlan, NonShiftWorkerPlan]);
        var nonShiftWorker = input.Agents[NonShiftWorkerRow];
        return input with
        {
            Agents = input.Agents.Select(a => a == nonShiftWorker ? a with { PerformsShiftWork = false } : a).ToList(),
            Assignments = input.Assignments.Select(a => a.AgentId == nonShiftWorker.Id ? a with { IsLocked = locked } : a).ToList(),
        };
    }

    /// <summary>
    /// Rebuilds the proposed regions of the plan, each widened by the row of the agent without shift work, and returns
    /// the first admitted rebuild (null when none is admitted). Regions span at least one late duty of that agent.
    /// </summary>
    private static RegionCandidate? FirstRebuildWithTheNonShiftWorker(HarmonyBitmap bitmap, RegionAdmission admission)
    {
        var harmonyEvaluator = new GlobalHarmonyEvaluator();
        var rebuilder = new RegionRebuilder(admission, harmonyEvaluator, RegionRebuildOptions.Default.BeamWidth);
        var rhythm = new RhythmMatrix(bitmap);
        var harmony = harmonyEvaluator.Evaluate(bitmap, rhythm);
        var floor = admission.Fitness(bitmap);
        var rng = new Random(RegionRebuildOptions.DefaultSeed);
        var options = RegionRebuildOptions.Default with { MinDays = NonShiftWorkerLateInterval };
        for (var round = 0; round < ProposalRounds; round++)
        {
            foreach (var proposed in RegionProposer.Propose(bitmap, rhythm, options, rng))
            {
                var rows = proposed.Rows.Append(NonShiftWorkerRow).Distinct().Order().ToArray();
                var region = new HarmonyRegion(rows, proposed.DayFrom, proposed.DayTo);
                var candidate = rebuilder.Rebuild(bitmap, rhythm, floor, harmony, region);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void ShouldCreateNoNewViolation(RegionAdmission admission, HarmonyBitmap bitmap, RegionCandidate candidate)
    {
        candidate.Region.Rows.ShouldContain(NonShiftWorkerRow);
        Enumerable.Range(candidate.Region.DayFrom, candidate.Region.DayTo - candidate.Region.DayFrom + 1)
            .ShouldContain(d => bitmap.GetCell(NonShiftWorkerRow, d).Symbol == CellSymbol.Late);
        foreach (var r in candidate.Region.Rows)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                admission.CreatesNewViolation(bitmap, candidate.Bitmap, r, d).ShouldBeFalse($"cell r{r} d{d}");
            }
        }
    }

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
    public void Rebuild_ALockedLateOfANonShiftWorker_DoesNotBlockTheOtherRows()
    {
        var (bitmap, components) = Build(ClassicPlanWithLatesOfANonShiftWorker(locked: true));
        var admission = Admission(components);
        bitmap.GetCell(NonShiftWorkerRow, NonShiftWorkerLockedLateDay).IsLocked.ShouldBeTrue();
        admission.CellValid(bitmap, NonShiftWorkerRow, NonShiftWorkerLockedLateDay).ShouldBeFalse();

        var candidate = FirstRebuildWithTheNonShiftWorker(bitmap, admission);

        candidate.ShouldNotBeNull();
        ShouldCreateNoNewViolation(admission, bitmap, candidate);
        for (var d = 0; d < bitmap.DayCount; d++)
        {
            ReferenceEquals(candidate.Bitmap.GetCell(NonShiftWorkerRow, d), bitmap.GetCell(NonShiftWorkerRow, d))
                .ShouldBeTrue($"day {d}");
        }
    }

    [Test]
    public void Rebuild_UnlockedLatesOfANonShiftWorker_StayInPlaceOrMoveToAValidRow()
    {
        var (bitmap, components) = Build(ClassicPlanWithLatesOfANonShiftWorker(locked: false));
        var admission = Admission(components);
        bitmap.GetCell(NonShiftWorkerRow, NonShiftWorkerLockedLateDay).IsLocked.ShouldBeFalse();
        admission.CellValid(bitmap, NonShiftWorkerRow, NonShiftWorkerLockedLateDay).ShouldBeFalse();

        var candidate = FirstRebuildWithTheNonShiftWorker(bitmap, admission);

        candidate.ShouldNotBeNull();
        ShouldCreateNoNewViolation(admission, bitmap, candidate);
        for (var d = 0; d < bitmap.DayCount; d++)
        {
            if (!admission.CellValid(candidate.Bitmap, NonShiftWorkerRow, d))
            {
                ReferenceEquals(candidate.Bitmap.GetCell(NonShiftWorkerRow, d), bitmap.GetCell(NonShiftWorkerRow, d))
                    .ShouldBeTrue($"day {d}");
            }
        }
    }

    [Test]
    public void CreatesNewViolation_AChangedNeighbourBreaksAPreviouslyValidCell_IsTrue()
    {
        var (bitmap, components) = Build(HarmonyTestPlans.Input("_L__", "__E_"));
        var admission = Admission(components);
        admission.CellValid(bitmap, 0, 1).ShouldBeTrue();
        var candidate = BitmapCloner.Clone(bitmap);
        candidate.SetCell(0, 2, bitmap.GetCell(1, 2));
        candidate.SetCell(1, 2, bitmap.GetCell(0, 2));

        ReferenceEquals(candidate.GetCell(0, 1), bitmap.GetCell(0, 1)).ShouldBeTrue();
        admission.CreatesNewViolation(bitmap, candidate, 0, 1).ShouldBeTrue();
        admission.CreatesNewViolation(bitmap, candidate, 0, 2).ShouldBeTrue();
        admission.CreatesNewViolation(bitmap, candidate, 1, 2).ShouldBeFalse();
    }

    [Test]
    public void CreatesNewViolation_AnUnchangedPreExistingViolationIsTolerated_ButAnotherInvalidCellInItsPlaceIsNot()
    {
        var (bitmap, components) = Build(ClassicPlanWithLatesOfANonShiftWorker(locked: true));
        var admission = Admission(components);
        const int day = NonShiftWorkerLockedLateDay;
        var otherLateRow = Enumerable.Range(0, ClassicPlan.Length).First(r => bitmap.GetCell(r, day).Symbol == CellSymbol.Late);
        admission.CellValid(bitmap, NonShiftWorkerRow, day).ShouldBeFalse();

        admission.CreatesNewViolation(bitmap, BitmapCloner.Clone(bitmap), NonShiftWorkerRow, day).ShouldBeFalse();

        var candidate = BitmapCloner.Clone(bitmap);
        candidate.SetCell(NonShiftWorkerRow, day, bitmap.GetCell(otherLateRow, day));
        admission.CreatesNewViolation(bitmap, candidate, NonShiftWorkerRow, day).ShouldBeTrue();
    }

    [Test]
    public void CreatesNewViolation_OnARuleValidBasePlan_EqualsTheCellCheck()
    {
        var (bitmap, components) = Build();
        var admission = Admission(components);
        var region = new HarmonyRegion([0, 3, 5], 2, 6);
        var candidate = BitmapCloner.Clone(bitmap);
        for (var d = region.DayFrom; d <= region.DayTo; d++)
        {
            var cells = region.Rows.Select(r => bitmap.GetCell(r, d)).ToArray();
            for (var i = 0; i < region.Rows.Count; i++)
            {
                candidate.SetCell(region.Rows[i], d, cells[(i + d) % cells.Length]);
            }
        }

        var invalidCells = 0;
        foreach (var r in region.Rows)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                admission.CellValid(bitmap, r, d).ShouldBeTrue($"base r{r} d{d}");
                var valid = admission.CellValid(candidate, r, d);
                invalidCells += valid ? 0 : 1;
                admission.CreatesNewViolation(bitmap, candidate, r, d).ShouldBe(!valid, $"cell r{r} d{d}");
            }
        }

        invalidCells.ShouldBeGreaterThan(0);
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
