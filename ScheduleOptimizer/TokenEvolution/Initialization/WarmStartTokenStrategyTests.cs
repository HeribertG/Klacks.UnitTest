// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class WarmStartTokenStrategyTests
{
    private static readonly Guid SeedShiftId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static CoreShift InScopeShift() => new CoreShift(
        Id: SeedShiftId.ToString(),
        Name: "Seed",
        Date: "2026-06-01",
        StartTime: "08:00",
        EndTime: "16:00",
        Hours: 8,
        RequiredAssignments: 1,
        Priority: 0);

    private static CoreAgent MakeAgent(string id) => new CoreAgent(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 0,
        Motivation: 0.5,
        MaxDailyHours: 24,
        MaxWeeklyHours: 168,
        MaxOptimalGap: 2)
    {
        FullTime = 40,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWarmStartAssignment Assignment(string agentId, DateOnly date) => new CoreWarmStartAssignment(
        AgentId: agentId,
        Date: date,
        ShiftRefId: SeedShiftId,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        TotalHours: 8m);

    private static CoreLockedWork Locked(string agentId, DateOnly date) => new CoreLockedWork(
        WorkId: $"locked-{agentId}-{date:yyyyMMdd}",
        AgentId: agentId,
        Date: date,
        ShiftTypeIndex: 0,
        TotalHours: 8m,
        StartAt: date.ToDateTime(new TimeOnly(8, 0)),
        EndAt: date.ToDateTime(new TimeOnly(16, 0)),
        ShiftRefId: Guid.NewGuid(),
        LocationContext: null);

    private static CoreWizardContext MakeContext(
        IEnumerable<CoreAgent> agents,
        IEnumerable<CoreWarmStartAssignment> assignments,
        IEnumerable<CoreLockedWork>? locked = null,
        IEnumerable<CoreBreakBlocker>? breaks = null)
    {
        return new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 6, 1),
            PeriodUntil = new DateOnly(2026, 6, 30),
            Agents = agents.ToList(),
            Shifts = [InScopeShift()],
            WarmStartAssignments = assignments.ToList(),
            LockedWorks = (locked ?? []).ToList(),
            BreakBlockers = (breaks ?? []).ToList(),
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 0,
            SchedulingMaxDailyHours = 24,
        };
    }

    /// <summary>Deterministic Random double whose NextDouble is high enough that no seed cell is perturbed
    /// away (rate = 0.05 + 0.999*0.10 = 0.15, and every per-cell draw 0.999 >= 0.15).</summary>
    private sealed class NoDropRandom : Random
    {
        public override double NextDouble() => 0.999;
    }

    [Test]
    public void BuildScenario_SeedTokens_AreNotLocked()
    {
        var agent = MakeAgent("A");
        var start = new DateOnly(2026, 6, 1); // Monday
        var assignments = Enumerable.Range(0, 5).Select(i => Assignment("A", start.AddDays(i)));
        var context = MakeContext([agent], assignments);

        var scenario = new WarmStartTokenStrategy().BuildScenario(context, new NoDropRandom());

        scenario.Tokens.Count.ShouldBe(5);
        scenario.Tokens.ShouldAllBe(t => !t.IsLocked);
        scenario.Tokens.ShouldAllBe(t => t.WorkIds.Count == 0);
    }

    [Test]
    public void BuildScenario_SeedCollidesWithLockedCell_LockedWinsSeedDropped()
    {
        var agent = MakeAgent("A");
        var day = new DateOnly(2026, 6, 2); // Tuesday
        var context = MakeContext(
            [agent],
            [Assignment("A", day)],
            locked: [Locked("A", day)]);

        var scenario = new WarmStartTokenStrategy().BuildScenario(context, new NoDropRandom());

        scenario.Tokens.Count(t => t.IsLocked && t.AgentId == "A" && t.Date == day).ShouldBe(1);
        scenario.Tokens.Count(t => !t.IsLocked && t.AgentId == "A" && t.Date == day).ShouldBe(0);
    }

    [Test]
    public void BuildScenario_InvalidCell_IsDroppedWhileRestSurvives()
    {
        var agent = MakeAgent("A");
        var blockedDay = new DateOnly(2026, 6, 3);   // Wednesday, covered by a break
        var validDays = new[]
        {
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 2),
            new DateOnly(2026, 6, 4),
            new DateOnly(2026, 6, 5),
        };
        var assignments = validDays.Select(d => Assignment("A", d))
            .Append(Assignment("A", blockedDay));
        var breaks = new[] { new CoreBreakBlocker("A", blockedDay, blockedDay, "Vacation") };
        var context = MakeContext([agent], assignments, breaks: breaks);

        var scenario = new WarmStartTokenStrategy().BuildScenario(context, new NoDropRandom());

        scenario.Tokens.Any(t => t.Date == blockedDay).ShouldBeFalse();
        scenario.Tokens.Select(t => t.Date).OrderBy(d => d).ShouldBe(validDays);
    }

    [Test]
    public void BuildScenario_AssignmentOutsideShiftScope_IsDroppedWhileInScopeSurvives()
    {
        var agent = MakeAgent("A");
        var inScopeDay = new DateOnly(2026, 6, 1);  // Monday, uses SeedShiftId (in context.Shifts)
        var outOfScopeDay = new DateOnly(2026, 6, 2);
        var outOfScope = new CoreWarmStartAssignment(
            AgentId: "A",
            Date: outOfScopeDay,
            ShiftRefId: Guid.NewGuid(),
            StartAt: outOfScopeDay.ToDateTime(new TimeOnly(8, 0)),
            EndAt: outOfScopeDay.ToDateTime(new TimeOnly(16, 0)),
            TotalHours: 8m);
        var context = MakeContext([agent], [Assignment("A", inScopeDay), outOfScope]);

        var scenario = new WarmStartTokenStrategy().BuildScenario(context, new NoDropRandom());

        scenario.Tokens.Any(t => t.Date == outOfScopeDay).ShouldBeFalse();
        scenario.Tokens.Count(t => t.Date == inScopeDay).ShouldBe(1);
    }

    [Test]
    public void BuildScenario_EmptySeed_ReturnsOnlyLockedTokens()
    {
        var agent = MakeAgent("A");
        var d1 = new DateOnly(2026, 6, 1);
        var d2 = new DateOnly(2026, 6, 2);
        var context = MakeContext(
            [agent],
            assignments: [],
            locked: [Locked("A", d1), Locked("A", d2)]);

        var scenario = new WarmStartTokenStrategy().BuildScenario(context, new NoDropRandom());

        scenario.Tokens.Count.ShouldBe(2);
        scenario.Tokens.ShouldAllBe(t => t.IsLocked);
        scenario.Tokens.ShouldAllBe(t => t.WorkIds.Count > 0);
    }

    [Test]
    public void BuildScenario_SameRngSeed_ProducesIdenticalSurvivorCells()
    {
        var agents = Enumerable.Range(0, 200).Select(i => MakeAgent($"A{i:D3}")).ToList();
        var day = new DateOnly(2026, 6, 1); // Monday
        var assignments = agents.Select(a => Assignment(a.Id, day)).ToList();
        var context = MakeContext(agents, assignments);
        var strategy = new WarmStartTokenStrategy();

        var run1 = strategy.BuildScenario(context, new Random(123))
            .Tokens.Select(t => t.AgentId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var run2 = strategy.BuildScenario(context, new Random(123))
            .Tokens.Select(t => t.AgentId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        run2.ShouldBe(run1);
    }

    [Test]
    public void BuildScenario_PerCellPerturbation_DropsPlausibleFractionAndDiffersAcrossSeeds()
    {
        var agents = Enumerable.Range(0, 200).Select(i => MakeAgent($"A{i:D3}")).ToList();
        var day = new DateOnly(2026, 6, 1); // Monday
        var assignments = agents.Select(a => Assignment(a.Id, day)).ToList();
        var context = MakeContext(agents, assignments);
        var strategy = new WarmStartTokenStrategy();

        var survivorsSeedA = strategy.BuildScenario(context, new Random(1))
            .Tokens.Select(t => t.AgentId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var survivorsSeedB = strategy.BuildScenario(context, new Random(2))
            .Tokens.Select(t => t.AgentId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var droppedA = 200 - survivorsSeedA.Count;
        droppedA.ShouldBeGreaterThan(0);
        droppedA.ShouldBeLessThan(60);
        survivorsSeedB.ShouldNotBe(survivorsSeedA);
    }

    [Test]
    public void BuildScenario_ConsecutiveDays_ShareBlockWithRunningPositionAndGapStartsNewBlock()
    {
        var agent = MakeAgent("A");
        var d0 = new DateOnly(2026, 6, 1);
        var assignments = new[]
        {
            Assignment("A", d0),            // Monday
            Assignment("A", d0.AddDays(1)), // Tuesday
            Assignment("A", d0.AddDays(2)), // Wednesday
            Assignment("A", d0.AddDays(4)), // Friday (gap on Thursday)
        };
        var context = MakeContext([agent], assignments);

        var scenario = new WarmStartTokenStrategy().BuildScenario(context, new NoDropRandom());
        var byDate = scenario.Tokens.ToDictionary(t => t.Date);

        byDate.Count.ShouldBe(4);
        var block0 = byDate[d0].BlockId;
        byDate[d0.AddDays(1)].BlockId.ShouldBe(block0);
        byDate[d0.AddDays(2)].BlockId.ShouldBe(block0);
        byDate[d0].PositionInBlock.ShouldBe(0);
        byDate[d0.AddDays(1)].PositionInBlock.ShouldBe(1);
        byDate[d0.AddDays(2)].PositionInBlock.ShouldBe(2);

        byDate[d0.AddDays(4)].BlockId.ShouldNotBe(block0);
        byDate[d0.AddDays(4)].PositionInBlock.ShouldBe(0);
    }
}
