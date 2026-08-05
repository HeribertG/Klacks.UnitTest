// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints;

/// <summary>
/// The frozen lookups replaced dictionaries that the checks rebuilt for every scored genome, so they
/// have to reproduce the old semantics exactly: first entry wins where the old code took the first
/// match, capacities accumulate per (shift, date), and unparsable shift ids are skipped rather than
/// throwing. The duplicate contract day is the one deliberate difference - the old ToDictionary threw,
/// this build keeps the first entry - and is pinned here so the change cannot be lost silently.
/// </summary>
[TestFixture]
public sealed class EvaluationContextTests
{
    private const string AgentId = "A";
    private static readonly DateOnly Day = new(2026, 6, 1);

    [Test]
    public void For_SameContext_ReturnsTheSameInstance()
    {
        var context = new CoreWizardContext { PeriodFrom = Day, PeriodUntil = Day };

        EvaluationContext.For(context).ShouldBeSameAs(EvaluationContext.For(context));
    }

    [Test]
    public void For_DifferentContexts_ReturnDifferentInstances()
    {
        var first = new CoreWizardContext { PeriodFrom = Day, PeriodUntil = Day };
        var second = new CoreWizardContext { PeriodFrom = Day, PeriodUntil = Day };

        EvaluationContext.For(first).ShouldNotBeSameAs(EvaluationContext.For(second));
    }

    [Test]
    public void ContractDayByAgentDate_DuplicatePair_KeepsTheFirstEntryInsteadOfThrowing()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            ContractDays =
            [
                new CoreContractDay(AgentId, Day, WorksOnDay: true, PerformsShiftWork: true, 1.0, 8, Guid.NewGuid()),
                new CoreContractDay(AgentId, Day, WorksOnDay: false, PerformsShiftWork: true, 1.0, 6, Guid.NewGuid()),
            ],
        };

        EvaluationContext.For(context).ContractDayByAgentDate[(AgentId, Day)].WorksOnDay.ShouldBeTrue();
    }

    [Test]
    public void CommandsByAgentDate_KeepsEveryCommandInInputOrder()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            ScheduleCommands =
            [
                new CoreScheduleCommand(AgentId, Day, ScheduleCommandKeyword.NoEarly),
                new CoreScheduleCommand(AgentId, Day, ScheduleCommandKeyword.NoNight),
            ],
        };

        var commands = EvaluationContext.For(context).CommandsByAgentDate[(AgentId, Day)];

        commands.Select(c => c.Keyword)
            .ShouldBe([ScheduleCommandKeyword.NoEarly, ScheduleCommandKeyword.NoNight]);
    }

    [Test]
    public void BreakBlockersByAgent_GroupsPerAgent()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            BreakBlockers =
            [
                new CoreBreakBlocker(AgentId, Day, Day, "vacation"),
                new CoreBreakBlocker("B", Day, Day, "sick"),
            ],
        };

        var eval = EvaluationContext.For(context);

        eval.BreakBlockersByAgent[AgentId].Count.ShouldBe(1);
        eval.BreakBlockersByAgent["B"].Count.ShouldBe(1);
        eval.BreakBlockersByAgent.ContainsKey("C").ShouldBeFalse();
    }

    [Test]
    public void SlotCapacities_AccumulatePerShiftAndDate_AndSkipUnparsableShifts()
    {
        var shiftId = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Shifts =
            [
                new CoreShift(shiftId.ToString(), "FD", Day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 2, 0),
                new CoreShift(shiftId.ToString(), "FD", Day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 3, 0),
                new CoreShift("not-a-guid", "FD", Day.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            ],
        };

        var eval = EvaluationContext.For(context);

        eval.SlotCapacities[(shiftId, Day)].ShouldBe(5);
        eval.SlotCapacities.Count.ShouldBe(1);
        eval.SlotsByKey[(shiftId, Day)].RequiredAssignments.ShouldBe(2);
    }

    [Test]
    public void AgentsWithMaximumHours_KeepsOnlyCappedAgents_InContextOrder()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [Agent("A", maximumHours: 0), Agent("B", maximumHours: 120), Agent("C", maximumHours: 80)],
        };

        EvaluationContext.For(context).AgentsWithMaximumHours
            .Select(a => a.Id)
            .ShouldBe(["B", "C"]);
    }

    private static CoreAgent Agent(string id, double maximumHours) => new(
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
        MaximumHours = maximumHours,
        PerformsShiftWork = true,
    };
}
