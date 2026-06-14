// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction;

[TestFixture]
public sealed class FuzzyBiddingAgentRotationTests
{
    private static CoreAgent MakeAgent(bool performsShiftWork = true) => new(
        Id: "A",
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: 6,
        MinRestHours: 12,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 160,
        PerformsShiftWork = performsShiftWork,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext MakeContext(CoreAgent agent) => new()
    {
        PeriodFrom = new DateOnly(2026, 6, 1),
        PeriodUntil = new DateOnly(2026, 6, 30),
        Agents = [agent],
    };

    private static AgentRuntimeState StateAfterEarlyBlock() => new(
        AgentId: "A",
        HoursAssignedThisRun: 24,
        CurrentBlockLength: 3,
        LastWorkedDate: new DateOnly(2026, 6, 3),
        DaysSinceShiftType: [3, int.MaxValue, int.MaxValue],
        CurrentBlockStartShiftType: 0);

    [Test]
    public void Evaluate_NewBlock_SameTypeBidsLowerThanRotatedType()
    {
        var sut = new FuzzyBiddingAgent();
        var agent = MakeAgent();
        var context = MakeContext(agent);
        var state = StateAfterEarlyBlock();

        // Two rest days since 06-03; both slots on 06-06 start a new block.
        var earlySlot = new CoreShift("s1", "F10", "2026-06-06", "07:00", "15:00", 8, 1, 0);
        var lateSlot = new CoreShift("s2", "S10", "2026-06-06", "15:00", "23:00", 8, 1, 0);

        var earlyBid = sut.Evaluate(agent, earlySlot, state, context);
        var lateBid = sut.Evaluate(agent, lateSlot, state, context);

        earlyBid.Score.ShouldBeLessThan(lateBid.Score,
            "restarting a block with the same shift type must bid lower than rotating to the next type");
    }

    [Test]
    public void Evaluate_NewBlockSameType_NonShiftWorkerIsNotPenalized()
    {
        var sut = new FuzzyBiddingAgent();
        var shiftWorker = MakeAgent(performsShiftWork: true);
        var dayWorker = MakeAgent(performsShiftWork: false);
        var state = StateAfterEarlyBlock();

        var earlySlot = new CoreShift("s1", "F10", "2026-06-06", "07:00", "15:00", 8, 1, 0);

        var shiftWorkerBid = sut.Evaluate(shiftWorker, earlySlot, state, MakeContext(shiftWorker));
        var dayWorkerBid = sut.Evaluate(dayWorker, earlySlot, state, MakeContext(dayWorker));

        dayWorkerBid.Score.ShouldBeGreaterThan(shiftWorkerBid.Score,
            "day-only workers may repeat their day-shift blocks without rotation penalty");
    }
}
