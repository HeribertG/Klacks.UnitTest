// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

[TestFixture]
public class GreedyTokenStrategyTests
{
    private static CoreAgent MakeAgent(string id, double fullTime, double currentHours = 0)
    {
        return new CoreAgent(
            Id: id,
            CurrentHours: currentHours,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = fullTime,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };
    }

    private static CoreShift MakeShift(DateOnly date, string id)
    {
        return new CoreShift(id, "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);
    }

    [Test]
    public void BuildScenario_FullTimeAgent_GetsHoursBeforePartTimeAgent()
    {
        var fullTime = MakeAgent("FT", fullTime: 40);
        var partTime = MakeAgent("PT", fullTime: 20);

        var date1 = new DateOnly(2026, 4, 20);
        var date2 = new DateOnly(2026, 4, 21);

        var shifts = new[]
        {
            MakeShift(date1, Guid.NewGuid().ToString()),
            MakeShift(date2, Guid.NewGuid().ToString()),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date1,
            PeriodUntil = date2,
            Agents = [fullTime, partTime],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.ShouldNotBeEmpty();
        scenario.Tokens.ShouldAllBe(t => t.AgentId == "FT");
    }

    [Test]
    public void BuildScenario_PreservesLockedTokens()
    {
        var agent = MakeAgent("A", fullTime: 40);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [],
            LockedWorks =
            [
                new CoreLockedWork(
                    WorkId: "w1",
                    AgentId: "A",
                    Date: date,
                    ShiftTypeIndex: 0,
                    TotalHours: 8m,
                    StartAt: date.ToDateTime(new TimeOnly(8, 0)),
                    EndAt: date.ToDateTime(new TimeOnly(16, 0)),
                    ShiftRefId: Guid.NewGuid(),
                    LocationContext: null),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy().BuildScenario(context, new Random(0));

        scenario.Tokens.Count().ShouldBe(1);
        scenario.Tokens[0].IsLocked.ShouldBeTrue();
    }

    [Test]
    public void BuildScenario_EnforcesFullSlotCoverage_EvenBeyondTarget()
    {
        var agent = MakeAgent("A", fullTime: 16);
        var date1 = new DateOnly(2026, 4, 20);
        var date2 = new DateOnly(2026, 4, 21);
        var date3 = new DateOnly(2026, 4, 22);

        var shifts = new[]
        {
            MakeShift(date1, Guid.NewGuid().ToString()),
            MakeShift(date2, Guid.NewGuid().ToString()),
            MakeShift(date3, Guid.NewGuid().ToString()),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date1,
            PeriodUntil = date3,
            Agents = [agent],
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.Where(t => !t.IsLocked).Count().ShouldBe(3);
    }
    [Test]
    public void BuildScenario_ForcedCoverage_NeverDoubleBooksTheOnlyAgent()
    {
        // Two shifts overlap on the same day and only one agent exists. Forced coverage may leave the
        // second slot empty, but it must never put the same person on both: a plan with a gap is a plan,
        // a plan with one human in two places is not.
        var agent = MakeAgent("A", fullTime: 8);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts =
            [
                MakeShift(date, Guid.NewGuid().ToString()),
                new CoreShift(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), "12:00", "20:00", 8, 1, 0),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        var assigned = scenario.Tokens.Where(t => t.AgentId == "A").OrderBy(t => t.StartAt).ToList();
        for (var i = 1; i < assigned.Count; i++)
        {
            assigned[i].StartAt.ShouldBeGreaterThanOrEqualTo(assigned[i - 1].EndAt);
        }
    }

    [Test]
    public void BuildScenario_ForcedCoverage_NeverCollidesWithAnExistingWork()
    {
        var agent = MakeAgent("A", fullTime: 8);
        var date = new DateOnly(2026, 4, 20);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [MakeShift(date, Guid.NewGuid().ToString())],
            ExistingWorkBlockers =
            [
                new CoreExistingWorkBlocker("A", date, date.ToDateTime(new TimeOnly(7, 0)), date.ToDateTime(new TimeOnly(15, 0))),
            ],
            SchedulingMaxConsecutiveDays = 6,
        };

        var scenario = new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0));

        scenario.Tokens.ShouldNotContain(t => t.AgentId == "A" && !t.IsLocked);
    }

    private static readonly DateOnly VetoDate = new(2026, 4, 20);

    private static CoreShift MakeSlot(string id, string start, string end)
        => new(id, "FD", VetoDate.ToString("yyyy-MM-dd"), start, end, 8, 1, 0);

    private static CoreWizardContext SingleSlotContext(
        CoreAgent agent,
        CoreShift slot,
        IReadOnlyList<CoreScheduleCommand>? commands = null,
        IReadOnlyList<CoreShiftPreference>? preferences = null,
        IReadOnlyList<CoreBreakBlocker>? breaks = null,
        IReadOnlyList<CoreContractDay>? contractDays = null,
        IReadOnlyList<CoreRestrictedTimeWindow>? windows = null) => new()
    {
        PeriodFrom = VetoDate,
        PeriodUntil = VetoDate,
        Agents = [agent],
        Shifts = [slot],
        ScheduleCommands = commands ?? [],
        ShiftPreferences = preferences ?? [],
        BreakBlockers = breaks ?? [],
        ContractDays = contractDays ?? [],
        RestrictedTimeWindows = windows ?? [],
        SchedulingMaxConsecutiveDays = 6,
    };

    private static IReadOnlyList<CoreToken> PlannedTokens(CoreWizardContext context)
        => new GreedyTokenStrategy { Epsilon = 0 }.BuildScenario(context, new Random(0)).Tokens
            .Where(t => !t.IsLocked)
            .ToList();

    [Test]
    public void ForcedCoverage_RespectsFreeKeyword()
    {
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(Guid.NewGuid().ToString(), "06:00", "14:00"), commands: [new CoreScheduleCommand("A", VetoDate, ScheduleCommandKeyword.Free)]);

        PlannedTokens(context).ShouldBeEmpty("a FREE day is absolute: forced coverage must leave the slot open");
    }

