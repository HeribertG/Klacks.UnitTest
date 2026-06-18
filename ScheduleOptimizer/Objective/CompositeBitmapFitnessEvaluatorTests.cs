// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.Objective;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Objective;

[TestFixture]
public class CompositeBitmapFitnessEvaluatorTests
{
    private static readonly DateOnly D1 = new(2026, 4, 20);
    private static readonly DateOnly D2 = new(2026, 4, 21);
    private static readonly CompositeObjective Objective = new();

    private static CoreAgent Agent(string id, double guaranteed) => new(
        Id: id, CurrentHours: 0, GuaranteedHours: guaranteed, MaxConsecutiveDays: 6,
        MinRestHours: 11, Motivation: 0.5, MaxDailyHours: 10, MaxWeeklyHours: 50, MaxOptimalGap: 2);

    private static CoreShift Shift(Guid id, DateOnly day, int cap) =>
        new(id.ToString(), "Shift", day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, cap, 0);

    private static BitmapAgent Row(string id) => new(id, id, 8m, new HashSet<CellSymbol>());

    private static Cell Work(DateOnly day, Guid shiftId, decimal hours) => new(
        CellSymbol.Early, shiftId, [], false,
        day.ToDateTime(new TimeOnly(8, 0)),
        day.ToDateTime(new TimeOnly(8, 0)).AddHours((double)hours),
        hours);

    private static HarmonyBitmap Bitmap(IReadOnlyList<string> agentIds, IReadOnlyList<DateOnly> days, Action<Cell[,]> fill)
    {
        var cells = new Cell[agentIds.Count, days.Count];
        for (var r = 0; r < agentIds.Count; r++)
        {
            for (var d = 0; d < days.Count; d++)
            {
                cells[r, d] = Cell.Free();
            }
        }

        fill(cells);
        return new HarmonyBitmap(agentIds.Select(Row).ToList(), days, cells);
    }

    private static CompositeBitmapFitnessEvaluator EvaluatorFor(CoreWizardContext context, HarmonyBitmap seed)
    {
        var baseline = Objective.Evaluate(ObjectiveInputBuilder.FromBitmap(seed, context));
        return new CompositeBitmapFitnessEvaluator(Objective, context, baseline);
    }

    [Test]
    public void Evaluate_TheSnapshotItself_ScoresItsCompositeScalar()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = D1, PeriodUntil = D1,
            Agents = [Agent("A", 8)],
            Shifts = [Shift(shift, D1, cap: 1)],
        };
        var seed = Bitmap(["A"], [D1], c => c[0, 0] = Work(D1, shift, 8));

        var baseline = Objective.Evaluate(ObjectiveInputBuilder.FromBitmap(seed, context));
        var evaluator = new CompositeBitmapFitnessEvaluator(Objective, context, baseline);

        var fitness = evaluator.Evaluate(seed);

        fitness.Fitness.ShouldBe(baseline.Scalar, 1e-9);
        fitness.Fitness.ShouldBeGreaterThanOrEqualTo(0.0);
        fitness.RowScores.Count.ShouldBe(seed.RowCount);
    }

    [Test]
    public void Evaluate_CandidateThatClosesUnderSupply_ScoresHigherThanTheSnapshot()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = D1, PeriodUntil = D1,
            Agents = [Agent("A", 8), Agent("B", 8)],
            Shifts = [Shift(shift, D1, cap: 2)],
        };
        // Snapshot covers only 1 of 2 -> one UnderSupply.
        var seed = Bitmap(["A", "B"], [D1], c => c[0, 0] = Work(D1, shift, 8));
        var evaluator = EvaluatorFor(context, seed);

        // Candidate covers both slots -> fewer UnderSupply, both agents on target.
        var candidate = Bitmap(["A", "B"], [D1], c =>
        {
            c[0, 0] = Work(D1, shift, 8);
            c[1, 0] = Work(D1, shift, 8);
        });

        var baselineFitness = evaluator.Evaluate(seed).Fitness;
        var candidateFitness = evaluator.Evaluate(candidate).Fitness;

        candidateFitness.ShouldBeGreaterThan(baselineFitness);
        candidateFitness.ShouldBeGreaterThanOrEqualTo(0.0);
    }

    [Test]
    public void Evaluate_CandidateThatIntroducesALegalityViolation_ScoresNegative()
    {
        var shift = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = D1, PeriodUntil = D1,
            Agents = [Agent("A", 8)],
            Shifts = [Shift(shift, D1, cap: 1)],
        };
        var seed = Bitmap(["A"], [D1], c => c[0, 0] = Work(D1, shift, 8));
        var evaluator = EvaluatorFor(context, seed);

        // 12h on a single day exceeds the 10h daily cap -> MaxDailyHours (legality) regression.
        var candidate = Bitmap(["A"], [D1], c => c[0, 0] = Work(D1, shift, 12));

        var fitness = evaluator.Evaluate(candidate).Fitness;

        fitness.ShouldBeLessThan(0.0);
        fitness.ShouldBeLessThan(evaluator.Evaluate(seed).Fitness);
    }

    [Test]
    public void Evaluate_CandidateThatRegressesTheWorstAgent_ScoresNegative_EvenWithAnIdenticalGate()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = D1, PeriodUntil = D2,
            Agents = [Agent("A", 8), Agent("B", 8)],
            Shifts = [Shift(s1, D1, cap: 1), Shift(s2, D2, cap: 1)],
        };
        // Snapshot: A and B each at their 8h target, both slots covered.
        var seed = Bitmap(["A", "B"], [D1, D2], c =>
        {
            c[0, 0] = Work(D1, s1, 8);
            c[1, 1] = Work(D2, s2, 8);
        });
        var evaluator = EvaluatorFor(context, seed);

        // Candidate: A takes both shifts (16h, over target), B gets nothing (0h, under target).
        // Coverage and legality are identical to the snapshot -> only the worst-agent hours floor breaks.
        var candidate = Bitmap(["A", "B"], [D1, D2], c =>
        {
            c[0, 0] = Work(D1, s1, 8);
            c[0, 1] = Work(D2, s2, 8);
        });

        var seedResult = Objective.Evaluate(ObjectiveInputBuilder.FromBitmap(seed, context));
        var candidateResult = Objective.Evaluate(ObjectiveInputBuilder.FromBitmap(candidate, context));
        candidateResult.Gate.ShouldBe(seedResult.Gate);                       // gate truly identical
        candidateResult.Diagnostics.WorstStundenabgleich
            .ShouldBeLessThan(seedResult.Diagnostics.WorstStundenabgleich);    // worst agent regressed

        evaluator.Evaluate(candidate).Fitness.ShouldBeLessThan(0.0);
    }
}
