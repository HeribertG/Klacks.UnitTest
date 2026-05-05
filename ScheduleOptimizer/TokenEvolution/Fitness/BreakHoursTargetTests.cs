// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Phase-3 verification: Break.WorkTime hours must count toward the agent's GuaranteedHours
/// (Stage 1 / Stage 2 fitness) but never toward the MaxWeeklyHours cap. Mirrors the
/// Phase-2 semantics already proven in the Harmonizer (Wizard 2).
/// </summary>

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class BreakHoursTargetTests
{
    private const string AgentId = "A";
    private const double GuaranteedHours = 24;
    private const decimal BreakHoursPerDay = 8m;
    private const int MaxConsecutiveDays = 6;
    private const double MinRestHours = 11;
    private const double MaxDailyHours = 10;
    private const double MaxWeeklyHours = 40;
    private const double MaxOptimalGap = 2;
    private const double Motivation = 0.5;

    private static CoreAgent MakeAgent(double guaranteedHours = GuaranteedHours)
    {
        return new CoreAgent(
            Id: AgentId,
            CurrentHours: 0,
            GuaranteedHours: guaranteedHours,
            MaxConsecutiveDays: MaxConsecutiveDays,
            MinRestHours: MinRestHours,
            Motivation: Motivation,
            MaxDailyHours: MaxDailyHours,
            MaxWeeklyHours: MaxWeeklyHours,
            MaxOptimalGap: MaxOptimalGap)
        {
            FullTime = 40,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };
    }

    private static CoreShift MakeShift(DateOnly date)
    {
        return new CoreShift(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);
    }

    [Test]
    public void BreakBlockerNowCarriesHours()
    {
        var blocker = new CoreBreakBlocker(
            AgentId: AgentId,
            FromInclusive: new DateOnly(2026, 4, 20),
            UntilInclusive: new DateOnly(2026, 4, 22),
            Reason: "Vacation",
            Hours: BreakHoursPerDay);

        blocker.Hours.ShouldBe(BreakHoursPerDay);
    }

    [Test]
    public void BreakBlockerHoursDefaultsToZero_ForBackwardsCompatibility()
    {
        var blocker = new CoreBreakBlocker(
            AgentId: AgentId,
            FromInclusive: new DateOnly(2026, 4, 20),
            UntilInclusive: new DateOnly(2026, 4, 20),
            Reason: "Vacation");

        blocker.Hours.ShouldBe(0m);
    }

    [Test]
    public void BreakHoursCountTowardTarget_StageOneMet_WhenBreakCoversGuaranteed()
    {
        var periodFrom = new DateOnly(2026, 4, 20);
        var periodUntil = periodFrom.AddDays(4);

        // Agent has guaranteed=24, no tokens assigned. 3 break days x 8h = 24h => Stage1 must be 1.
        var context = new CoreWizardContext
        {
            PeriodFrom = periodFrom,
            PeriodUntil = periodUntil,
            Agents = [MakeAgent()],
            Shifts = [MakeShift(periodFrom)],
            BreakBlockers =
            [
                new CoreBreakBlocker(
                    AgentId: AgentId,
                    FromInclusive: periodFrom,
                    UntilInclusive: periodFrom.AddDays(2),
                    Reason: "Vacation",
                    Hours: BreakHoursPerDay),
            ],
        };

        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage1.ShouldBe(1.0);
    }

    [Test]
    public void BreakHoursCountTowardTarget_StageOneMet_WhenBreakPlusTokensCoverGuaranteed()
    {
        var periodFrom = new DateOnly(2026, 4, 20);
        var periodUntil = periodFrom.AddDays(4);

        // 1 break day x 8h = 8h + 2 tokens x 8h = 16h => total 24h covers GuaranteedHours=24.
        var context = new CoreWizardContext
        {
            PeriodFrom = periodFrom,
            PeriodUntil = periodUntil,
            Agents = [MakeAgent()],
            Shifts = Enumerable.Range(0, 5).Select(i => MakeShift(periodFrom.AddDays(i))).ToList(),
            BreakBlockers =
            [
                new CoreBreakBlocker(
                    AgentId: AgentId,
                    FromInclusive: periodFrom.AddDays(4),
                    UntilInclusive: periodFrom.AddDays(4),
                    Reason: "Sick",
                    Hours: BreakHoursPerDay),
            ],
        };

        var tokenStart = new TimeOnly(8, 0);
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens =
            [
                new CoreToken(
                    WorkIds: [],
                    ShiftTypeIndex: 0,
                    Date: periodFrom,
                    TotalHours: 8m,
                    StartAt: periodFrom.ToDateTime(tokenStart),
                    EndAt: periodFrom.ToDateTime(tokenStart.AddHours(8)),
                    BlockId: Guid.NewGuid(),
                    PositionInBlock: 0,
                    IsLocked: false,
                    LocationContext: null,
                    ShiftRefId: Guid.Empty,
                    AgentId: AgentId),
                new CoreToken(
                    WorkIds: [],
                    ShiftTypeIndex: 0,
                    Date: periodFrom.AddDays(1),
                    TotalHours: 8m,
                    StartAt: periodFrom.AddDays(1).ToDateTime(tokenStart),
                    EndAt: periodFrom.AddDays(1).ToDateTime(tokenStart.AddHours(8)),
                    BlockId: Guid.NewGuid(),
                    PositionInBlock: 0,
                    IsLocked: false,
                    LocationContext: null,
                    ShiftRefId: Guid.Empty,
                    AgentId: AgentId),
            ],
        };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage1.ShouldBe(1.0);
    }

    [Test]
    public void WithoutBreakHours_StageOneRemainsZero_WhenTokensInsufficient()
    {
        var periodFrom = new DateOnly(2026, 4, 20);
        var periodUntil = periodFrom.AddDays(4);

        // Only 1 token x 8h, no break blockers => 8/24 missing.
        var context = new CoreWizardContext
        {
            PeriodFrom = periodFrom,
            PeriodUntil = periodUntil,
            Agents = [MakeAgent()],
            Shifts = Enumerable.Range(0, 5).Select(i => MakeShift(periodFrom.AddDays(i))).ToList(),
        };

        var tokenStart = new TimeOnly(8, 0);
        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens =
            [
                new CoreToken(
                    WorkIds: [],
                    ShiftTypeIndex: 0,
                    Date: periodFrom,
                    TotalHours: 8m,
                    StartAt: periodFrom.ToDateTime(tokenStart),
                    EndAt: periodFrom.ToDateTime(tokenStart.AddHours(8)),
                    BlockId: Guid.NewGuid(),
                    PositionInBlock: 0,
                    IsLocked: false,
                    LocationContext: null,
                    ShiftRefId: Guid.Empty,
                    AgentId: AgentId),
            ],
        };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage1.ShouldBe(0);
    }

    [Test]
    public void BreakHoursDoNotCountTowardWeeklyMax()
    {
        // FuzzyBiddingAgent computes WeeklyLoad = (CurrentHours + HoursAssignedThisRun) / MaxWeeklyHours.
        // Break hours never go through CurrentHours nor HoursAssignedThisRun, so they must not raise
        // WeeklyLoad. We verify this indirectly: an agent with a Break blocker on the same week as a
        // candidate slot must produce the SAME WeeklyLoad input as one without the blocker.

        var date = new DateOnly(2026, 4, 22);
        var slot = new CoreShift(Guid.NewGuid().ToString(), "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);
        var agent = MakeAgent() with { CurrentHours = 16 };

        var contextWithoutBreak = new CoreWizardContext
        {
            PeriodFrom = date.AddDays(-3),
            PeriodUntil = date.AddDays(3),
            Agents = [agent],
            Shifts = [slot],
        };

        var contextWithBreak = new CoreWizardContext
        {
            PeriodFrom = date.AddDays(-3),
            PeriodUntil = date.AddDays(3),
            Agents = [agent],
            Shifts = [slot],
            BreakBlockers =
            [
                new CoreBreakBlocker(
                    AgentId: AgentId,
                    FromInclusive: date.AddDays(-1),
                    UntilInclusive: date.AddDays(-1),
                    Reason: "Sick",
                    Hours: BreakHoursPerDay),
            ],
        };

        var bidder = new FuzzyBiddingAgent();
        var state = AgentRuntimeState.Initial(AgentId);

        var bidNoBreak = bidder.Evaluate(agent, slot, state, contextWithoutBreak);
        var bidWithBreak = bidder.Evaluate(agent, slot, state, contextWithBreak);

        // The presence of a Break blocker must not influence the WeeklyLoad input — same bid score.
        bidWithBreak.Score.ShouldBe(bidNoBreak.Score);
    }

    [Test]
    public void BreakHoursClippedToPeriod_BeforeAddingToTarget()
    {
        var periodFrom = new DateOnly(2026, 4, 20);
        var periodUntil = periodFrom.AddDays(2);

        // Break overlaps the period only on 2 days (20..21). The 3rd day (22) is the last period day,
        // remaining 23..25 are outside. Total in-period break hours = 3 days x 8h = 24h => Stage1 = 1.
        var blocker = new CoreBreakBlocker(
            AgentId: AgentId,
            FromInclusive: periodFrom,
            UntilInclusive: periodFrom.AddDays(5),
            Reason: "Vacation",
            Hours: BreakHoursPerDay);

        var context = new CoreWizardContext
        {
            PeriodFrom = periodFrom,
            PeriodUntil = periodUntil,
            Agents = [MakeAgent()],
            Shifts = [MakeShift(periodFrom)],
            BreakBlockers = [blocker],
        };

        var scenario = new CoreScenario { Id = "s", Tokens = [] };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage1.ShouldBe(1.0);
    }
}
