// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Keywords;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords.KeywordWishTestData;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The wish repair of Wizard 1 (M3 real-data finding 2026-11-06): Lena wishes NIGHT on a day Amir holds the night, she
/// works an early shift on another day. Handing her the night alone would cost Amir his guaranteed hours (Stage 1 ranks
/// before the wish), the hours-neutral exchange (her early shift to Amir, his night to her) fulfils the wish and is found.
/// Hard rules on both receiving sides stay binding: availability, the holder's own keywords, locked shifts.
/// </summary>
[TestFixture]
public class WishRepairSwapTests
{
    private const string Lena = "lena";
    private const string Amir = "amir";
    private const double OneShiftGuarantee = 8;

    private static readonly DateOnly EarlyDay = Monday.AddDays(1);
    private static readonly DateOnly NightDay = Monday.AddDays(3);
    private static readonly Guid EarlyShift = Guid.NewGuid();
    private static readonly Guid NightShift = Guid.NewGuid();

    private static CoreWizardContext LenaAmirContext(
        KeywordMode mode,
        IReadOnlyList<CoreScheduleCommand>? extraCommands = null,
        IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>? ineligible = null)
    {
        var commands = new List<CoreScheduleCommand> { new(Lena, NightDay, ScheduleCommandKeyword.OnlyNight) };
        commands.AddRange(extraCommands ?? []);
        return new CoreWizardContext
        {
            PeriodFrom = Monday,
            PeriodUntil = Sunday,
            Agents = [Agent(Lena, OneShiftGuarantee), Agent(Amir, OneShiftGuarantee)],
            Shifts = [Slot(EarlyShift, EarlyDay, Early), Slot(NightShift, NightDay, Night)],
            ScheduleCommands = commands,
            KeywordMode = mode,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMinPauseHours = 11,
            IneligibleAssignments = ineligible ?? new HashSet<(string, Guid, DateOnly)>(),
        };
    }

    private static CoreScenario LenaEarlyAmirNight(bool lockNight = false)
        => new()
        {
            Id = "plan",
            Tokens =
            [
                Token(Lena, EarlyShift, EarlyDay, Early),
                Token(Amir, NightShift, NightDay, Night) with { IsLocked = lockNight },
            ],
        };

    private static string OwnerOf(CoreScenario scenario, Guid shiftId)
        => scenario.Tokens.Single(token => token.ShiftRefId == shiftId).AgentId;

    [TestCase(KeywordMode.Hard)]
    [TestCase(KeywordMode.Soft)]
    public void Apply_FindsTheHoursNeutralExchange(KeywordMode mode)
    {
        var context = LenaAmirContext(mode);
        var evaluator = TokenFitnessEvaluator.Create(context);
        var plan = LenaEarlyAmirNight();

        var repaired = new WishRepairSwap().Apply(plan, context, evaluator);

        OwnerOf(repaired, NightShift).ShouldBe(Lena);
        OwnerOf(repaired, EarlyShift).ShouldBe(Amir);
        repaired.FitnessKeywordWishes.Unfulfilled.ShouldBe(0);
        repaired.FitnessStage1.ShouldBe(plan.FitnessStage1);
        evaluator.Compare(repaired, plan).ShouldBeLessThan(0);
    }

    [Test]
    public void Apply_NightBlockedByAvailability_LeavesThePlan()
    {
        var context = LenaAmirContext(
            KeywordMode.Hard, ineligible: new HashSet<(string, Guid, DateOnly)> { (Lena, NightShift, NightDay) });
        var plan = LenaEarlyAmirNight();

        var repaired = new WishRepairSwap().Apply(plan, context, TokenFitnessEvaluator.Create(context));

        repaired.ShouldBeSameAs(plan);
    }

    [Test]
    public void Apply_HolderMayNotTakeTheCounterpart_LeavesThePlan()
    {
        var context = LenaAmirContext(
            KeywordMode.Hard, extraCommands: [new CoreScheduleCommand(Amir, EarlyDay, ScheduleCommandKeyword.Free)]);
        var plan = LenaEarlyAmirNight();

        var repaired = new WishRepairSwap().Apply(plan, context, TokenFitnessEvaluator.Create(context));

        repaired.ShouldBeSameAs(plan);
    }

    [Test]
    public void Apply_LockedNight_LeavesThePlan()
    {
        var context = LenaAmirContext(KeywordMode.Hard);
        var plan = LenaEarlyAmirNight(lockNight: true);

        var repaired = new WishRepairSwap().Apply(plan, context, TokenFitnessEvaluator.Create(context));

        repaired.ShouldBeSameAs(plan);
    }

    [Test]
    public void Apply_WithoutScheduleCommands_ReturnsTheSameInstance()
    {
        var context = Context(
            [Agent(Lena, OneShiftGuarantee), Agent(Amir, OneShiftGuarantee)],
            [Slot(EarlyShift, EarlyDay, Early), Slot(NightShift, NightDay, Night)],
            [],
            KeywordMode.Hard);
        var plan = LenaEarlyAmirNight();

        new WishRepairSwap().Apply(plan, context, TokenFitnessEvaluator.Create(context)).ShouldBeSameAs(plan);
    }

    [Test]
    public void Apply_HolderWishesTheSameNight_LeavesThePlan()
    {
        var context = LenaAmirContext(
            KeywordMode.Hard, extraCommands: [new CoreScheduleCommand(Amir, NightDay, ScheduleCommandKeyword.OnlyNight)]);
        var plan = LenaEarlyAmirNight();

        var repaired = new WishRepairSwap().Apply(plan, context, TokenFitnessEvaluator.Create(context));

        repaired.ShouldBeSameAs(plan);
    }

    [Test]
    public void IsRepair_BetterHoursWithoutAFulfilledWish_IsRejected()
    {
        var current = new CoreScenario { FitnessStage1 = 0.5, FitnessKeywordWishes = new KeywordWishTally(0, 0, 0, 1) };
        var betterHoursOnly = new CoreScenario { FitnessStage1 = 0.9, FitnessKeywordWishes = new KeywordWishTally(0, 0, 0, 1) };

        WishRepairSwap.IsRepair(betterHoursOnly, current).ShouldBeFalse();
    }

    [Test]
    public void IsRepair_FulfilledWishAtTheCostOfStage2_IsRejected()
    {
        var current = new CoreScenario { FitnessStage1 = 1, FitnessStage2 = 0.8, FitnessKeywordWishes = new KeywordWishTally(0, 0, 0, 1) };
        var worseTarget = new CoreScenario { FitnessStage1 = 1, FitnessStage2 = 0.7, FitnessKeywordWishes = default };

        WishRepairSwap.IsRepair(worseTarget, current).ShouldBeFalse();
    }

    [Test]
    public void IsRepair_FulfilledWishWithEverythingElseEqual_IsAccepted()
    {
        var current = new CoreScenario { FitnessStage1 = 1, FitnessStage2 = 0.8, FitnessKeywordWishes = new KeywordWishTally(0, 0, 0, 1) };
        var fulfilled = new CoreScenario { FitnessStage1 = 1, FitnessStage2 = 0.8, FitnessKeywordWishes = default };

        WishRepairSwap.IsRepair(fulfilled, current).ShouldBeTrue();
    }
}
