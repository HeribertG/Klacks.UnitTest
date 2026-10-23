// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// The rotation start follows the canon wherever the rules allow it: it stays legal, covers as many slots as
/// coverage-first, and its plan has a clearly stronger rhythm between the agents.
/// </summary>
[TestFixture]
public sealed class RotationTemplateTokenStrategyTests
{
    [Test]
    public void Scenario4_IsLegalCoversLikeCoverageFirstAndHasMoreRhythm()
    {
        var context = RotationTestContexts.Scenario4();
        var evaluator = TokenFitnessEvaluator.Create(context);

        var rotation = new RotationTemplateTokenStrategy().BuildScenario(context, new Random(1));
        var coverage = new CoverageFirstTokenStrategy().BuildScenario(context, new Random(1));
        var rotationResult = evaluator.EvaluateDetailed(rotation, context);
        var coverageResult = evaluator.EvaluateDetailed(coverage, context);

        rotationResult.Stage0.ShouldBe(0);
        rotation.Tokens.Count.ShouldBeGreaterThanOrEqualTo(coverage.Tokens.Count);
        rotationResult.Stage4Components.Rhythm.ShouldBeGreaterThan(coverageResult.Stage4Components.Rhythm);
    }

    [Test]
    public void Vacation_IsRespected()
    {
        var context = RotationTestContexts.Scenario4(vacation: true);

        var plan = new RotationTemplateTokenStrategy().BuildScenario(context, new Random(1));

        plan.Tokens.Where(t => t.AgentId == "MA03")
            .ShouldAllBe(t => t.Date < RotationTestContexts.From.AddDays(7) || t.Date > RotationTestContexts.From.AddDays(20));
        TokenFitnessEvaluator.Create(context).EvaluateDetailed(plan, context).Stage0.ShouldBe(0);
    }

    [Test]
    public void TheStrategyIsDeterministic()
    {
        var context = RotationTestContexts.Scenario4();

        var first = new RotationTemplateTokenStrategy().BuildScenario(context, new Random(1));
        var second = new RotationTemplateTokenStrategy().BuildScenario(context, new Random(99));

        first.Tokens.Select(t => (t.AgentId, t.Date, t.ShiftTypeIndex))
            .ShouldBe(second.Tokens.Select(t => (t.AgentId, t.Date, t.ShiftTypeIndex)));
    }

    [Test]
    public void AppliesTo_FullTeamWhoseCanonStaffsEverySeat()
    {
        new RotationTemplateTokenStrategy().AppliesTo(RotationTestContexts.Scenario4()).ShouldBeTrue();
    }

    [Test]
    public void AppliesTo_NotASmallTeamWhoseCanonLeavesSeatsOpen()
    {
        var context = RotationTestContexts.SmallTeam();

        RotationTemplate.Build(context)!.CoverageFit.ShouldBeLessThan(RotationTemplateTokenStrategy.MinCoverageFit);
        new RotationTemplateTokenStrategy().AppliesTo(context).ShouldBeFalse();
    }
}
