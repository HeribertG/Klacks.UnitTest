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
/// The keyword-wish tiers of the Wizard 1 fitness: counts compared lexicographically (FREE, then OnlyX, then NoX). In Soft
/// mode the broken tiers rank right after coverage and before the guaranteed hours (Stage 1) - a wish breaks only to cover
/// a slot, never for hours; in Hard mode they rank after Stage 1. Unfulfilled positive wishes rank after Stage 1 and before
/// the target hours (Stage 2) in both modes. Always below legality and coverage.
/// </summary>
[TestFixture]
public class KeywordWishFitnessTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static readonly KeywordWishTally NoWishes = default;
    private static readonly KeywordWishTally OneBrokenFree = new(BrokenFree: 1, BrokenOnly: 0, BrokenNo: 0, Unfulfilled: 0);
    private static readonly KeywordWishTally OneUnfulfilled = new(BrokenFree: 0, BrokenOnly: 0, BrokenNo: 0, Unfulfilled: 1);

    private static CoreScenario Scenario(int stage0, double stage1, KeywordWishTally wishes, double stage2)
        => new()
        {
            FitnessStage0Legality = 0,
            FitnessStage0 = stage0,
            FitnessStage1 = stage1,
            FitnessKeywordWishes = wishes,
            FitnessStage2 = stage2,
        };

    private static TokenFitnessEvaluator Evaluator(CoreWizardContext context) => TokenFitnessEvaluator.Create(context);

    private static TokenFitnessEvaluator Evaluator(KeywordMode mode) => Evaluator(Context([], [], [], mode));

    [Test]
    public void Comparer_SoftMode_BrokenWishesRankBeforeStage1()
    {
        var betterHours = Scenario(0, stage1: 1.0, OneBrokenFree, stage2: 1.0);
        var wishKept = Scenario(0, stage1: 0.5, NoWishes, stage2: 0);

        Evaluator(KeywordMode.Soft).Compare(wishKept, betterHours).ShouldBeLessThan(0, "a wish must not break for guaranteed hours");
    }

    [Test]
    public void Comparer_HardMode_BrokenWishesRankAfterStage1()
    {
        var betterHours = Scenario(0, stage1: 1.0, OneBrokenFree, stage2: 0);
        var wishKept = Scenario(0, stage1: 0.5, NoWishes, stage2: 0);

        Evaluator(KeywordMode.Hard).Compare(betterHours, wishKept).ShouldBeLessThan(0);
    }

    [TestCase(KeywordMode.Hard)]
    [TestCase(KeywordMode.Soft)]
    public void Comparer_UnfulfilledRanksAfterStage1(KeywordMode mode)
    {
        var betterHours = Scenario(0, stage1: 1.0, OneUnfulfilled, stage2: 0);
        var fulfilled = Scenario(0, stage1: 0.5, NoWishes, stage2: 0);

        Evaluator(mode).Compare(betterHours, fulfilled).ShouldBeLessThan(0);
    }

    [TestCase(KeywordMode.Hard)]
    [TestCase(KeywordMode.Soft)]
    public void Comparer_UnfulfilledRanksBeforeStage2(KeywordMode mode)
    {
        var fulfilled = Scenario(0, stage1: 1.0, NoWishes, stage2: 0.1);
        var betterTarget = Scenario(0, stage1: 1.0, OneUnfulfilled, stage2: 0.9);

        Evaluator(mode).Compare(fulfilled, betterTarget).ShouldBeLessThan(0);
    }

    [Test]
    public void Comparer_SoftMode_CoverageRanksAboveWishes()
    {
        var covered = Scenario(stage0: 0, stage1: 1.0, new KeywordWishTally(100, 100, 100, 100), stage2: 0);
        var openSlot = Scenario(stage0: 1, stage1: 1.0, NoWishes, stage2: 0);

        Evaluator(KeywordMode.Soft).Compare(covered, openSlot).ShouldBeLessThan(0);
    }

    [Test]
    public void Comparer_SoftMode_OneFreeOutweighsAnyNumberOfOnly()
    {
        var oneFree = Scenario(0, stage1: 1.0, OneBrokenFree, stage2: 1.0);
        var manyOnly = Scenario(0, stage1: 0, new KeywordWishTally(0, 1000, 1000, 1000), stage2: 0);

        Evaluator(KeywordMode.Soft).Compare(manyOnly, oneFree).ShouldBeLessThan(0);
    }

    /// <summary>
    /// Owner example: a choice between breaking one FREE and breaking twelve OnlyX, coverage equal - the plan breaking the
    /// OnlyX wishes wins. The former weighted sum (FREE 100, OnlyX 10) chose the FREE here.
    /// </summary>
    [Test]
    public void SoftMode_BreakingTwelveOnlyX_BeatsBreakingOneFree()
    {
        const int Slots = 12;
        var slotIds = Enumerable.Range(0, Slots).Select(_ => Guid.NewGuid()).ToList();
        var agents = new List<CoreAgent> { Agent("A") };
        var commands = new List<CoreScheduleCommand> { new("A", Monday, ScheduleCommandKeyword.Free) };
        for (var i = 0; i < Slots; i++)
        {
            agents.Add(Agent($"B{i}"));
            agents.Add(Agent($"C{i}"));
            commands.Add(new CoreScheduleCommand($"B{i}", Monday, ScheduleCommandKeyword.OnlyLate));
        }

        var context = Context(agents, slotIds.Select(id => Slot(id, Monday, Early)).ToList(), commands, KeywordMode.Soft);
        var evaluator = Evaluator(context);
        var breaksFree = new CoreScenario
        {
            Tokens = slotIds.Select((id, i) => Token(i == 0 ? "A" : $"C{i}", id, Monday, Early)).ToList(),
        };
        var breaksOnly = new CoreScenario
        {
            Tokens = slotIds.Select((id, i) => Token($"B{i}", id, Monday, Early)).ToList(),
        };

        evaluator.Evaluate(breaksFree, context);
        evaluator.Evaluate(breaksOnly, context);

        breaksFree.FitnessKeywordWishes.ShouldBe(new KeywordWishTally(1, 0, 0, Unfulfilled: Slots), "every LATE of B stays unfulfilled");
        breaksOnly.FitnessKeywordWishes.ShouldBe(new KeywordWishTally(0, Slots, 0, Unfulfilled: Slots), "an early shift on LATE is broken and unfulfilled");
        breaksOnly.FitnessStage0.ShouldBe(breaksFree.FitnessStage0);
        evaluator.Compare(breaksOnly, breaksFree).ShouldBeLessThan(0);
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
        covered.FitnessKeywordWishes.ShouldBe(OneBrokenFree);
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

        wished.FitnessKeywordWishes.ShouldBe(NoWishes);
        other.FitnessKeywordWishes.ShouldBe(OneUnfulfilled);
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

        scenario.FitnessKeywordWishes.ShouldBe(NoWishes);
        scenario.Fitness.ShouldBe(
            scenario.FitnessStage1 + (scenario.FitnessStage2 * 0.1) + (scenario.FitnessStage3 * 0.01) + (scenario.FitnessStage4 * 0.001),
            1e-12);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void HardMode_EvolutionPullsTheNightsToTheLowerRankedEmployeeWhoAskedForThem(bool withWish)
    {
        var shifts = new List<CoreShift>();
        var commands = new List<CoreScheduleCommand>();
        for (var day = 0; day < 3; day++)
        {
            var date = Monday.AddDays(day);
            shifts.Add(Slot(Guid.NewGuid(), date, Night));
            commands.Add(new CoreScheduleCommand("A", date, ScheduleCommandKeyword.OnlyNight));
        }

        var context = Context([Agent("B"), Agent("A")], shifts, withWish ? commands : [], KeywordMode.Hard);
        var config = new TokenEvolutionConfig { PopulationSize = 16, MaxGenerations = 25, RandomSeed = 7, EvaluationParallelism = 1 };

        var best = TokenEvolutionLoop.Create().Run(context, config);

        best.Tokens.Count.ShouldBe(3);
        var nightsOfA = best.Tokens.Count(t => t.AgentId == "A");
        if (withWish)
        {
            nightsOfA.ShouldBe(3);
        }
        else
        {
            nightsOfA.ShouldBeLessThan(3, "control: without the wish the target-hours stage serves the top roster agent B first");
        }
    }

    [Test]
    public void SoftMode_EvolutionCoversTheOnlySlotOnAFreeDay_AndTheCountShowsTheBrokenFree()
    {
        var context = Context(
            [Agent("A")],
            [Slot(ShiftId, Monday, Early)],
            [new CoreScheduleCommand("A", Monday, ScheduleCommandKeyword.Free)],
            KeywordMode.Soft);
        var config = new TokenEvolutionConfig { PopulationSize = 8, MaxGenerations = 10, RandomSeed = 3, EvaluationParallelism = 1 };

        var best = TokenEvolutionLoop.Create().Run(context, config);

        best.Tokens.Count.ShouldBe(1);
        WizardKeywordWishes.For(context).Evaluate(best.Tokens, collectItems: true).Tally.BrokenFree.ShouldBe(1);
    }

    [Test]
    public void HardMode_EvolutionLeavesTheOnlySlotOnAFreeDayOpen()
    {
        var context = Context(
            [Agent("A")],
            [Slot(ShiftId, Monday, Early)],
            [new CoreScheduleCommand("A", Monday, ScheduleCommandKeyword.Free)],
            KeywordMode.Hard);
        var config = new TokenEvolutionConfig { PopulationSize = 8, MaxGenerations = 10, RandomSeed = 3, EvaluationParallelism = 1 };

        TokenEvolutionLoop.Create().Run(context, config).Tokens.ShouldBeEmpty();
    }

    /// <summary>
    /// Owner example: every slot can be covered without A, A is below his guaranteed hours and marked Tuesday FREE. The Soft
    /// run must not give him the Tuesday shift for hours - he stays under his guaranteed hours. The control run without the
    /// wish shows the guaranteed-hours stage would otherwise give it to him.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void SoftMode_AWishIsNotBrokenForGuaranteedHours(bool withWish)
    {
        var tuesday = Monday.AddDays(1);
        List<CoreScheduleCommand> commands = withWish ? [new CoreScheduleCommand("A", tuesday, ScheduleCommandKeyword.Free)] : [];
        var context = Context(
            [Agent("A", guaranteedHours: 16), Agent("B")],
            [Slot(Guid.NewGuid(), Monday, Early), Slot(Guid.NewGuid(), tuesday, Early)],
            commands,
            KeywordMode.Soft);
        var config = new TokenEvolutionConfig { PopulationSize = 16, MaxGenerations = 25, RandomSeed = 5, EvaluationParallelism = 1 };

        var best = TokenEvolutionLoop.Create().Run(context, config);

        best.Tokens.Count.ShouldBe(2, "both slots are covered");
        var aWorksTuesday = best.Tokens.Any(t => t.AgentId == "A" && t.Date == tuesday);
        if (withWish)
        {
            aWorksTuesday.ShouldBeFalse();
            WizardKeywordWishes.For(context).Evaluate(best.Tokens, collectItems: false).Tally.Broken.ShouldBe(0);
            best.Tokens.Where(t => t.AgentId == "A").Sum(t => t.TotalHours).ShouldBeLessThan(16m, "A stays under his guaranteed hours");
        }
        else
        {
            aWorksTuesday.ShouldBeTrue("control: without the wish Stage 1 gives A both shifts");
        }
    }
}
