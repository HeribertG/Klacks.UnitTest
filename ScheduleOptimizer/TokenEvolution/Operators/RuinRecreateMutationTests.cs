// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// M8 ruin and recreate: a contiguous calendar window loses its non-locked tokens and the
/// package-aware coverage sweep rebuilds it; only the end state is ever compared. The guards
/// prove full coverage after the rebuild, locked survival inside the window and seeded replay.
/// </summary>
[TestFixture]
public class RuinRecreateMutationTests
{
    private const int DayCount = 8;

    private static CoreAgent MakeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 80,
        MaxOptimalGap: 2)
    {
        FullTime = 80,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static (CoreWizardContext Context, CoreScenario Scenario, List<CoreShift> Shifts) BuildFixture(
        bool lockOneInsideEveryWindow = false)
    {
        var start = new DateOnly(2026, 4, 20);
        var shifts = Enumerable.Range(0, DayCount)
            .Select(i => new CoreShift(
                Guid.NewGuid().ToString(), "FD", start.AddDays(i).ToString("yyyy-MM-dd"),
                "08:00", "16:00", 8, 1, 0))
            .ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = start,
            PeriodUntil = start.AddDays(DayCount - 1),
            Agents = [MakeAgent("A"), MakeAgent("B")],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
            SchedulingMaxDailyHours = 10,
        };

        var tokens = shifts.Select((s, i) => new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: start.AddDays(i),
            TotalHours: 8m,
            StartAt: start.AddDays(i).ToDateTime(new TimeOnly(8, 0)),
            EndAt: start.AddDays(i).ToDateTime(new TimeOnly(16, 0)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: lockOneInsideEveryWindow && i == DayCount / 2,
            LocationContext: null,
            ShiftRefId: Guid.Parse(s.Id),
            AgentId: i % 2 == 0 ? "A" : "B")).ToList();
        var scenario = new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = tokens };
        return (context, scenario, shifts);
    }

    private static RuinRecreateMutation Sut() =>
        new(new TokenRepair(new Klacks.ScheduleOptimizer.TokenEvolution.Constraints.TokenConstraintChecker()));

    [Test]
    public void Apply_RebuildsFullCoverageAfterTheRuin()
    {
        var (context, scenario, shifts) = BuildFixture();

        var result = Sut().Apply(
            new TokenOperatorContext(scenario, null, context, new Random(42)),
            windowMinDays: 5,
            windowMaxDays: 10);

        var covered = result.Tokens.Select(t => (t.ShiftRefId, t.Date)).ToHashSet();
        foreach (var shift in shifts)
        {
            covered.ShouldContain(
                (Guid.Parse(shift.Id), DateOnly.Parse(shift.Date)),
                "the sweep must rebuild every slot the ruin emptied");
        }
    }

    [Test]
    public void Apply_NeverRemovesALockedToken()
    {
        // The window spans at least 5 of 8 days, so the locked mid-period token always lies inside.
        var (context, scenario, _) = BuildFixture(lockOneInsideEveryWindow: true);
        var locked = scenario.Tokens.Single(t => t.IsLocked);

        var result = Sut().Apply(
            new TokenOperatorContext(scenario, null, context, new Random(42)),
            windowMinDays: 5,
            windowMaxDays: 10);

        result.Tokens.ShouldContain(
            t => t.IsLocked && t.Date == locked.Date && t.AgentId == locked.AgentId,
            "a locked token inside the ruined window must survive verbatim");
    }

    [Test]
    public void Apply_SeededRunsReplayIdentically()
    {
        var (context, scenario, _) = BuildFixture();
        static List<(string, DateOnly, Guid)> KeyOf(CoreScenario s) => s.Tokens
            .Select(t => (t.AgentId, t.Date, t.ShiftRefId))
            .OrderBy(x => x.AgentId).ThenBy(x => x.Date).ThenBy(x => x.ShiftRefId)
            .ToList();

        var first = Sut().Apply(new TokenOperatorContext(scenario, null, context, new Random(7)), 5, 10);
        var second = Sut().Apply(new TokenOperatorContext(scenario, null, context, new Random(7)), 5, 10);

        KeyOf(second).ShouldBe(KeyOf(first), "the same seed must ruin and rebuild the same window identically");
    }
}
