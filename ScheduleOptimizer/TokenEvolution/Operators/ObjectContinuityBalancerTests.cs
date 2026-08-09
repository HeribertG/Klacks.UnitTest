// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

[TestFixture]
public class ObjectContinuityBalancerTests
{
    private const string FirstAgent = "MA-1";
    private const string SecondAgent = "MA-2";
    private const string ShiftName = "FD";
    private const string StartTime = "08:00";
    private const string EndTime = "16:00";
    private const double ShiftHours = 8;
    private const int ShiftTypeIndexEarly = 0;

    private static readonly DateOnly FirstDay = new(2026, 4, 20);
    private static readonly Guid OrderAShift = new("00000000-0000-0000-0000-00000000ba01");
    private static readonly Guid OrderBShift = new("00000000-0000-0000-0000-00000000ba02");
    private static readonly string OrderA = OrderAShift.ToString();
    private static readonly string OrderB = OrderBShift.ToString();

    /// <summary>
    /// Two employees, two identically cut orders, two consecutive days. Each employee starts on the
    /// other's order, so both cross the orders exactly once and one same-day exchange removes both
    /// switches. Everything else — hours, kind, days, coverage — is identical in every arrangement, so
    /// the order-loyalty term is the only stage that can decide.
    /// </summary>
    /// <param name="preferences">Master-data preferences of the run</param>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator)
        BuildCrossedPlan(params CoreShiftPreference[] preferences)
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = FirstDay.AddDays(1),
            Agents = [Agent(FirstAgent), Agent(SecondAgent)],
            Shifts =
            [
                Slot(FirstDay, OrderAShift, OrderA),
                Slot(FirstDay, OrderBShift, OrderB),
                Slot(FirstDay.AddDays(1), OrderAShift, OrderA),
                Slot(FirstDay.AddDays(1), OrderBShift, OrderB),
            ],
            ShiftPreferences = preferences,
        };

        var scenario = new CoreScenario
        {
            Id = "crossed",
            Tokens =
            [
                Token(FirstAgent, FirstDay, OrderAShift, OrderA),
                Token(SecondAgent, FirstDay, OrderBShift, OrderB),
                Token(FirstAgent, FirstDay.AddDays(1), OrderBShift, OrderB),
                Token(SecondAgent, FirstDay.AddDays(1), OrderAShift, OrderA),
            ],
        };

        var evaluator = TokenFitnessEvaluator.Create(context);
        evaluator.Evaluate(scenario, context);
        return (context, scenario, evaluator);
    }

    private static CoreAgent Agent(string id) => new(
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
        FullTime = 40,
        PerformsShiftWork = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreShift Slot(DateOnly date, Guid shiftRefId, string location) => new(
        Id: shiftRefId.ToString(),
        Name: ShiftName,
        Date: date.ToString("yyyy-MM-dd"),
        StartTime: StartTime,
        EndTime: EndTime,
        Hours: ShiftHours,
        RequiredAssignments: 1,
        Priority: 0)
    {
        LocationContext = location,
    };

    private static CoreToken Token(string agentId, DateOnly date, Guid shiftRefId, string location)
    {
        var start = new TimeOnly(8, 0);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: ShiftTypeIndexEarly,
            Date: date,
            TotalHours: (decimal)ShiftHours,
            StartAt: date.ToDateTime(start),
            EndAt: date.ToDateTime(start.AddHours(ShiftHours)),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: location,
            ShiftRefId: shiftRefId,
            AgentId: agentId);
    }

    private static IReadOnlyList<string> OwnershipOf(CoreScenario scenario) => scenario.Tokens
        .OrderBy(t => t.Date)
        .ThenBy(t => t.ShiftRefId)
        .Select(t => $"{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.AgentId}")
        .ToList();

    private static int BlacklistedCount(CoreScenario scenario, CoreWizardContext context) => scenario.Tokens
        .Count(t => context.ShiftPreferences.Any(p => p.AgentId == t.AgentId
            && p.ShiftRefId == t.ShiftRefId
            && p.Kind == ShiftPreferenceKind.Blacklist));

    [Test]
    public void Apply_CrossedOrders_TradesTheDayAndRemovesEverySwitch()
    {
        var (context, scenario, evaluator) = BuildCrossedPlan();

        var balanced = new ObjectContinuityBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        LocationContinuity.Score(balanced).ShouldBe(1.0);
        balanced.Tokens.Count.ShouldBe(scenario.Tokens.Count);
        balanced.Tokens.Sum(t => t.TotalHours).ShouldBe(scenario.Tokens.Sum(t => t.TotalHours));
        foreach (var perAgent in balanced.Tokens.GroupBy(t => t.AgentId))
        {
            perAgent.Select(t => t.LocationContext).Distinct().Count().ShouldBe(1);
        }
    }

    [Test]
    public void Apply_PreferredShiftOnBothSides_NeverTradesItAway()
    {
        var (context, scenario, evaluator) = BuildCrossedPlan(
            new CoreShiftPreference(FirstAgent, OrderAShift, ShiftPreferenceKind.Preferred),
            new CoreShiftPreference(FirstAgent, OrderBShift, ShiftPreferenceKind.Preferred));

        var balanced = new ObjectContinuityBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
        balanced.Tokens
            .Count(t => t.AgentId == FirstAgent)
            .ShouldBe(2);
        balanced.Tokens
            .Where(t => t.AgentId == FirstAgent)
            .Select(t => t.ShiftRefId)
            .ShouldBe([OrderAShift, OrderBShift], ignoreOrder: true);
    }

    [Test]
    public void Apply_BlacklistedReceiver_LeavesThePlanAloneAndAddsNoViolation()
    {
        var (context, scenario, evaluator) = BuildCrossedPlan(
            new CoreShiftPreference(FirstAgent, OrderBShift, ShiftPreferenceKind.Blacklist),
            new CoreShiftPreference(SecondAgent, OrderBShift, ShiftPreferenceKind.Blacklist));

        var before = BlacklistedCount(scenario, context);
        var balanced = new ObjectContinuityBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
        BlacklistedCount(balanced, context).ShouldBe(before);
    }

    [Test]
    public void Apply_RunTwice_ProducesTheIdenticalPlan()
    {
        var (firstContext, firstScenario, firstEvaluator) = BuildCrossedPlan();
        var (secondContext, secondScenario, secondEvaluator) = BuildCrossedPlan();

        var first = new ObjectContinuityBalancer().Apply(firstScenario, firstContext, firstEvaluator);
        var second = new ObjectContinuityBalancer().Apply(secondScenario, secondContext, secondEvaluator);
        var again = new ObjectContinuityBalancer().Apply(first, firstContext, firstEvaluator);

        OwnershipOf(second).ShouldBe(OwnershipOf(first));
        again.ShouldBeSameAs(first);
    }

    [Test]
    public void Apply_LockedTokens_AreNeverTraded()
    {
        var (context, scenario, evaluator) = BuildCrossedPlan();
        var locked = new CoreScenario
        {
            Id = "locked",
            Tokens = scenario.Tokens.Select(t => t with { IsLocked = true }).ToList(),
        };
        evaluator.Evaluate(locked, context);

        new ObjectContinuityBalancer().Apply(locked, context, evaluator).ShouldBeSameAs(locked);
    }

    [Test]
    public void Apply_WithoutAnyOrderDimension_IsANoOp()
    {
        var (context, scenario, evaluator) = BuildCrossedPlan();
        var unlabelled = new CoreScenario
        {
            Id = "unlabelled",
            Tokens = scenario.Tokens.Select(t => t with { LocationContext = null }).ToList(),
        };
        evaluator.Evaluate(unlabelled, context);

        new ObjectContinuityBalancer().Apply(unlabelled, context, evaluator).ShouldBeSameAs(unlabelled);
    }
}
