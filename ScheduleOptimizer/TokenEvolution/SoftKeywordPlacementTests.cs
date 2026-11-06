// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Keywords;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords.KeywordWishTestData;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

/// <summary>
/// The Soft placement paths break a keyword wish only to cover an otherwise open slot and then the least severe one
/// (none, NoX, OnlyX, FREE): auction round 2, the keyword rung of the repair ladder (after the block-ideal rung) and the
/// forced coverage of the greedy seeder. The hour-driven pick of each path runs only on the least-breaking candidates.
/// </summary>
[TestFixture]
public class SoftKeywordPlacementTests
{
    private const double TargetHours = 40;

    private static readonly Guid MondaySlot = Guid.NewGuid();
    private static readonly Guid MondayLateSlot = Guid.NewGuid();

    private static CoreScheduleCommand Command(string agentId, ScheduleCommandKeyword keyword) => new(agentId, Monday, keyword);

    private static SlotAuctioneer Auctioneer() => new(
        new HungerOnlyBiddingAgent(), new Stage0HardConstraintChecker(), new Stage1SoftConstraintChecker());

    [Test]
    public void Auction_RoundTwo_BreaksTheOnlyXInsteadOfTheFree()
    {
        var context = Context(
            [Agent("A", TargetHours), Agent("D", TargetHours)],
            [Slot(MondaySlot, Monday, Early)],
            [Command("A", ScheduleCommandKeyword.Free), Command("D", ScheduleCommandKeyword.OnlyLate)],
            KeywordMode.Soft);

        var outcome = Auctioneer().Run(context, new Random(0));

        outcome.Results.ShouldHaveSingleItem().Round.ShouldBe(2);
        outcome.Scenario.Tokens.ShouldHaveSingleItem().AgentId.ShouldBe("D");
    }

