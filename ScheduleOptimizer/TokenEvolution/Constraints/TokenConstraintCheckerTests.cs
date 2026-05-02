// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Constraints;

[TestFixture]
public class TokenConstraintCheckerTests
{
    private static CoreAgent MakeAgent(string id, bool shiftWork = true, double maxHours = 0, double currentHours = 0)
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
            MaximumHours = maxHours,
            PerformsShiftWork = shiftWork,
        };
    }

    private static CoreToken MakeToken(
        string agentId,
        DateOnly date,
        int shiftTypeIndex = 0,
        decimal hours = 8,
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        Guid? blockId = null,
        bool isLocked = false)
    {
        var actualStart = startTime ?? new TimeOnly(8, 0);
        var actualEnd = endTime ?? actualStart.AddHours((double)hours);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: shiftTypeIndex,
            Date: date,
            TotalHours: hours,
            StartAt: date.ToDateTime(actualStart),
            EndAt: date.ToDateTime(actualEnd),
            BlockId: blockId ?? Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: isLocked,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: agentId);
    }

    [Test]
    public void Check_EmptyScenario_ReturnsNoViolations()
    {
        var context = new CoreWizardContext
        {
            Agents = [MakeAgent("A")],
            PeriodFrom = new DateOnly(2026, 4, 20),
            PeriodUntil = new DateOnly(2026, 4, 22),
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Check_WorkOnDayViolation_WhenContractForbidsWeekday()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            ContractDays = [new CoreContractDay("A", date, WorksOnDay: false, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.NewGuid())],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date)] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.WorkOnDayViolation).ShouldBe(1);
    }

    [Test]
    public void Check_PerformsShiftWorkViolation_ForNonShiftAgentOnLateShift()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A", shiftWork: false)],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date, shiftTypeIndex: 1)] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.PerformsShiftWorkViolation).ShouldBe(1);
    }

    [Test]
    public void Check_PerDayKeywordViolation_WhenFreeCommandIsViolated()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            ScheduleCommands = [new CoreScheduleCommand("A", date, ScheduleCommandKeyword.Free)],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date)] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.PerDayKeywordViolation).ShouldBe(1);
    }

    [Test]
    public void Check_BreakBlockerViolation_WhenTokenDateIsBlocked()
    {
        var date = new DateOnly(2026, 4, 22);
        var context = new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 4, 20),
            PeriodUntil = new DateOnly(2026, 4, 25),
            Agents = [MakeAgent("A")],
            BreakBlockers = [new CoreBreakBlocker("A", new DateOnly(2026, 4, 21), new DateOnly(2026, 4, 23), "Vacation")],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date)] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.BreakBlockerViolation).ShouldBe(1);
    }

    [Test]
    public void Check_MaxConsecutiveDays_WhenBlockExceedsCap()
    {
        var blockId = Guid.NewGuid();
        var start = new DateOnly(2026, 4, 20);
        var tokens = Enumerable.Range(0, 7)
            .Select(i => MakeToken("A", start.AddDays(i), blockId: blockId))
            .ToList();

        var context = new CoreWizardContext
        {
            PeriodFrom = start,
            PeriodUntil = start.AddDays(6),
            Agents = [MakeAgent("A")],
            SchedulingMaxConsecutiveDays = 6,
        };
        var scenario = new CoreScenario { Id = "s", Tokens = tokens };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.MaxConsecutiveDays).ShouldBe(1);
    }

    [Test]
    public void Check_MinPauseHours_WhenGapIsTooShort()
    {
        var date = new DateOnly(2026, 4, 20);
        var nextDate = date.AddDays(1);
        var tokens = new List<CoreToken>
        {
            MakeToken("A", date, hours: 8, startTime: new TimeOnly(14, 0), endTime: new TimeOnly(22, 0)),
            MakeToken("A", nextDate, hours: 8, startTime: new TimeOnly(6, 0), endTime: new TimeOnly(14, 0)),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = nextDate,
            Agents = [MakeAgent("A")],
            SchedulingMinPauseHours = 11,
        };
        var scenario = new CoreScenario { Id = "s", Tokens = tokens };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.MinPauseHours).ShouldBe(1);
    }

    [Test]
    public void Check_MaxDailyHours_WhenTokensExceedDailyCap()
    {
        var date = new DateOnly(2026, 4, 20);
        var tokens = new List<CoreToken>
        {
            MakeToken("A", date, hours: 6, startTime: new TimeOnly(6, 0), endTime: new TimeOnly(12, 0)),
            MakeToken("A", date, hours: 6, startTime: new TimeOnly(14, 0), endTime: new TimeOnly(20, 0)),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            SchedulingMaxDailyHours = 10,
        };
        var scenario = new CoreScenario { Id = "s", Tokens = tokens };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.ShouldContain(v => v.Kind == ViolationKind.MaxDailyHours);
    }

    [Test]
    public void Check_MaximumHoursExceeded_WhenTotalOverflowsAgentCap()
    {
        var date = new DateOnly(2026, 4, 20);
        var tokens = Enumerable.Range(0, 3)
            .Select(i => MakeToken("A", date.AddDays(i), hours: 10))
            .ToList();

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(2),
            Agents = [MakeAgent("A", maxHours: 20, currentHours: 5)],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = tokens };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Count(v => v.Kind == ViolationKind.MaximumHoursExceeded).ShouldBe(1);
    }

    [Test]
    public void Check_MultipleViolationTypes_AreAllReported()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A", shiftWork: false)],
            ContractDays = [new CoreContractDay("A", date, WorksOnDay: false, PerformsShiftWork: false, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.NewGuid())],
            ScheduleCommands = [new CoreScheduleCommand("A", date, ScheduleCommandKeyword.Free)],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date, shiftTypeIndex: 1)] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        var kinds = result.Select(v => v.Kind).ToList();
        kinds.ShouldContain(ViolationKind.WorkOnDayViolation);
        kinds.ShouldContain(ViolationKind.PerformsShiftWorkViolation);
        kinds.ShouldContain(ViolationKind.PerDayKeywordViolation);
    }

    [Test]
    public void CountViolations_ReturnsCheckCount()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            ScheduleCommands = [new CoreScheduleCommand("A", date, ScheduleCommandKeyword.Free)],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date)] };

        var checker = new TokenConstraintChecker();
        checker.CountViolations(scenario, context).ShouldBe(checker.Check(scenario, context).Count);
    }

    [Test]
    public void Check_UnderSupply_ReportsOneViolationPerMissingSlot()
    {
        var date = new DateOnly(2026, 4, 20);
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            Shifts =
            [
                new CoreShift(shiftA.ToString(), "Shift-A", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
                new CoreShift(shiftB.ToString(), "Shift-B", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            ],
        };
        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.Where(v => v.Kind == ViolationKind.UnderSupply).Count().ShouldBe(2);
        result.Where(v => v.Kind == ViolationKind.UnderSupply)
            .Select(v => v.ShiftRefId).ToArray()
            .ShouldBeEquivalentTo(new Guid?[] { shiftA, shiftB });
    }

    [Test]
    public void Check_UnderSupply_NoViolation_WhenAllSlotsAreCovered()
    {
        var date = new DateOnly(2026, 4, 20);
        var shiftA = Guid.NewGuid();
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [MakeAgent("A")],
            Shifts =
            [
                new CoreShift(shiftA.ToString(), "Shift-A", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            ],
        };
        var token = MakeToken("A", date) with { ShiftRefId = shiftA };
        var scenario = new CoreScenario { Id = "s", Tokens = [token] };

        var result = new TokenConstraintChecker().Check(scenario, context);

        result.ShouldNotContain(v => v.Kind == ViolationKind.UnderSupply);
    }
}
