// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

/// <summary>
/// Live finding 2026-11-07 (AutoWizard December, seven instead of six days in a row), fixed by owner decision variant A: Wizard 1
/// counts unlocked existing works INSIDE the period (CoreWizardContext.ExistingWorkBlockers) toward MaxConsecutiveDays,
/// as it already did for overlap, minimum pause and weekly rest days. The employee holds existing works Thu d1 to Mon d5
/// that this run does not plan (works on shifts outside the planned set, out-of-group real works, substitutes of a
/// replacement, or clones on slots the run left open); placing Tue d6 and Wed d7 as well would make a chain of 7 with a
/// cap of 6. The dates span two calendar weeks so the weekly rest day guard cannot mask the rule.
/// Only Wizard 1 counts them: the Wizard-4/bitmap path calls the plain PlanConstraintChecker overload, where those
/// works are the scored cells and may have moved, so it must keep counting the cells alone.
/// Known and accepted: a run made of existing works and LOCKED tokens only is still reported by the plan checker, because a
/// locked token is a plan assignment there; no operator can remove it, so the violation is a constant offset of the fitness.
/// </summary>
[TestFixture]
public sealed class Wizard1InPeriodExistingWorkStreakTests
{
    private const string AgentId = "A";
    private const string OtherAgentId = "B";
    private const int NightStartHour = 22;
    private const int NightEndHour = 6;
    private const int BridgeSideDays = 3;
    private const int MaxConsecutiveDays = 6;
    private const int ExistingWorkDays = 5;
    private const int StartHour = 8;
    private const int EndHour = 16;
    private const decimal SlotHours = 8m;
    private const int EarlyKind = 0;

    private static readonly DateOnly PeriodFrom = new(2026, 12, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 12, 31);
    private static readonly DateOnly FirstExistingDay = new(2026, 12, 3);
    private static readonly DateOnly Day6 = FirstExistingDay.AddDays(ExistingWorkDays);
    private static readonly DateOnly Day7 = Day6.AddDays(1);

    [Test]
    public void Control_Stage0_StreakOfPlannedTokens_VetoesTheSeventhDay()
    {
        var assigned = Enumerable.Range(0, ExistingWorkDays + 1)
            .Select(offset => Token(FirstExistingDay.AddDays(offset)))
            .ToList();

        var verdict = new Stage0HardConstraintChecker().Check(MakeAgent(), Slot(Day7), assigned, MakeContext(existing: []));

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe(Stage0RuleNames.MaxConsecutiveDays);
    }

    [Test]
    public void Control_TokenEvolutionRun_TokenOnlyStreak_StaysWithinCap()
    {
        var best = TokenEvolutionLoop.Create().Run(MakeContext(existing: [], SevenShifts()), SmallConfig());

        PlannedDayCount(best).ShouldBeLessThanOrEqualTo(MaxConsecutiveDays);
    }

    [Test]
    public void Stage0_InPeriodExistingWorksCountTowardTheStreak()
    {
        var verdict = new Stage0HardConstraintChecker().Check(MakeAgent(), Slot(Day7), [Token(Day6)], MakeContext(ExistingWorks()));

        verdict.ShouldNotBeNull("5 existing works + d6 + d7 is a chain of 7, the cap is 6");
        verdict!.RuleName.ShouldBe(Stage0RuleNames.MaxConsecutiveDays);
    }

    [Test]
    public void SlotConstraintFilter_InPeriodExistingWorksCountTowardTheBlockLength()
    {
        var valid = SlotConstraintFilter.IsValidAssignment(
            MakeAgent(), Day7, EarlyKind, Guid.NewGuid(), SlotHours, MakeContext(ExistingWorks()), [Token(Day6)],
            At(Day7, StartHour), At(Day7, EndHour), SlotRelaxation.None);

        valid.ShouldBeFalse("5 existing works + d6 + d7 is a chain of 7, the cap is 6");
    }

    [Test]
    public void SlotConstraintFilter_WithoutExistingWorks_TheSeventhDayIsAllowed()
    {
        var valid = SlotConstraintFilter.IsValidAssignment(
            MakeAgent(), Day7, EarlyKind, Guid.NewGuid(), SlotHours, MakeContext(existing: []), [Token(Day6)],
            At(Day7, StartHour), At(Day7, EndHour), SlotRelaxation.None);

        valid.ShouldBeTrue("control: without the existing works d6 and d7 are a block of 2");
    }

