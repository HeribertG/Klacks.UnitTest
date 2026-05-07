// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Loop;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

[TestFixture]
public class RejectMemoryTests
{
    [Test]
    public void Note_AcceptedBatch_DoesNotEnterMemory()
    {
        var memory = new RejectMemory(capacity: 3);

        memory.Note(BuildEvaluation(BatchAcceptance.Accepted, "consolidate_block"));

        memory.Entries.Count.ShouldBe(0);
    }

    [Test]
    public void Note_RejectedBatches_StoredInOrder()
    {
        var memory = new RejectMemory(capacity: 3);

        memory.Note(BuildEvaluation(BatchAcceptance.Rejected, "consolidate_block"));
        memory.Note(BuildEvaluation(BatchAcceptance.WouldDegrade, "consolidate_block"));

        memory.Entries.Count.ShouldBe(2);
        memory.Entries.Last().Result.ShouldBe(BatchAcceptance.WouldDegrade);
    }

    [Test]
    public void Note_BeyondCapacity_DropsOldest()
    {
        var memory = new RejectMemory(capacity: 2);

        memory.Note(BuildEvaluation(BatchAcceptance.Rejected, "first"));
        memory.Note(BuildEvaluation(BatchAcceptance.Rejected, "second"));
        memory.Note(BuildEvaluation(BatchAcceptance.Rejected, "third"));

        memory.Entries.Count.ShouldBe(2);
        memory.Entries.First().Intent.ShouldBe("second");
        memory.Entries.Last().Intent.ShouldBe("third");
    }

    [Test]
    public void Note_WouldDegrade_BuildsScoreSummary()
    {
        var memory = new RejectMemory();

        memory.Note(new BatchEvaluation(
            Guid.NewGuid(),
            "consolidate_block",
            BatchAcceptance.WouldDegrade,
            AppliedSteps: [],
            Rejections: [],
            StoppedAtStep: null,
            ScoreBefore: 0.7,
            ScoreAfter: 0.65));

        memory.Entries.Single().Summary.ShouldContain("0.650");
        memory.Entries.Single().Summary.ShouldContain("0.700");
    }

    [Test]
    public void Note_RejectedBatch_StoresRejectedSwapCoordinates()
    {
        var memory = new RejectMemory();
        var swap = new PlanCellSwap(2, 5, 7, 5, "ignored");
        var rejection = new PlanMutationRejection(swap, PlanMutationRejectionReason.HardConstraintViolation, "max consecutive");

        memory.Note(new BatchEvaluation(
            Guid.NewGuid(),
            "consolidate_block",
            BatchAcceptance.Rejected,
            AppliedSteps: [],
            Rejections: [rejection],
            StoppedAtStep: 0,
            ScoreBefore: 0.5,
            ScoreAfter: 0.5));

        var entry = memory.Entries.Single();
        entry.RejectedSwaps.Count.ShouldBe(1);
        entry.RejectedSwaps[0].RowA.ShouldBe(2);
        entry.RejectedSwaps[0].RowB.ShouldBe(7);
        entry.RejectedSwaps[0].DayA.ShouldBe(5);
    }

    [Test]
    public void Note_WouldDegrade_StoresAllAppliedStepsAsForbidden()
    {
        var memory = new RejectMemory();
        var s0 = new PlanCellSwap(1, 3, 4, 3, "");
        var s1 = new PlanCellSwap(2, 3, 5, 3, "");

        memory.Note(new BatchEvaluation(
            Guid.NewGuid(),
            "consolidate_block",
            BatchAcceptance.WouldDegrade,
            AppliedSteps: [s0, s1],
            Rejections: [],
            StoppedAtStep: null,
            ScoreBefore: 0.5,
            ScoreAfter: 0.45));

        var entry = memory.Entries.Single();
        entry.RejectedSwaps.Count.ShouldBe(2);
    }

    [Test]
    public void ForbiddenSwapKeys_DeduplicatesAcrossEntries_OrderInvariant()
    {
        var memory = new RejectMemory();
        var swapForward = new PlanCellSwap(2, 5, 7, 5, "");
        var swapReversed = new PlanCellSwap(7, 5, 2, 5, "");
        var swapDifferent = new PlanCellSwap(3, 5, 8, 5, "");

        memory.Note(new BatchEvaluation(
            Guid.NewGuid(), "consolidate_block", BatchAcceptance.Rejected,
            AppliedSteps: [],
            Rejections: [new PlanMutationRejection(swapForward, PlanMutationRejectionReason.HardConstraintViolation, "")],
            StoppedAtStep: 0, ScoreBefore: 0.5, ScoreAfter: 0.5));
        memory.Note(new BatchEvaluation(
            Guid.NewGuid(), "consolidate_block", BatchAcceptance.Rejected,
            AppliedSteps: [],
            Rejections: [new PlanMutationRejection(swapReversed, PlanMutationRejectionReason.HardConstraintViolation, "")],
            StoppedAtStep: 0, ScoreBefore: 0.5, ScoreAfter: 0.5));
        memory.Note(new BatchEvaluation(
            Guid.NewGuid(), "consolidate_block", BatchAcceptance.Rejected,
            AppliedSteps: [],
            Rejections: [new PlanMutationRejection(swapDifferent, PlanMutationRejectionReason.HardConstraintViolation, "")],
            StoppedAtStep: 0, ScoreBefore: 0.5, ScoreAfter: 0.5));

        var keys = memory.ForbiddenSwapKeys();
        keys.Count.ShouldBe(2);
        keys[0].ShouldBe(new ForbiddenSwapKey(2, 7, 5));
        keys[1].ShouldBe(new ForbiddenSwapKey(3, 8, 5));
    }

    [Test]
    public void DefaultCapacity_IsTen()
    {
        var memory = new RejectMemory();
        for (var i = 0; i < 12; i++)
        {
            memory.Note(BuildEvaluation(BatchAcceptance.Rejected, "intent-" + i));
        }
        memory.Entries.Count.ShouldBe(10);
        memory.Entries.First().Intent.ShouldBe("intent-2");
        memory.Entries.Last().Intent.ShouldBe("intent-11");
    }

    private static BatchEvaluation BuildEvaluation(BatchAcceptance result, string intent) => new(
        Guid.NewGuid(),
        intent,
        result,
        AppliedSteps: [],
        Rejections: [],
        StoppedAtStep: null,
        ScoreBefore: 0.5,
        ScoreAfter: 0.5);
}
