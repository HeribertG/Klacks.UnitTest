// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

/// <summary>
/// Pins the block-ordering term to the rule it is meant to advocate: inside one work package the
/// shift type stays constant, and every day that departs from the package's starting type costs,
/// no matter in which direction. The former implementation only counted backwards steps, so a
/// package that walked early → late → night scored as flawless and rule 7 had no advocate in the
/// fitness at all. The comparison is day based, because two non-overlapping shifts on one calendar
/// day are a permitted split duty and must not be booked as a rotation violation.
/// </summary>
[TestFixture]
public class BlockOrderingDeviationTests
{
    private const int Early = 0;

    private const int Late = 1;

    private const int Night = 2;

    private static readonly DateOnly BlockStart = new(2026, 4, 20);

    [Test]
    public void APureBlockScoresBetterThanAnAscendingMixedBlock()
    {
        var pure = Score([Early, Early, Early]);
        var ascending = Score([Early, Late, Night]);

        pure.ShouldBeGreaterThan(ascending);
    }

    [Test]
    public void AnAscendingMixedBlockIsScoredExactlyLikeADescendingOne()
    {
        Score([Early, Late, Night]).ShouldBe(Score([Night, Late, Early]), 1e-9);
    }

    [Test]
    public void EveryDayDepartingFromTheBlockStartCounts()
    {
        var oneDeviation = Score([Early, Early, Late]);
        var twoDeviations = Score([Early, Late, Late]);

        oneDeviation.ShouldBeGreaterThan(twoDeviations);
    }

    /// <summary>
    /// The reference block deviates on its last day, so both sides score strictly below 1 and the
    /// comparison cannot pass by both being perfect. Adding a second, later shift to the first day
    /// must leave the score untouched: the day keeps the type of its earliest shift.
    /// </summary>
    [Test]
    public void ASplitDutyOnOneDayIsNotARotationViolation()
    {
        var singleShiftDays = BuildScenario(
            [(BlockStart, Early), (BlockStart.AddDays(1), Early), (BlockStart.AddDays(2), Late)]);
        var withSplitDuty = BuildScenario(
            [(BlockStart, Early), (BlockStart, Night), (BlockStart.AddDays(1), Early), (BlockStart.AddDays(2), Late)]);

        var reference = ScoreOf(singleShiftDays);
        reference.ShouldBeLessThan(1.0);
        ScoreOf(withSplitDuty).ShouldBe(reference, 1e-9);
    }

    private static double Score(IReadOnlyList<int> kindsPerDay)
    {
        var days = new List<(DateOnly Date, int Kind)>(kindsPerDay.Count);
        for (var i = 0; i < kindsPerDay.Count; i++)
        {
            days.Add((BlockStart.AddDays(i), kindsPerDay[i]));
        }

        return ScoreOf(BuildScenario(days));
    }

    private static double ScoreOf((CoreScenario Scenario, CoreWizardContext Context) input)
        => TokenFitnessEvaluator.Create(input.Context)
            .EvaluateDetailed(input.Scenario, input.Context)
            .Stage3Components.BlockOrder;

    private static (CoreScenario Scenario, CoreWizardContext Context) BuildScenario(
        IReadOnlyList<(DateOnly Date, int Kind)> days)
    {
        var tokens = days.Select(d => MakeToken(d.Date, d.Kind)).ToList();
        var context = new CoreWizardContext
        {
            PeriodFrom = days[0].Date,
            PeriodUntil = days[^1].Date,
            Agents = [MakeAgent()],
            Shifts = [],
        };

        return (new CoreScenario { Id = "s", Tokens = tokens }, context);
    }

    private static CoreToken MakeToken(DateOnly date, int kind)
    {
        var start = new TimeOnly(6, 0).AddHours(kind * 6);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: kind,
            Date: date,
            TotalHours: 8,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(start).AddHours(8),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: "A");
    }

    private static CoreAgent MakeAgent()
        => new(
            Id: "A",
            CurrentHours: 0,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 24,
            MaxWeeklyHours: 60,
            MaxOptimalGap: 24)
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
}