    [Test]
    public void Stage0_NightExistingWork_OccupiesItsStartAndItsEndDay()
    {
        // d1-d4 day works, then a night work d5 22:00 - d6 06:00: d6 is occupied by its morning end, so d7 is day 7.
        var existing = Enumerable.Range(0, ExistingWorkDays - 1)
            .Select(offset => ExistingWork(AgentId, FirstExistingDay.AddDays(offset)))
            .Append(NightExistingWork(FirstExistingDay.AddDays(ExistingWorkDays - 1)))
            .ToList();

        var verdict = new Stage0HardConstraintChecker().Check(MakeAgent(), Slot(Day7), [], MakeContext(existing));

        verdict.ShouldNotBeNull("the night work covers d5 and d6, so d1-d7 is a chain of 7");
        verdict!.RuleName.ShouldBe(Stage0RuleNames.MaxConsecutiveDays);
    }

    [Test]
    public void ExistingWorkDayIndex_WorkEndingAtMidnight_OccupiesOnlyItsStartDay()
    {
        var context = MakeContext([new CoreExistingWorkBlocker(AgentId, Day6, At(Day6, EndHour), At(Day7, 0))]);

        context.HasExistingWorkOn(AgentId, Day6).ShouldBeTrue();
        context.HasExistingWorkOn(AgentId, Day7).ShouldBeFalse();
        context.HasExistingWorkOn(OtherAgentId, Day6).ShouldBeFalse();
    }

    [Test]
    public void Stage0_PlannedDayBridgingTwoExistingChains_IsVetoed()
    {
        var bridgeDay = FirstExistingDay.AddDays(BridgeSideDays);

        var verdict = new Stage0HardConstraintChecker().Check(MakeAgent(), Slot(bridgeDay), [], MakeContext(BridgeWorks(bridgeDay)));

        verdict.ShouldNotBeNull("3 existing + the bridge day + 3 existing is a chain of 7");
        verdict!.RuleName.ShouldBe(Stage0RuleNames.MaxConsecutiveDays);
    }

    [Test]
    public void TokenConstraintChecker_PlannedDayBridgingTwoExistingChains_IsReported()
    {
        var bridgeDay = FirstExistingDay.AddDays(BridgeSideDays);
        var scenario = new CoreScenario { Tokens = [Token(bridgeDay)] };

        var violations = new TokenConstraintChecker().Check(scenario, MakeContext(BridgeWorks(bridgeDay)));

        violations.Single(v => v.Kind == ViolationKind.MaxConsecutiveDays).Date.ShouldBe(bridgeDay);
    }

    [Test]
    public void Stage0_ExistingWorksOfAnotherAgent_DoNotCount()
    {
        var othersWorks = Enumerable.Range(0, ExistingWorkDays)
            .Select(offset => ExistingWork(OtherAgentId, FirstExistingDay.AddDays(offset)))
            .ToList();

        var verdict = new Stage0HardConstraintChecker().Check(MakeAgent(), Slot(Day7), [Token(Day6)], MakeContext(othersWorks));

        verdict.ShouldBeNull();
    }

    [Test]
    public void Stage0_BreakDayBetweenExistingWorks_StillInterruptsTheChain()
    {
        // d1-d3 existing, d4 absence, d5-d7 existing, slot d8: the chain is d5-d8 = 4, not 8.
        var breakDay = FirstExistingDay.AddDays(BridgeSideDays);
        var slotDay = breakDay.AddDays(BridgeSideDays + 1);
        var context = MakeContext(BridgeWorks(breakDay), breaks: [new CoreBreakBlocker(AgentId, breakDay, breakDay, "Vacation")]);

        var verdict = new Stage0HardConstraintChecker().Check(MakeAgent(), Slot(slotDay), [], context);

        verdict.ShouldBeNull();
    }

