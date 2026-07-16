// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Fitness;

[TestFixture]
public class EvaluateDetailedTests
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

    private static (CoreScenario Scenario, CoreWizardContext Context) MakeSample()
    {
        var date = new DateOnly(2026, 4, 20);
        var pairs = MakeMatchingPairs("A", date, 5);
        var context = new CoreWizardContext
        {
            PeriodFrom = date,
            PeriodUntil = date.AddDays(4),
            Agents = [MakeAgent("A", fullTime: 40, minimumHours: 20)],
            Shifts = pairs.Select(p => p.Shift).ToList(),
        };

        var scenario = new CoreScenario
        {
            Id = "s",
            Tokens = pairs.Select(p => p.Token).ToList(),
        };

        return (scenario, context);
    }

    [Test]
    public void EvaluateDetailed_ReturnsSameStageAggregatesAsEvaluate()
    {
        var (scenarioA, contextA) = MakeSample();
        var (scenarioB, contextB) = MakeSample();

        var sut = TokenFitnessEvaluator.Create(contextA);
        sut.Evaluate(scenarioA, contextA);
        var detailed = sut.EvaluateDetailed(scenarioB, contextB);

        detailed.Stage0.ShouldBe(scenarioA.FitnessStage0);
        detailed.Stage1.ShouldBe(scenarioA.FitnessStage1);
        detailed.Stage2.ShouldBe(scenarioA.FitnessStage2);
        detailed.Stage3.ShouldBe(scenarioA.FitnessStage3);
        detailed.Stage4.ShouldBe(scenarioA.FitnessStage4);
    }

    [Test]
    public void EvaluateDetailed_AlsoFillsScenarioAggregates()
    {
        var (scenario, context) = MakeSample();

        var sut = TokenFitnessEvaluator.Create(context);
        var detailed = sut.EvaluateDetailed(scenario, context);

        scenario.FitnessStage3.ShouldBe(detailed.Stage3);
        scenario.FitnessStage4.ShouldBe(detailed.Stage4);
    }

    [Test]
    public void EvaluateDetailed_AllComponentsWithinUnitInterval()
    {
        var (scenario, context) = MakeSample();

        var sut = TokenFitnessEvaluator.Create(context);
        var detailed = sut.EvaluateDetailed(scenario, context);

        detailed.Stage3Components.BlockOrder.ShouldBeInRange(0.0, 1.0);
        detailed.Stage3Components.Blacklist.ShouldBeInRange(0.0, 1.0);
        detailed.Stage3Components.Location.ShouldBeInRange(0.0, 1.0);
        detailed.Stage3Components.MaxGap.ShouldBeInRange(0.0, 1.0);
        detailed.Stage4Components.Fairness.ShouldBeInRange(0.0, 1.0);
        detailed.Stage4Components.MinimumHours.ShouldBeInRange(0.0, 1.0);
        detailed.Stage4Components.BlockSymmetry.ShouldBeInRange(0.0, 1.0);
    }

    [Test]
    public void EvaluateDetailed_Stage3ComponentsRecomposeIntoStage3Aggregate()
    {
        var (scenario, context) = MakeSample();

        var sut = TokenFitnessEvaluator.Create(context);
        var detailed = sut.EvaluateDetailed(scenario, context);

        var totalWeight = sut.Stage3BlockOrderWeight
            + sut.Stage3BlacklistWeight
            + sut.Stage3LocationWeight
            + sut.Stage3MaxGapWeight;
        var recomposed = ((detailed.Stage3Components.BlockOrder * sut.Stage3BlockOrderWeight)
            + (detailed.Stage3Components.Blacklist * sut.Stage3BlacklistWeight)
            + (detailed.Stage3Components.Location * sut.Stage3LocationWeight)
            + (detailed.Stage3Components.MaxGap * sut.Stage3MaxGapWeight)) / totalWeight;

        recomposed.ShouldBe(detailed.Stage3, 1e-9);
    }

    [Test]
    public void EvaluateDetailed_Stage4ComponentsMeanEqualsStage4Aggregate()
    {
        var (scenario, context) = MakeSample();

        var sut = TokenFitnessEvaluator.Create(context);
        var detailed = sut.EvaluateDetailed(scenario, context);

        var mean = (detailed.Stage4Components.Fairness
            + detailed.Stage4Components.MinimumHours
            + detailed.Stage4Components.BlockSymmetry) / 3.0;

        mean.ShouldBe(detailed.Stage4, 1e-9);
    }
}
