// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Candidates;

[TestFixture]
public class CandidateStepFilterTests
{
    [Test]
    public void FilterToCandidates_EmptyList_ReturnsBatchUnchanged()
    {
        var batch = MakeBatch(new PlanCellSwap(0, 0, 1, 0, "test"));
        var result = CandidateStepFilter.FilterToCandidates(batch, Array.Empty<MoveCandidate>());

        result.ShouldBeSameAs(batch);
    }

    [Test]
    public void FilterToCandidates_AllStepsInList_ReturnsBatchUnchanged()
    {
        var batch = MakeBatch(new PlanCellSwap(0, 0, 1, 0, "test"));
        var candidates = new[] { new MoveCandidate(0, 0, 1, 0, "hint", 1.0) };

        var result = CandidateStepFilter.FilterToCandidates(batch, candidates);

        result.ShouldBeSameAs(batch);
    }

    [Test]
    public void FilterToCandidates_StepNotInList_IsDropped()
    {
        var batch = MakeBatch(
            new PlanCellSwap(0, 0, 1, 0, "in-list"),
            new PlanCellSwap(2, 5, 3, 5, "self-proposed"));
        var candidates = new[] { new MoveCandidate(0, 0, 1, 0, "hint", 1.0) };

        var result = CandidateStepFilter.FilterToCandidates(batch, candidates);

        result.Steps.Count.ShouldBe(1);
        result.Steps[0].RowA.ShouldBe(0);
        result.Steps[0].DayA.ShouldBe(0);
        result.BatchId.ShouldBe(batch.BatchId);
        result.Intent.ShouldBe(batch.Intent);
    }

    [Test]
    public void FilterToCandidates_MirroredCoordinatesMatch()
    {
        // LLM emits the swap with rowA/rowB transposed against the candidate.
        var batch = MakeBatch(new PlanCellSwap(1, 0, 0, 0, "mirrored"));
        var candidates = new[] { new MoveCandidate(0, 0, 1, 0, "hint", 1.0) };

        var result = CandidateStepFilter.FilterToCandidates(batch, candidates);

        result.Steps.Count.ShouldBe(1);
    }

    [Test]
    public void FilterToCandidates_AllStepsSelfProposed_ReturnsEmptyBatch()
    {
        var batch = MakeBatch(new PlanCellSwap(2, 5, 3, 5, "self-proposed"));
        var candidates = new[] { new MoveCandidate(0, 0, 1, 0, "hint", 1.0) };

        var result = CandidateStepFilter.FilterToCandidates(batch, candidates);

        result.Steps.ShouldBeEmpty();
        result.BatchId.ShouldBe(batch.BatchId);
    }

    [Test]
    public void CountStepsInCandidates_CountsBothOrientations()
    {
        var batch = MakeBatch(
            new PlanCellSwap(0, 0, 1, 0, "direct"),
            new PlanCellSwap(3, 5, 2, 5, "mirror-of-c2"),
            new PlanCellSwap(9, 9, 8, 8, "neither"));
        var candidates = new[]
        {
            new MoveCandidate(0, 0, 1, 0, "c1", 1.0),
            new MoveCandidate(2, 5, 3, 5, "c2", 1.0),
        };

        var result = CandidateStepFilter.CountStepsInCandidates(batch, candidates);

        result.ShouldBe(2);
    }

    [Test]
    public void CountStepsInCandidates_EmptyList_ReturnsZero()
    {
        var batch = MakeBatch(new PlanCellSwap(0, 0, 1, 0, "test"));
        CandidateStepFilter.CountStepsInCandidates(batch, Array.Empty<MoveCandidate>()).ShouldBe(0);
    }

    private static MutationBatch MakeBatch(params PlanCellSwap[] steps)
        => new(Guid.NewGuid(), HolisticIntent.ConsolidateBlock, LlmIteration: 0, steps);
}