    [Test]
    public void TokenConstraintChecker_ReportsTheStreakAcrossExistingWorks_AtTheFirstPlanDay()
    {
        var scenario = new CoreScenario { Tokens = [Token(Day6), Token(Day7)] };

        var violations = new TokenConstraintChecker().Check(scenario, MakeContext(ExistingWorks()));

        var streak = violations.Single(v => v.Kind == ViolationKind.MaxConsecutiveDays);
        streak.AgentId.ShouldBe(AgentId);
        streak.Date.ShouldBe(Day6, "the repair removes a token of the run, so the violation points at a plan day");
    }

    [Test]
    public void PlanConstraintChecker_StreakWorks_ExistingWorksAloneAreNoViolation()
    {
        var sevenExistingDays = Enumerable.Range(0, MaxConsecutiveDays + 1)
            .Select(offset => FirstExistingDay.AddDays(offset))
            .Select(day => new CoreExistingWorkBlocker(AgentId, day, At(day, StartHour), At(day, EndHour)))
            .ToList();
        var farAway = Assignment(PeriodUntil);

        var violations = new PlanConstraintChecker().Check([farAway], MakeContext(sevenExistingDays), sevenExistingDays);

        violations.ShouldNotContain(v => v.Kind == ViolationKind.MaxConsecutiveDays, "no plan day touches the chain");
    }

    [Test]
    public void PlanConstraintChecker_PlainOverload_UsedByWizard4_IgnoresInPeriodExistingWorks()
    {
        var violations = new PlanConstraintChecker().Check([Assignment(Day6), Assignment(Day7)], MakeContext(ExistingWorks()));

        violations.ShouldNotContain(v => v.Kind == ViolationKind.MaxConsecutiveDays);
    }

    [Test]
    public void PlanConstraintChecker_PlainOverload_UsedByWizard4_CountsAMovedWorkOnlyAtItsNewCell()
    {
        // Wizard 4 fills ExistingWorkBlockers with the works it moves inside the bitmap. The work of d4 moved away,
        // so the cells form runs of 3 and 3 although the stale blockers still cover seven days in a row.
        var originalDays = Enumerable.Range(0, MaxConsecutiveDays + 1).Select(offset => FirstExistingDay.AddDays(offset)).ToList();
        var blockers = originalDays
            .Select(day => new CoreExistingWorkBlocker(AgentId, day, At(day, StartHour), At(day, EndHour)))
            .ToList();
        var cells = originalDays.Where((_, index) => index != 3).Select(BitmapCell).ToList();

        var violations = new PlanConstraintChecker().Check(cells, MakeContext(blockers));

        violations.ShouldNotContain(v => v.Kind == ViolationKind.MaxConsecutiveDays);
    }

    [Test]
    public void PlanConstraintChecker_PlainOverload_StillReportsAStreakOfCells()
    {
        var cells = Enumerable.Range(0, MaxConsecutiveDays + 1).Select(offset => BitmapCell(FirstExistingDay.AddDays(offset))).ToList();

        var violations = new PlanConstraintChecker().Check(cells, MakeContext(existing: []));

        var streak = violations.Single(v => v.Kind == ViolationKind.MaxConsecutiveDays);
        streak.Date.ShouldBe(FirstExistingDay);
    }

    [Test]
    public void SlotConstraintFilter_MaxWorkDaysIdeal_StillReadsTheTokensOnly()
    {
        // 5 existing works + d6 is a block of 6: within the MaxConsecutiveDays cap, above the MaxWorkDays ideal of 5.
        // The ideal is not part of the owner decision and keeps ignoring in-period existing works.
        var valid = SlotConstraintFilter.IsValidAssignment(
            MakeAgent(), Day6, EarlyKind, Guid.NewGuid(), SlotHours, MakeContext(ExistingWorks()), [],
            At(Day6, StartHour), At(Day6, EndHour), SlotRelaxation.None);

        valid.ShouldBeTrue();
    }

    [Test]
    public void TokenEvolutionRun_DoesNotExtendExistingWorksBeyondTheCap()
    {
        var context = MakeContext(ExistingWorks(), [Shift(Day6), Shift(Day7)]);

        var best = TokenEvolutionLoop.Create().Run(context, SmallConfig());

        HoldsBothDays(best).ShouldBeFalse("both slots on top of 5 existing works make a chain of 7");
    }

