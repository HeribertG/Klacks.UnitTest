// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

[TestFixture]
public class BatchEvaluatorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Evaluate_FirstStepHitsLockedCell_ReturnsRejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 2);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: true));

        var evaluator = BuildEvaluator();
        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps: [new PlanCellSwap(0, 0, 1, 0, "test swap on locked cell")]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.Result.ShouldBe(BatchAcceptance.Rejected);
        result.AppliedSteps.Count.ShouldBe(0);
        result.Rejections.Count.ShouldBe(1);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.LockedCell);
        result.StoppedAtStep.ShouldBe(0);
    }

    [Test]
    public void Evaluate_CrossDaySwap_ReturnsRejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        var evaluator = BuildEvaluator();
        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps: [new PlanCellSwap(0, 0, 1, 1, "cross-day swap")]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.Result.ShouldBe(BatchAcceptance.Rejected);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void Evaluate_StoppedPrefixCarriesAcceptedSteps()
    {
        var bitmap = BuildBitmap(rows: 3, days: 2);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));
        bitmap.SetCell(1, 0, new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));
        bitmap.SetCell(2, 0, new Cell(CellSymbol.Other, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: true));

        var evaluator = BuildEvaluator();
        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps:
            [
                new PlanCellSwap(0, 0, 1, 0, "swap valid pair"),
                new PlanCellSwap(0, 0, 2, 0, "second step hits locked row"),
            ]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.StoppedAtStep.ShouldBe(1);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.LockedCell);
        if (result.Result == BatchAcceptance.PartiallyAccepted)
        {
            result.AppliedSteps.Count.ShouldBe(1);
        }
        else
        {
            result.Result.ShouldBe(BatchAcceptance.WouldDegrade);
            result.AppliedSteps.Count.ShouldBe(0);
        }
    }

    private static BatchEvaluator BuildEvaluator()
    {
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));
        var fitness = new HarmonyFitnessEvaluator(new HarmonyScorer());
        return new BatchEvaluator(validator, fitness);
    }

    private static HarmonyBitmap BuildBitmap(int rows, int days)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>()));
        }
        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }
}