    [Test]
    public void Auction_RoundTwo_DoesNotBreakAWishForGuaranteedHours()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Sunday,
            Agents = [Agent("A", TargetHours), Agent("C")],
            Shifts = [Slot(MondaySlot, Monday, Early)],
            ScheduleCommands = [Command("A", ScheduleCommandKeyword.Free)],
            ShiftPreferences = [new CoreShiftPreference("C", Guid.NewGuid(), ShiftPreferenceKind.Preferred)],
            KeywordMode = KeywordMode.Soft,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };

        var outcome = Auctioneer().Run(context, new Random(0));

        outcome.Results.ShouldHaveSingleItem().Round.ShouldBe(2);
        outcome.Scenario.Tokens.ShouldHaveSingleItem().AgentId.ShouldBe(
            "C", "A is below his guaranteed hours, but only C covers the slot without breaking a wish");
    }

    [Test]
    public void Repair_TakesTheBlockIdealRungBeforeBreakingAWish()
    {
        var tuesday = Monday.AddDays(1);
        var tuesdaySlot = Guid.NewGuid();
        var blockIdealOne = Agent("B") with { MaxWorkDays = 1 };
        var context = Context(
            [Agent("A"), blockIdealOne],
            [Slot(MondaySlot, Monday, Early), Slot(tuesdaySlot, tuesday, Early)],
            [Command("A", ScheduleCommandKeyword.Free)],
            KeywordMode.Soft);
        var scenario = new CoreScenario { Id = "s", Tokens = [Token("B", tuesdaySlot, tuesday, Early) with { IsLocked = true }] };

        SlotConstraintFilter.IsValidAssignment(
                blockIdealOne, Monday, Early.Kind, MondaySlot, 8, context, scenario.Tokens, relaxation: SlotRelaxation.RestDaysOnly)
            .ShouldBeFalse("B needs the block-ideal rung");

        var repaired = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        repaired.Tokens.ShouldContain(t => t.ShiftRefId == MondaySlot && t.AgentId == "B");
        WizardKeywordWishes.For(context).Evaluate(repaired.Tokens, collectItems: false).Tally.Broken.ShouldBe(0);
    }

    [Test]
    public void Repair_OnTheKeywordRung_BreaksTheLeastSevereWish()
    {
        var context = Context(
            [Agent("A"), Agent("D")],
            [Slot(MondaySlot, Monday, Early)],
            [Command("A", ScheduleCommandKeyword.Free), Command("D", ScheduleCommandKeyword.OnlyLate)],
            KeywordMode.Soft);
        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var repaired = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        repaired.Tokens.ShouldHaveSingleItem().AgentId.ShouldBe("D");
        WizardKeywordWishes.For(context).Evaluate(repaired.Tokens, collectItems: false).Tally
            .ShouldBe(new KeywordWishTally(BrokenFree: 0, BrokenOnly: 1, BrokenNo: 0, Unfulfilled: 1), "an early shift on LATE is broken and unfulfilled");
    }

    [Test]
    public void Repair_OnTheKeywordRung_MovesTheBlockerAndBreaksTheWishOfTheMover()
    {
        var (context, scenario) = MoveContext(KeywordMode.Soft);
        var escalations = new List<string>();

        var repaired = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)), escalations.Add);

        repaired.Tokens.Count.ShouldBe(2);
        repaired.Tokens.ShouldContain(t => t.ShiftRefId == MondaySlot && t.AgentId == "X");
        repaired.Tokens.ShouldContain(t => t.ShiftRefId == MondayLateSlot && t.AgentId == "Y", "Y takes the handed-over late shift");
        escalations.ShouldHaveSingleItem().ShouldEndWith("relocate", customMessage: "the slot was filled by the move, not directly");
        WizardKeywordWishes.For(context).Evaluate(repaired.Tokens, collectItems: false).Tally
            .ShouldBe(new KeywordWishTally(BrokenFree: 0, BrokenOnly: 1, BrokenNo: 0, Unfulfilled: 1), "X works early on LATE");
    }

    [Test]
    public void Repair_HardMode_DoesNotMoveABlockerToBreakAWish()
    {
        var (context, scenario) = MoveContext(KeywordMode.Hard);

        var repaired = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(scenario, null, context, new Random(0)));

        repaired.Tokens.ShouldHaveSingleItem().AgentId.ShouldBe("X");
        repaired.Tokens.ShouldNotContain(t => t.ShiftRefId == MondaySlot);
    }

    /// <summary>
    /// The early slot is open. X (LATE on Monday) holds the late shift, so the daily hours block the early slot; Y has no
    /// wish but has blacklisted the early shift (a hard veto) and can only take the late one. No agent takes the slot
    /// directly on any rung, and the move (X hands the late shift to Y, then takes the early one) is legal only once X's
    /// wish may be broken.
    /// </summary>
    private static (CoreWizardContext Context, CoreScenario Scenario) MoveContext(KeywordMode mode)
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Sunday,
            Agents = [Agent("X"), Agent("Y")],
            Shifts = [Slot(MondaySlot, Monday, Early), Slot(MondayLateSlot, Monday, Late)],
            ScheduleCommands = [Command("X", ScheduleCommandKeyword.OnlyLate)],
            ShiftPreferences = [new CoreShiftPreference("Y", MondaySlot, ShiftPreferenceKind.Blacklist)],
            KeywordMode = mode,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
        };
        return (context, new CoreScenario { Id = "s", Tokens = [Token("X", MondayLateSlot, Monday, Late)] });
    }

    [TestCase(KeywordMode.Soft, "D")]
    [TestCase(KeywordMode.Hard, null)]
    public void Repair_HardMode_NeverReachesTheKeywordRung(KeywordMode mode, string? expected)
    {
        var context = Context(
            [Agent("A"), Agent("D")],
            [Slot(MondaySlot, Monday, Early)],
            [Command("A", ScheduleCommandKeyword.Free), Command("D", ScheduleCommandKeyword.OnlyLate)],
            mode);

        var repaired = new TokenRepair(new TokenConstraintChecker())
            .Apply(new TokenOperatorContext(new CoreScenario { Id = "s", Tokens = [] }, null, context, new Random(0)));

        repaired.Tokens.SingleOrDefault()?.AgentId.ShouldBe(expected);
        if (expected is null)
        {
            repaired.Tokens.ShouldBeEmpty();
        }
    }

    [Test]
    public void GreedyForcedCoverage_PrefersTheAgentBreakingNoWishOverTheLeastLoaded()
    {
        var capped = Agent("C") with { MaximumHours = 4 };
        var context = Context(
            [Agent("A"), capped],
            [Slot(MondaySlot, Monday, Early)],
            [Command("A", ScheduleCommandKeyword.Free)],
            KeywordMode.Soft);

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.ShouldHaveSingleItem().AgentId.ShouldBe("C");
    }
}