    private static TokenEvolutionConfig SmallConfig() => new()
    {
        PopulationSize = 8,
        MaxGenerations = 10,
        EarlyStopNoImprovementGenerations = 5,
        EvaluationParallelism = 1,
        RandomSeed = 1,
    };

    private static List<CoreShift> SevenShifts()
        => Enumerable.Range(0, MaxConsecutiveDays + 1)
            .Select(offset => Shift(FirstExistingDay.AddDays(offset)))
            .ToList();

    private static int PlannedDayCount(CoreScenario scenario)
        => scenario.Tokens
            .Where(t => t.AgentId == AgentId && !t.IsLocked)
            .Select(t => t.Date)
            .Distinct()
            .Count();

    private static bool HoldsBothDays(CoreScenario scenario)
    {
        var days = scenario.Tokens
            .Where(t => t.AgentId == AgentId && !t.IsLocked)
            .Select(t => t.Date)
            .ToHashSet();
        return days.Contains(Day6) && days.Contains(Day7);
    }

    private static List<CoreExistingWorkBlocker> ExistingWorks()
        => Enumerable.Range(0, ExistingWorkDays)
            .Select(offset => ExistingWork(AgentId, FirstExistingDay.AddDays(offset)))
            .ToList();

    private static List<CoreExistingWorkBlocker> BridgeWorks(DateOnly gapDay)
        => Enumerable.Range(1, BridgeSideDays)
            .SelectMany(offset => new[] { gapDay.AddDays(-offset), gapDay.AddDays(offset) })
            .Select(day => ExistingWork(AgentId, day))
            .ToList();

    private static CoreExistingWorkBlocker ExistingWork(string agentId, DateOnly day)
        => new(agentId, day, At(day, StartHour), At(day, EndHour));

    private static CoreExistingWorkBlocker NightExistingWork(DateOnly day)
        => new(AgentId, day, At(day, NightStartHour), At(day.AddDays(1), NightEndHour));

    private static CoreAgent MakeAgent() => new(
        Id: AgentId,
        CurrentHours: 0,
        GuaranteedHours: 160,
        MaxConsecutiveDays: MaxConsecutiveDays,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = 160,
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

    private static CoreWizardContext MakeContext(
        IReadOnlyList<CoreExistingWorkBlocker> existing,
        IReadOnlyList<CoreShift>? shifts = null,
        IReadOnlyList<CoreBreakBlocker>? breaks = null) => new()
    {
        PeriodFrom = PeriodFrom,
        PeriodUntil = PeriodUntil,
        Agents = [MakeAgent()],
        Shifts = shifts ?? [],
        BreakBlockers = breaks ?? [],
        ExistingWorkBlockers = existing,
        SchedulingMaxConsecutiveDays = MaxConsecutiveDays,
        SchedulingMaxDailyHours = 10,
    };

    private static CoreShift Shift(DateOnly day)
        => new(Guid.NewGuid().ToString(), "FD", day.ToString("yyyy-MM-dd"), "08:00", "16:00", (double)SlotHours, 1, 0);

    private static CoreShift Slot(DateOnly day) => Shift(day);

    private static DateTime At(DateOnly day, int hour) => day.ToDateTime(new TimeOnly(hour, 0));

    private static CoreToken Token(DateOnly day) => new(
        WorkIds: [],
        ShiftTypeIndex: EarlyKind,
        Date: day,
        TotalHours: SlotHours,
        StartAt: At(day, StartHour),
        EndAt: At(day, EndHour),
        BlockId: Guid.NewGuid(),
        PositionInBlock: 0,
        IsLocked: false,
        LocationContext: null,
        ShiftRefId: Guid.NewGuid(),
        AgentId: AgentId);

    private static AssignmentView BitmapCell(DateOnly day) => Assignment(day) with { BlockId = null };

    private static AssignmentView Assignment(DateOnly day) => new(
        AgentId: AgentId,
        Date: day,
        ShiftRefId: Guid.NewGuid(),
        ShiftTypeIndex: EarlyKind,
        TotalHours: SlotHours,
        StartAt: At(day, StartHour),
        EndAt: At(day, EndHour),
        BlockId: Guid.NewGuid(),
        IsLocked: false);
}
