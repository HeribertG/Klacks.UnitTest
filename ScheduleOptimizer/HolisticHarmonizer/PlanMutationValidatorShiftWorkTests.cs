// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Stage 3 respects the shift-work flag on both of its paths: a cross-day swap and an LLM batch may not hand a late
/// duty to an agent without shift work. In a batch the offending step stops the batch, the valid prefix stays eligible.
/// </summary>
[TestFixture]
public class PlanMutationValidatorShiftWorkTests
{
    private const string ShiftWorkReasonFragment = "does not perform shift work";
    private const string Intent = "redistribute_load";
    private const int ShiftWorkerRow = 0;
    private const int EarlyOnlyRow = 1;
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void CrossDay_LateToEarlyOnlyAgent_Rejected()
    {
        var bitmap = BuildBitmap();
        bitmap.SetCell(ShiftWorkerRow, 0, Work(CellSymbol.Late, 0, 15, 23));
        bitmap.SetCell(EarlyOnlyRow, 1, Work(CellSymbol.Early, 1, 7, 15));
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(ShiftWorkerRow, 0, EarlyOnlyRow, 1, Intent));

        rejection.ShouldNotBeNull();
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(ShiftWorkReasonFragment);
    }

    [Test]
    public void CrossDay_EarlyToEarlyOnlyAgent_PassesTheShiftWorkCheck()
    {
        var bitmap = BuildBitmap();
        bitmap.SetCell(ShiftWorkerRow, 0, Work(CellSymbol.Early, 0, 7, 15));
        bitmap.SetCell(EarlyOnlyRow, 1, Work(CellSymbol.Late, 1, 15, 23));
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(ShiftWorkerRow, 0, EarlyOnlyRow, 1, Intent));

        (rejection?.Detail ?? string.Empty).ShouldNotContain(ShiftWorkReasonFragment);
        rejection.ShouldBeNull();
    }

    [Test]
    public void SameDay_LateToEarlyOnlyAgent_Rejected()
    {
        var bitmap = BuildBitmap();
        bitmap.SetCell(ShiftWorkerRow, 1, Work(CellSymbol.Late, 1, 15, 23));
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(ShiftWorkerRow, 1, EarlyOnlyRow, 1, Intent));

        rejection.ShouldNotBeNull();
        rejection.Detail.ShouldContain(ShiftWorkReasonFragment);
    }

    [Test]
    public void Batch_StopsAtTheStepGivingALateToAnEarlyOnlyAgent()
    {
        var bitmap = BuildBitmap();
        bitmap.SetCell(ShiftWorkerRow, 0, Work(CellSymbol.Early, 0, 7, 15));
        bitmap.SetCell(ShiftWorkerRow, 1, Work(CellSymbol.Late, 1, 15, 23));
        var evaluator = new BatchEvaluator(
            new PlanMutationValidator(new DomainAwareReplaceValidator(null)),
            new HarmonyFitnessEvaluator(new HarmonyScorer()));
        var batch = new MutationBatch(
            Guid.NewGuid(),
            Intent,
            LlmIteration: 0,
            Steps:
            [
                new PlanCellSwap(ShiftWorkerRow, 0, EarlyOnlyRow, 0, "early to the early-only agent"),
                new PlanCellSwap(ShiftWorkerRow, 1, EarlyOnlyRow, 1, "late to the early-only agent"),
            ]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.StoppedAtStep.ShouldBe(1);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        result.Rejections.Single().Detail.ShouldContain(ShiftWorkReasonFragment);
        result.AppliedSteps.ShouldNotContain(step => step.DayA == 1);
        bitmap.GetCell(EarlyOnlyRow, 1).Symbol.ShouldNotBe(CellSymbol.Late);
    }

    private static Cell Work(CellSymbol symbol, int day, int startHour, int endHour)
    {
        var date = Day0.AddDays(day);
        return new Cell(symbol, Guid.NewGuid(), [Guid.NewGuid()], false,
            date.ToDateTime(new TimeOnly(startHour, 0)), date.ToDateTime(new TimeOnly(endHour, 0)), 8m);
    }

    private static HarmonyBitmap BuildBitmap()
    {
        const int days = 3;
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Agent 0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "Agent 1", 100m, new HashSet<CellSymbol>(), PerformsShiftWork: false),
        };

        return BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []));
    }
}
