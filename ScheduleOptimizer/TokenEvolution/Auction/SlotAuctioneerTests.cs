// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Auction;

[TestFixture]
public class SlotAuctioneerTests
{
    private static CoreAgent MakeAgent(string id, double guaranteed = 180)
    {
        return new CoreAgent(
            Id: id,
            CurrentHours: 0,
            GuaranteedHours: guaranteed,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = 180,
            MaxWorkDays = 5,
            MinRestDays = 2,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        };
    }

    private static CoreShift MakeShift(DateOnly date, string id) =>
        new(id, "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);

    private static SlotAuctioneer MakeSut() => new(
        new HungerOnlyBiddingAgent(),
        new Stage0HardConstraintChecker(),
        new Stage1SoftConstraintChecker());

    [Test]
    public void Run_TwoAgentsTwoSlots_RespectsBlockAndGapRules()
    {
        var sut = MakeSut();
        var agents = new[] { MakeAgent("A"), MakeAgent("B") };
        var d0 = new DateOnly(2026, 4, 20);
        var shifts = Enumerable.Range(0, 10)
            .Select(i => MakeShift(d0.AddDays(i), Guid.NewGuid().ToString()))
            .ToArray();
        var ctx = new CoreWizardContext
        {
            PeriodFrom = d0,
            PeriodUntil = d0.AddDays(9),
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
        };

        var outcome = sut.Run(ctx, new Random(0));

        outcome.Scenario.Tokens.ShouldNotBeEmpty();
        var agentA = outcome.Scenario.Tokens.Where(t => t.AgentId == "A").OrderBy(t => t.Date).ToList();
        for (int i = 1; i < agentA.Count; i++)
        {
            var consecutive = agentA[i].Date == agentA[i - 1].Date.AddDays(1);
            if (!consecutive)
            {
                ((agentA[i].Date.DayNumber - agentA[i - 1].Date.DayNumber) - 1)
                    .ShouldBeGreaterThanOrEqualTo(2,
                        "MinRestDays must be respected between blocks");
            }
        }
    }

    [Test]
    public void Run_DeterministicSeed_ProducesIdenticalScenarios()
    {
        var agents = new[] { MakeAgent("A"), MakeAgent("B") };
        var d0 = new DateOnly(2026, 4, 20);
        var shifts = Enumerable.Range(0, 7)
            .Select(i => MakeShift(d0.AddDays(i), $"S{i}"))
            .ToArray();
        var ctx = new CoreWizardContext
        {
            PeriodFrom = d0,
            PeriodUntil = d0.AddDays(6),
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
        };

        var run1 = MakeSut().Run(ctx, new Random(42));
        var run2 = MakeSut().Run(ctx, new Random(42));

        run1.Scenario.Tokens.Select(t => $"{t.Date}|{t.AgentId}").ShouldBe(
            run2.Scenario.Tokens.Select(t => $"{t.Date}|{t.AgentId}"));
    }

    [Test]
    public void Run_AllSlotsBlockedByStage0_LeavesSlotsUnassigned()
    {
        var sut = MakeSut();
        var agent = MakeAgent("A");
        var d0 = new DateOnly(2026, 4, 25); // Saturday
        var slot = MakeShift(d0, "S1");
        var ctx = new CoreWizardContext
        {
            PeriodFrom = d0,
            PeriodUntil = d0,
            Agents = new[]
            {
                agent with { WorkOnSaturday = false, WorkOnSunday = false },
            },
            Shifts = new[] { slot },
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
        };

        var outcome = sut.Run(ctx, new Random(0));

        outcome.Scenario.Tokens.ShouldBeEmpty();
        outcome.Results.Count().ShouldBe(1);
        outcome.Results[0].Round.ShouldBe(3);
        outcome.Results[0].WinnerAgentId.ShouldBeNull();
    }
}
