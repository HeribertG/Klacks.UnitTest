// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee;
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
    public void Evaluate_CrossDaySwapWithCoverageMismatch_ReturnsHardConstraintViolation()
    {
        // rowA day0 has work, rowB day1 is free (default Free) → coverage would change → reject.
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));

        var evaluator = BuildEvaluator();
        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps: [new PlanCellSwap(0, 0, 1, 1, "cross-day work↔free")]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.Result.ShouldBe(BatchAcceptance.Rejected);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        result.Rejections.Single().Detail.ShouldContain("coverage");
    }

    [Test]
    public void Evaluate_CrossDaySwapCoverageNeutralBothFree_ReturnsNoEffect()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        var evaluator = BuildEvaluator();
        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps: [new PlanCellSwap(0, 0, 1, 1, "cross-day free↔free")]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.Result.ShouldBe(BatchAcceptance.Rejected);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.NoEffect);
    }

    [Test]
    public void Evaluate_CrossDaySwapDifferentWorkSymbols_PassesValidator()
    {
        // rowA day0 = Early, rowB day1 = Late — both work, different shift types.
        // Hard validator now admits cross-day; committee may still veto, but the rejection
        // reason in this minimal setup should NOT be HardConstraintViolation or NoEffect.
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));
        bitmap.SetCell(1, 1, new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));

        var evaluator = BuildEvaluator();
        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps: [new PlanCellSwap(0, 0, 1, 1, "cross-day work↔work")]);

        var result = evaluator.Evaluate(bitmap, batch);

        // No hard rejection. Result is either Accepted, PartiallyAccepted, or WouldDegrade
        // depending on the score. Either way, no rejection records with HardConstraintViolation.
        result.Rejections.Where(r => r.Reason == PlanMutationRejectionReason.HardConstraintViolation).ShouldBeEmpty();
        result.Rejections.Where(r => r.Reason == PlanMutationRejectionReason.NoEffect).ShouldBeEmpty();
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

    [Test]
    public void Evaluate_CommitteeVetoesHardValidStep_RejectsWithCommitteeVetoReason()
    {
        var bitmap = BuildBitmap(rows: 2, days: 2);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));
        bitmap.SetCell(1, 0, new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: false));

        var alwaysVetoCommittee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new AlwaysVetoAgent("Stub-A", "first reason"),
            new AlwaysVetoAgent("Stub-B", "second reason"),
        });
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));
        var fitness = new HarmonyFitnessEvaluator(new HarmonyScorer());
        var evaluator = new BatchEvaluator(validator, fitness, alwaysVetoCommittee);

        var batch = new MutationBatch(
            Guid.NewGuid(),
            "consolidate_block",
            LlmIteration: 0,
            Steps: [new PlanCellSwap(0, 0, 1, 0, "valid hard, but committee blocks")]);

        var result = evaluator.Evaluate(bitmap, batch);

        result.Result.ShouldBe(BatchAcceptance.Rejected);
        result.AppliedSteps.Count.ShouldBe(0);
        result.Rejections.Count.ShouldBe(1);
        result.Rejections.Single().Reason.ShouldBe(PlanMutationRejectionReason.CommitteeVeto);
        result.Rejections.Single().Detail.ShouldContain("Stub-A");
        result.Rejections.Single().Detail.ShouldContain("Stub-B");
    }

    private static BatchEvaluator BuildEvaluator()
    {
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));
        var fitness = new HarmonyFitnessEvaluator(new HarmonyScorer());
        return new BatchEvaluator(validator, fitness);
    }

    private sealed class AlwaysVetoAgent : IConstraintAgent
    {
        private readonly string _reason;
        public AlwaysVetoAgent(string name, string reason)
        {
            Name = name;
            _reason = reason;
        }
        public string Name { get; }
        public ConstraintAgentVerdict Evaluate(HarmonyBitmap before, PlanCellSwap swap)
            => new(Name, ConstraintAgentVote.Veto, _reason);
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
