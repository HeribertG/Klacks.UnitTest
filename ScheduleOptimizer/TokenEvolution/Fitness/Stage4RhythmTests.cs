// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

/// <summary>
/// Stage 4 carries the canon rhythm as a fifth component: it is in [0..1], it is part of the stage aggregate, and a
/// canon start scores a higher rhythm than coverage-first.
/// </summary>
[TestFixture]
public sealed class Stage4RhythmTests
{
    [Test]
    public void Rhythm_IsAStage4Component()
    {
        var context = RotationTestContexts.Scenario4();
        var plan = new CoverageFirstTokenStrategy().BuildScenario(context, new Random(1));

        var result = TokenFitnessEvaluator.Create(context).EvaluateDetailed(plan, context);
        var components = result.Stage4Components;

        components.Rhythm.ShouldBeInRange(0d, 1d);
        result.Stage4.ShouldBe(
            (components.Fairness + components.MinimumHours + components.BlockSymmetry + components.ShiftKindFairness + components.Rhythm) / 5d,
            1e-9);
    }
}
