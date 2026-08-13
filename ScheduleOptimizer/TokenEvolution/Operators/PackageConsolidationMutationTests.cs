// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// M6 package consolidation: a short package dissolves through an equal-hours trade onto a date
/// that extends another package of the same agent, so the child ties stages 0 to 2 and the
/// stage-3 compactness term can decide. The trade must obey the same hard gates as the random
/// swap and leave the plan untouched when no valid trade exists.
/// </summary>
[TestFixture]
public class PackageConsolidationMutationTests
{
    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        MaximumHours = 0,
        PerformsShiftWork = true,
        MaxWorkDays = 5,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreToken MakeToken(string agentId, DateOnly date, bool isLocked = false) => new(
        WorkIds: [],
        ShiftTypeIndex: 0,
        Date: date,
        TotalHours: 8m,
        StartAt: date.ToDateTime(new TimeOnly(7, 0)),
        EndAt: date.ToDateTime(new TimeOnly(15, 0)),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: isLocked,
        LocationContext: null,
        ShiftRefId: Guid.Empty,
        AgentId: agentId);

    private static CoreWizardContext Context(DateOnly from, DateOnly until) => new()
    {
        Agents = [MakeAgent("A"), MakeAgent("B")],
        PeriodFrom = from,
        PeriodUntil = until,
        SchedulingMaxDailyHours = 10,
        SchedulingMinPauseHours = 11,
        SchedulingMaxConsecutiveDays = 6,
    };

    private static CoreScenario Scenario(params CoreToken[] tokens) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Tokens = tokens.ToList(),
    };

    [Test]
    public void Apply_TradesTheShortPackageDayOntoAPackageExtension()
    {
        // A: package Mon-Wed plus an isolated single day the following Mon (short package).
        // B: package Mon-Thu, where Thu is the day that would extend A's package.
        // The trade must move A's single day to B and hand A the Thursday that extends Mon-Wed.
        var monday = new DateOnly(2026, 4, 20);
        var isolated = MakeToken("A", monday.AddDays(7));
        var extendsA = MakeToken("B", monday.AddDays(3));
        var scenario = Scenario(
            MakeToken("A", monday),
            MakeToken("A", monday.AddDays(1)),
            MakeToken("A", monday.AddDays(2)),
            isolated,
            MakeToken("B", monday.AddDays(4)),
            MakeToken("B", monday.AddDays(5)),
            MakeToken("B", monday.AddDays(6)),
            extendsA);

        var result = new PackageConsolidationMutation().Apply(
            new TokenOperatorContext(scenario, null, Context(monday, monday.AddDays(10)), new Random(42)));

        result.Tokens.ShouldContain(
            t => t.Date == monday.AddDays(3) && t.AgentId == "A",
            "the Thursday token must move to A, extending A's Mon-Wed package to four days");
        result.Tokens.ShouldContain(
            t => t.Date == monday.AddDays(7) && t.AgentId == "B",
            "A's isolated day must move to B in exchange");
    }

    [Test]
    public void Apply_KeepsBothHourAccountsUnchanged()
    {
        var monday = new DateOnly(2026, 4, 20);
        var scenario = Scenario(
            MakeToken("A", monday),
            MakeToken("A", monday.AddDays(1)),
            MakeToken("A", monday.AddDays(2)),
            MakeToken("A", monday.AddDays(7)),
            MakeToken("B", monday.AddDays(3)),
            MakeToken("B", monday.AddDays(4)),
            MakeToken("B", monday.AddDays(5)));

        var before = scenario.Tokens
            .GroupBy(t => t.AgentId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.TotalHours));

        var result = new PackageConsolidationMutation().Apply(
            new TokenOperatorContext(scenario, null, Context(monday, monday.AddDays(10)), new Random(7)));

        var after = result.Tokens
            .GroupBy(t => t.AgentId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.TotalHours));
        after.ShouldBe(before, "an equal-hours trade may never move hours between the agents");
    }

    [Test]
    public void Apply_WithoutAnyShortPackage_ReturnsThePlanUnchanged()
    {
        var monday = new DateOnly(2026, 4, 20);
        var scenario = Scenario(
            MakeToken("A", monday),
            MakeToken("A", monday.AddDays(1)),
            MakeToken("A", monday.AddDays(2)),
            MakeToken("B", monday.AddDays(1)),
            MakeToken("B", monday.AddDays(2)),
            MakeToken("B", monday.AddDays(3)));

        var result = new PackageConsolidationMutation().Apply(
            new TokenOperatorContext(scenario, null, Context(monday, monday.AddDays(6)), new Random(42)));

        result.Tokens.Select(t => (t.AgentId, t.Date))
            .ShouldBe(scenario.Tokens.Select(t => (t.AgentId, t.Date)),
                "a plan without short packages offers nothing to consolidate");
    }

    [Test]
    public void Apply_NeverTradesALockedToken()
    {
        // The only dissolvable short-package day is locked, so the plan must stay unchanged.
        var monday = new DateOnly(2026, 4, 20);
        var scenario = Scenario(
            MakeToken("A", monday),
            MakeToken("A", monday.AddDays(1)),
            MakeToken("A", monday.AddDays(2)),
            MakeToken("A", monday.AddDays(7), isLocked: true),
            MakeToken("B", monday.AddDays(3)),
            MakeToken("B", monday.AddDays(4)));

        var result = new PackageConsolidationMutation().Apply(
            new TokenOperatorContext(scenario, null, Context(monday, monday.AddDays(10)), new Random(42)));

        result.Tokens.Single(t => t.Date == monday.AddDays(7)).AgentId.ShouldBe(
            "A", "a locked token must never change hands");
        result.Tokens.Single(t => t.Date == monday.AddDays(3)).AgentId.ShouldBe(
            "B", "without a tradable short-package day nothing may move");
    }
}
