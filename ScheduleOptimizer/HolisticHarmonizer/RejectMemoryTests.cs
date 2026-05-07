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
