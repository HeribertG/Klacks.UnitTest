// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Keywords;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords.KeywordWishTestData;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

/// <summary>
/// The keyword-wish tier of the Wizard 1 fitness: ranked after the guaranteed hours (Stage 1) and before the target hours
/// (Stage 2), always below legality and coverage. Positive wishes attract in Hard mode too.
/// </summary>
[TestFixture]
public class KeywordWishFitnessTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static CoreScenario Scenario(double stage0Legality, int stage0, double stage1, double wishes, double stage2)
        => new()
        {
            FitnessStage0Legality = (int)stage0Legality,
            FitnessStage0 = stage0,
            FitnessStage1 = stage1,
            FitnessKeywordWishes = wishes,
            FitnessStage2 = stage2,
        };

    private static TokenFitnessEvaluator Evaluator(CoreWizardContext context) => TokenFitnessEvaluator.Create(context);

    [Test]
    public void Comparer_WishesRankAfterStage1()
    {
        var betterHours = Scenario(0, 0, stage1: 1.0, wishes: 100, stage2: 0);
        var fewerWishes = Scenario(0, 0, stage1: 0.5, wishes: 0, stage2: 0);

        Evaluator(Context([], [], [], KeywordMode.Soft)).Compare(betterHours, fewerWishes).ShouldBeLessThan(0);
    }

    [Test]
    public void Comparer_WishesRankBeforeStage2()
    {
        var fewerWishes = Scenario(0, 0, stage1: 1.0, wishes: 1, stage2: 0.1);
        var betterTarget = Scenario(0, 0, stage1: 1.0, wishes: 3, stage2: 0.9);

        Evaluator(Context([], [], [], KeywordMode.Soft)).Compare(fewerWishes, betterTarget).ShouldBeLessThan(0);
    }

    [Test]
    public void Comparer_CoverageRanksAboveWishes()
    {
        var covered = Scenario(0, stage0: 0, stage1: 1.0, wishes: 100, stage2: 0);
        var openSlot = Scenario(0, stage0: 1, stage1: 1.0, wishes: 0, stage2: 0);

        Evaluator(Context([], [], [], KeywordMode.Soft)).Compare(covered, openSlot).ShouldBeLessThan(0);
    }

    [Test]
    public void SoftMode_CoveringTheOnlySlotOnAFreeDay_BeatsLeavingItOpen_AndCountsTheBrokenFree()
    {
        var context = Context(
            [Agent("A")],
            [Slot(ShiftId, Monday, Early)],
            [new CoreScheduleCommand("A", Monday, ScheduleCommandKeyword.Free)],
            KeywordMode.Soft);
        var evaluator = Evaluator(context);
        var covered = new CoreScenario { Tokens = [Token("A", ShiftId, Monday, Early)] };
        var open = new CoreScenario { Tokens = [] };

        evaluator.Evaluate(covered, context);
        evaluator.Evaluate(open, context);

        evaluator.Compare(covered, open).ShouldBeLessThan(0, "an open slot is a coverage defect, a broken FREE only a wish");
        covered.FitnessKeywordWishes.ShouldBe(TokenEvolutionConfig.DefaultKeywordWishFreeWeight);
        covered.FitnessStage0Legality.ShouldBe(0, "in Soft mode the broken FREE is no legality violation");
        WizardKeywordWishes.For(context).Evaluate(covered.Tokens, collectItems: true).Tally.BrokenFree.ShouldBe(1);
    }

    [Test]
    public void HardMode_OnlyNightAgentHoldingTheNight_BeatsTheSameNightOnAnotherAgent()
    {
        var context = Context(
            [Agent("A"), Agent("B")],
            [Slot(ShiftId, Monday, Night)],
            [new CoreScheduleCommand("A", Monday, ScheduleCommandKeyword.OnlyNight)],
            KeywordMode.Hard);
        var evaluator = Evaluator(context);
        var wished = new CoreScenario { Tokens = [Token("A", ShiftId, Monday, Night)] };
        var other = new CoreScenario { Tokens = [Token("B", ShiftId, Monday, Night)] };

        evaluator.Evaluate(wished, context);
        evaluator.Evaluate(other, context);

        wished.FitnessKeywordWishes.ShouldBe(0);
        other.FitnessKeywordWishes.ShouldBe(TokenEvolutionConfig.DefaultKeywordWishUnfulfilledWeight);
        evaluator.Compare(wished, other).ShouldBeLessThan(0);
    }

    [Test]
    public void HardMode_NotFreeDayWithoutShift_IsUnfulfilled()
    {
        var context = Context(
            [Agent("A"), Agent("B")],
            [Slot(ShiftId, Monday, Late)],
            [new CoreScheduleCommand("A", Monday, ScheduleCommandKeyword.NotFree)],
            KeywordMode.Hard);
        var evaluator = Evaluator(context);
        var wished = new CoreScenario { Tokens = [Token("A", ShiftId, Monday, Late)] };
        var other = new CoreScenario { Tokens = [Token("B", ShiftId, Monday, Late)] };

        evaluator.Evaluate(wished, context);
        evaluator.Evaluate(other, context);

        evaluator.Compare(wished, other).ShouldBeLessThan(0);
    }

    [Test]
    public void NoScheduleCommands_LeavesTheLegacyAggregateUnchanged()
    {
        var context = Context([Agent("A")], [Slot(ShiftId, Monday, Early)], [], KeywordMode.Hard);
        var scenario = new CoreScenario { Tokens = [Token("A", ShiftId, Monday, Early)] };

        Evaluator(context).Evaluate(scenario, context);

        scenario.FitnessKeywordWishes.ShouldBe(0);
        scenario.Fitness.ShouldBe(
            scenario.FitnessStage1 + (scenario.FitnessStage2 * 0.1) + (scenario.FitnessStage3 * 0.01) + (scenario.FitnessStage4 * 0.001),
            1e-12);
    }

    [Test]
    public void HardMode_EvolutionGivesTheNightsToTheEmployeeWhoAskedForThem()
    {
        var shifts = new List<CoreShift>();
        var commands = new List<CoreScheduleCommand>();
        for (var day = 0; day < 3; day++)
        {
            var date = Monday.AddDays(day);
            shifts.Add(Slot(Guid.NewGuid(), date, Night));
            commands.Add(new CoreScheduleCommand("A", date, ScheduleCommandKeyword.OnlyNight));
        }

        var context = Context([Agent("B"), Agent("A")], shifts, commands, KeywordMode.Hard);
        var config = new TokenEvolutionConfig { PopulationSize = 16, MaxGenerations = 25, RandomSeed = 7, EvaluationParallelism = 1 };

        var best = TokenEvolutionLoop.Create().Run(context, config);

        best.Tokens.Count(t => t.AgentId == "A").ShouldBe(3);
        WizardKeywordWishes.For(context).Evaluate(best.Tokens, collectItems: false).Tally.Unfulfilled.ShouldBe(0);
    }
}