    [Test]
    public void ForcedCoverage_RespectsOnlyEarly_OnANightSlot()
    {
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(Guid.NewGuid().ToString(), "23:00", "07:00"), commands: [new CoreScheduleCommand("A", VetoDate, ScheduleCommandKeyword.OnlyEarly)]);

        PlannedTokens(context).ShouldBeEmpty("OnlyEarly forbids a night shift even under forced coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsNoNight()
    {
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(Guid.NewGuid().ToString(), "23:00", "07:00"), commands: [new CoreScheduleCommand("A", VetoDate, ScheduleCommandKeyword.NoNight)]);

        PlannedTokens(context).ShouldBeEmpty("NoNight forbids a night shift even under forced coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsShiftBlacklist()
    {
        var shiftId = Guid.NewGuid();
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(shiftId.ToString(), "06:00", "14:00"), preferences: [new CoreShiftPreference("A", shiftId, ShiftPreferenceKind.Blacklist)]);

        PlannedTokens(context).ShouldBeEmpty("a blacklisted shift is never assigned, not even to force coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsBreakBlocker()
    {
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(Guid.NewGuid().ToString(), "06:00", "14:00"), breaks: [new CoreBreakBlocker("A", VetoDate, VetoDate, "Vacation", 8m)]);

        PlannedTokens(context).ShouldBeEmpty("an agent on vacation is never assigned, not even to force coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsContractDayWithoutWork()
    {
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(Guid.NewGuid().ToString(), "06:00", "14:00"), contractDays: [new CoreContractDay("A", VetoDate, WorksOnDay: false, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 0, ContractId: Guid.NewGuid())]);

        PlannedTokens(context).ShouldBeEmpty("a day without an active contract is not workable, not even to force coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsStaticWeekdayFlag()
    {
        var agent = MakeAgent("A", fullTime: 8) with { WorkOnMonday = false };
        var context = SingleSlotContext(agent, MakeSlot(Guid.NewGuid().ToString(), "06:00", "14:00"));

        PlannedTokens(context).ShouldBeEmpty("a weekday the agent does not work is not workable, not even to force coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsShiftWorkFlag()
    {
        var agent = MakeAgent("A", fullTime: 8) with { PerformsShiftWork = false };
        var context = SingleSlotContext(agent, MakeSlot(Guid.NewGuid().ToString(), "23:00", "07:00"));

        PlannedTokens(context).ShouldBeEmpty("a non-shift worker never gets a night shift, not even to force coverage");
    }

    [Test]
    public void ForcedCoverage_RespectsRestrictedTimeWindow()
    {
        var shiftId = Guid.NewGuid();
        var context = SingleSlotContext(MakeAgent("A", fullTime: 8), MakeSlot(shiftId.ToString(), "06:00", "14:00"), windows: [new CoreRestrictedTimeWindow(1, 1, 12, 31, 10 * 60, 12 * 60, new HashSet<Guid> { shiftId })]);

        PlannedTokens(context).ShouldBeEmpty("a restricted time window is a hard veto, also under forced coverage");
    }

    [Test]
    public void ForcedCoverage_StillRelaxesMaximumHours()
    {
        var agent = MakeAgent("A", fullTime: 8) with { MaximumHours = 4 };
        var context = SingleSlotContext(agent, MakeSlot(Guid.NewGuid().ToString(), "06:00", "14:00"));

        PlannedTokens(context).Count.ShouldBe(1, "MaximumHours stays relaxable: coverage beats the hour cap");
    }
}
