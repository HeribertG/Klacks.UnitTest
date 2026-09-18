// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnEvalBaselineTests
{
    [Test]
    public void ComputeMedian_OddCount_ReturnsTheMiddleValueRegardlessOfOrder()
    {
        var median = TurnEvalBaseline.ComputeMedian([0.5541m, 0.5149m, 0.5520m]);

        median.ShouldBe(0.5520m);
    }

    [Test]
    public void ComputeMedian_EvenCount_ReturnsTheMeanOfTheTwoMiddleValues()
    {
        var median = TurnEvalBaseline.ComputeMedian([0.90m, 0.50m, 0.60m, 0.10m]);

        median.ShouldBe(0.55m);
    }

    [Test]
    public void ComputeMedian_OneOutlier_DoesNotMoveTheBaselineTowardsTheMaximum()
    {
        var median = TurnEvalBaseline.ComputeMedian([0.51m, 0.52m, 0.53m, 0.99m, 0.50m]);

        median.ShouldBe(0.52m);
    }

    [Test]
    public void ComputeMedian_BelowTheMinimumRunCount_ReturnsNull()
    {
        var composites = Enumerable.Repeat(0.5m, TurnEvalDefaults.MinBaselineRuns - 1).ToList();

        TurnEvalBaseline.ComputeMedian(composites).ShouldBeNull();
    }

    [Test]
    public void ComputeMedian_ExactlyTheMinimumRunCount_ReturnsAMedian()
    {
        var composites = Enumerable.Repeat(0.5m, TurnEvalDefaults.MinBaselineRuns).ToList();

        TurnEvalBaseline.ComputeMedian(composites).ShouldBe(0.5m);
    }
}
