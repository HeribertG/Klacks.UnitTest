// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

/// <summary>
/// Rank of the stand-in tier in the Wizard 1 comparison: right after legality and coverage, before every hours or soft
/// stage, so a plan never hands a coverable slot to a placeholder, while a placeholder still beats an open slot.
/// </summary>
[TestFixture]
public class TokenFitnessEvaluatorStandInTests
{
    [Test]
    public void Compare_FewerStandInTokens_WinsAgainstBetterHoursAndSoftStages()
    {
        var realFirst = Plan(stage0: 0, standInTokens: 3, stage1: 0.2, stage2: 0.1);
        var standInHeavy = Plan(stage0: 0, standInTokens: 4, stage1: 1.0, stage2: 1.0);

        Evaluator().Compare(realFirst, standInHeavy).ShouldBeLessThan(0);
    }

    [Test]
    public void Compare_StandInOnASlot_WinsAgainstAnOpenSlot()
    {
        var filledByStandIn = Plan(stage0: 0, standInTokens: 4, stage1: 0.2, stage2: 0.1);
        var openSlot = Plan(stage0: 1, standInTokens: 3, stage1: 1.0, stage2: 1.0);

        Evaluator().Compare(filledByStandIn, openSlot).ShouldBeLessThan(0);
    }

    [Test]
    public void Evaluate_CountsOnlyTheTokensOfStandIns()
    {
        var real = Agent("real");
        var standIn = Agent("stand-in") with { IsStandIn = true };
        var context = new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 12, 10),
            PeriodUntil = new DateOnly(2026, 12, 10),
            Agents = [real, standIn],
        };
        var plan = new CoreScenario { Tokens = [Token("real"), Token("stand-in"), Token("stand-in")] };

        TokenFitnessEvaluator.Create(context).Evaluate(plan, context);

        plan.FitnessStandInTokens.ShouldBe(2);
    }

    private static TokenFitnessEvaluator Evaluator() => new(new TokenConstraintChecker(), new Dictionary<string, double>(), []);

    private static CoreScenario Plan(int stage0, int standInTokens, double stage1, double stage2) => new()
    {
        FitnessStage0 = stage0,
        FitnessStandInTokens = standInTokens,
        FitnessStage1 = stage1,
        FitnessStage2 = stage2,
    };

    private static CoreAgent Agent(string id) => new(id, 0, 0, 6, 11, 0.5, 10, 50, 2);

    private static CoreToken Token(string agentId)
    {
        var date = new DateOnly(2026, 12, 10);
        return new CoreToken(
            [Guid.NewGuid().ToString()],
            0,
            date,
            8m,
            date.ToDateTime(new TimeOnly(8, 0)),
            date.ToDateTime(new TimeOnly(16, 0)),
            Guid.NewGuid(),
            0,
            false,
            null,
            Guid.NewGuid(),
            agentId);
    }
}
