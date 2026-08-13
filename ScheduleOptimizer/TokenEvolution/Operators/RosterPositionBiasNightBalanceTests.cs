// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// M11 night-share tie-break in the accuracy-aware pick: a NIGHT slot goes to the candidate
/// holding the fewest nights — but only as the LAST tie-break, after the rule-5 accuracy group
/// and the rule-6 preference, and never for non-night slots.
/// </summary>
[TestFixture]
public class RosterPositionBiasNightBalanceTests
{
    private static CoreAgent MakeAgent(string id, double guaranteedHours = 0) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: guaranteedHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        PerformsShiftWork = true,
    };

    private static CoreToken NightToken(string agentId, DateOnly date) => new(
        WorkIds: [],
        ShiftTypeIndex: 2,
        Date: date,
        TotalHours: 8m,
        StartAt: date.ToDateTime(new TimeOnly(23, 0)),
        EndAt: date.AddDays(1).ToDateTime(new TimeOnly(7, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: agentId);

    [Test]
    public void Pick_NightSlot_GoesToTheCandidateWithTheFewestNights()
    {
        // Both candidates sit above target (no accuracy split); A hoards three nights, B none.
        var monday = new DateOnly(2026, 4, 20);
        var roster = new List<CoreAgent> { MakeAgent("A"), MakeAgent("B") };
        var tokens = new List<CoreToken>
        {
            NightToken("A", monday),
            NightToken("A", monday.AddDays(3)),
            NightToken("A", monday.AddDays(6)),
        };

        for (var seed = 0; seed < 10; seed++)
        {
            var picked = RosterPositionBias.PickAccuracyAware(
                roster, tokens, roster, new Random(seed), balanceNightShare: true);
            picked.Id.ShouldBe("B", "the night must go to the agent holding the fewest nights on every seed");
        }
    }

    [Test]
    public void Pick_AccuracyGroupOutranksTheNightBalance()
    {
        // A is still below its guaranteed hours, B is not — rule 5 keeps the night with A even
        // though A already holds more nights.
        var monday = new DateOnly(2026, 4, 20);
        var below = MakeAgent("A", guaranteedHours: 180);
        var atTarget = MakeAgent("B");
        var roster = new List<CoreAgent> { below, atTarget };
        var tokens = new List<CoreToken> { NightToken("A", monday) };

        var picked = RosterPositionBias.PickAccuracyAware(
            roster, tokens, roster, new Random(1), balanceNightShare: true);

        picked.Id.ShouldBe("A", "rule 5 (hours top-down) outranks the rule-9 night tie-break");
    }
}
