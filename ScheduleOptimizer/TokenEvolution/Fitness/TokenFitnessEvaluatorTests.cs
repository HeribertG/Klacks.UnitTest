// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class TokenFitnessEvaluatorTests
{
    private static CoreAgent MakeAgent(
        string id,
        double fullTime,
        double guaranteed = 0,
        double currentHours = 0,
        double minimumHours = 0)
    {
        return new CoreAgent(
            Id: id,
            CurrentHours: currentHours,
            GuaranteedHours: guaranteed,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = fullTime,
            MinimumHours = minimumHours,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };
    }

    private static CoreShift MakeShift(DateOnly date, Guid? id = null)
    {
        return new CoreShift((id ?? Guid.NewGuid()).ToString(), "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0);
    }

    private static IList<(CoreShift Shift, CoreToken Token)> MakeMatchingPairs(
        string agentId, DateOnly startDate, int count, int shiftTypeIndex = 0, decimal hours = 8)
    {
        var pairs = new List<(CoreShift Shift, CoreToken Token)>(count);
        for (var i = 0; i < count; i++)
        {
            var day = startDate.AddDays(i);
            var shiftRefId = Guid.NewGuid();
            var shift = MakeShift(day, shiftRefId);
            var token = MakeToken(agentId, day, shiftTypeIndex, hours) with { ShiftRefId = shiftRefId };
            pairs.Add((shift, token));
        }

        return pairs;
    }

    private static CoreToken MakeToken(string agentId, DateOnly date, int shiftTypeIndex = 0, decimal hours = 8)
    {
        var start = new TimeOnly(8, 0);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: shiftTypeIndex,
            Date: date,
            TotalHours: hours,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(start.AddHours((double)hours)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: agentId);
    }

    [Test]
    public void Evaluate_FillsAllFitnessStagesAndFitnessAggregate()
    {
        var date = new DateOnly(2026, 4, 20);
        var pairs = MakeMatchingPairs("A", date, 5);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(4),
            Agents = [MakeAgent("A", fullTime: 40)],
            Shifts = pairs.Select(p => p.Shift).ToList(),
        };

        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = pairs.Select(p => p.Token).ToList(),
        };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage0.Should().Be(0);
        scenario.FitnessStage1.Should().Be(1);
        scenario.FitnessStage2.Should().BeInRange(0.99, 1.0);
    }

    [Test]
    public void Compare_Stage0ViolationsWin_EvenIfOtherStagesBetter()
    {
        var a = new CoreScenario { Id = "a", FitnessStage0 = 0, FitnessStage1 = 0, FitnessStage2 = 0 };
        var b = new CoreScenario { Id = "b", FitnessStage0 = 1, FitnessStage1 = 1, FitnessStage2 = 1 };

        var sut = new TokenFitnessEvaluator(
            new Klacks.ScheduleOptimizer.TokenEvolution.Constraints.TokenConstraintChecker(),
            new Dictionary<string, double>(),
            new List<CoreAgent>());

        sut.Compare(a, b).Should().BeLessThan(0);
    }

    [Test]
    public void Compare_Stage1HigherWins_WhenStage0Equal()
    {
        var a = new CoreScenario { Id = "a", FitnessStage0 = 0, FitnessStage1 = 0.9, FitnessStage2 = 0 };
        var b = new CoreScenario { Id = "b", FitnessStage0 = 0, FitnessStage1 = 0.5, FitnessStage2 = 1 };

        var sut = new TokenFitnessEvaluator(
            new Klacks.ScheduleOptimizer.TokenEvolution.Constraints.TokenConstraintChecker(),
            new Dictionary<string, double>(),
            new List<CoreAgent>());

        sut.Compare(a, b).Should().BeLessThan(0);
    }

    [Test]
    public void Evaluate_GuaranteedHoursMet_SetsStage1ToOne()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(4),
            Agents = [MakeAgent("A", fullTime: 40, guaranteed: 16)],
            Shifts = Enumerable.Range(0, 5).Select(i => MakeShift(date.AddDays(i))).ToList(),
        };

        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = [MakeToken("A", date), MakeToken("A", date.AddDays(1))],
        };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage1.Should().Be(1);
    }

    [Test]
    public void Evaluate_GuaranteedHoursMissed_SetsStage1ToZero()
    {
        var date = new DateOnly(2026, 4, 20);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(4),
            Agents = [MakeAgent("A", fullTime: 40, guaranteed: 24)],
            Shifts = Enumerable.Range(0, 5).Select(i => MakeShift(date.AddDays(i))).ToList(),
        };

        var scenario = new CoreScenario { Id = "s", Tokens = [MakeToken("A", date)] };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(scenario, context);

        scenario.FitnessStage1.Should().Be(0);
    }

    [Test]
    public void Evaluate_Stage3BlacklistPreference_ReducesStage3()
    {
        var date = new DateOnly(2026, 4, 20);
        var shiftRef = Guid.NewGuid();
        var agent = MakeAgent("A", fullTime: 40);

        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date,
            Agents = [agent],
            Shifts = [new CoreShift(shiftRef.ToString(), "FD", date.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0)],
            ShiftPreferences = [new CoreShiftPreference("A", shiftRef, ShiftPreferenceKind.Blacklist)],
        };

        var goodScenario = new CoreScenario { Id = "good", Tokens = [MakeToken("A", date)] };
        var blacklistedScenario = new CoreScenario
        {
            Id = "bad",
            Tokens = [MakeToken("A", date) with { ShiftRefId = shiftRef }],
        };

        var sut = TokenFitnessEvaluator.Create(context);
        sut.Evaluate(goodScenario, context);
        sut.Evaluate(blacklistedScenario, context);

        blacklistedScenario.FitnessStage3.Should().BeLessThan(goodScenario.FitnessStage3);
    }
}
