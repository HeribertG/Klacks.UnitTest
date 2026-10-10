// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Common.Rotation;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The same-day purity repair on a plan where one exchange makes both employees' blocks pure: MA-1 works early, early,
/// late on days 1-3 and MA-2 works early, late, late on days 3-5. Handing MA-1's day-3 late to MA-2 for MA-2's day-3
/// early gives early-early-early and late-late-late; both keep every worked day, no block change moves
/// (SPEC-ROTATION-2026-10-08 rule 4: purity never at the expense of rotation).
/// </summary>
[TestFixture]
public class PurityBalancerTests
{
    private const string FirstAgent = "MA-1";
    private const string SecondAgent = "MA-2";
    private const string Order = "order-x";

    private const string EarlyStart = "06:00";
    private const string EarlyEnd = "14:00";
    private const string LateStart = "14:00";
    private const string LateEnd = "22:00";

    private const double FullHours = 8;
    private const string DateFormat = "yyyy-MM-dd";

    private const int EarlyKind = 0;
    private const int LateKind = 1;

    private const int EarlyHour = 6;
    private const int LateHour = 14;

    private static readonly DateOnly FirstDay = new(2026, 4, 20);
    private static readonly DateOnly SecondDay = FirstDay.AddDays(1);
    private static readonly DateOnly ThirdDay = FirstDay.AddDays(2);
    private static readonly DateOnly FourthDay = FirstDay.AddDays(3);
    private static readonly DateOnly FifthDay = FirstDay.AddDays(4);
    private static readonly DateOnly LastDay = FirstDay.AddDays(6);

    private static readonly Guid FirstEarlySlot = new("00000000-0000-0000-0000-00000000c0e0");
    private static readonly Guid SecondEarlySlot = new("00000000-0000-0000-0000-00000000c0e1");
    private static readonly Guid FirstLateSlot = new("00000000-0000-0000-0000-00000000c0e2");
    private static readonly Guid SecondLateSlot = new("00000000-0000-0000-0000-00000000c0e3");

    /// <param name="lockFirstAgentLate">Marks MA-1's day-3 late as locked work</param>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator) BuildPlan(
        bool lockFirstAgentLate = false)
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = LastDay,
            Agents = [Agent(FirstAgent), Agent(SecondAgent)],
            Shifts =
            [
                Slot(FirstDay, FirstEarlySlot, EarlyStart, EarlyEnd),
                Slot(SecondDay, FirstEarlySlot, EarlyStart, EarlyEnd),
                Slot(ThirdDay, FirstLateSlot, LateStart, LateEnd),
                Slot(ThirdDay, SecondEarlySlot, EarlyStart, EarlyEnd),
                Slot(FourthDay, SecondLateSlot, LateStart, LateEnd),
                Slot(FifthDay, SecondLateSlot, LateStart, LateEnd),
            ],
        };

        var scenario = new CoreScenario
        {
            Id = "purity-balance",
            Tokens =
            [
                Token(FirstAgent, FirstDay, FirstEarlySlot, EarlyKind, EarlyHour),
                Token(FirstAgent, SecondDay, FirstEarlySlot, EarlyKind, EarlyHour),
                Token(FirstAgent, ThirdDay, FirstLateSlot, LateKind, LateHour, lockFirstAgentLate),
                Token(SecondAgent, ThirdDay, SecondEarlySlot, EarlyKind, EarlyHour),
                Token(SecondAgent, FourthDay, SecondLateSlot, LateKind, LateHour),
                Token(SecondAgent, FifthDay, SecondLateSlot, LateKind, LateHour),
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

    private static CoreShift Slot(DateOnly date, Guid shiftRefId, string start, string end)
        => new(
            Id: shiftRefId.ToString(),
            Name: Order,
            Date: date.ToString(DateFormat),
            StartTime: start,
            EndTime: end,
            Hours: FullHours,
            RequiredAssignments: 1,
            Priority: 0)
        {
            LocationContext = Order,
        };

    private static CoreToken Token(
        string agentId, DateOnly date, Guid shiftRefId, int kind, int startHour, bool locked = false)
    {
        var start = date.ToDateTime(new TimeOnly(startHour, 0));
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: kind,
            Date: date,
            TotalHours: (decimal)FullHours,
            StartAt: start,
            EndAt: start.AddHours(FullHours),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: locked,
            LocationContext: Order,
            ShiftRefId: shiftRefId,
            AgentId: agentId);
    }

    private static IReadOnlyList<int> KindsOf(CoreScenario scenario, string agentId) => scenario.Tokens
        .Where(t => t.AgentId == agentId)
        .OrderBy(t => t.Date)
        .ThenBy(t => t.StartAt)
        .Select(t => t.ShiftTypeIndex)
        .ToList();

    private static IReadOnlyList<DateOnly> DaysOf(CoreScenario scenario, string agentId) => scenario.Tokens
        .Where(t => t.AgentId == agentId)
        .Select(t => t.Date)
        .OrderBy(d => d)
        .ToList();

    private static RotationAssessment RotationOf(CoreScenario scenario, CoreWizardContext context)
    {
        var rotation = RotationContext.For(context);
        var total = default(RotationAssessment);
        foreach (var agent in context.Agents)
        {
            total = total.Add(AgentRotationAssessor.Assess(
                scenario.Tokens.Where(t => t.AgentId == agent.Id), agent.Id, context, rotation));
        }

        return total;
    }

    private static IReadOnlyList<string> OwnershipOf(CoreScenario scenario) => scenario.Tokens
        .OrderBy(t => t.Date)
        .ThenBy(t => t.ShiftRefId)
        .Select(t => $"{t.Date.ToString(DateFormat)}|{t.ShiftRefId}|{t.AgentId}|{t.ShiftTypeIndex}")
        .ToList();

    [Test]
    public void Apply_SameDayExchangeMakesBothBlocksPure_SwapsTheDayAndKeepsWorkedDays()
    {
        var (context, scenario, evaluator) = BuildPlan();
        var rotationBefore = RotationOf(scenario, context);

        var balanced = new PurityBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        KindsOf(balanced, FirstAgent).ShouldBe([EarlyKind, EarlyKind, EarlyKind]);
        KindsOf(balanced, SecondAgent).ShouldBe([LateKind, LateKind, LateKind]);
        DaysOf(balanced, FirstAgent).ShouldBe(DaysOf(scenario, FirstAgent));
        DaysOf(balanced, SecondAgent).ShouldBe(DaysOf(scenario, SecondAgent));

        var rotationAfter = RotationOf(balanced, context);
        rotationAfter.InBlockChanges.ShouldBeLessThan(rotationBefore.InBlockChanges);
        rotationAfter.NonIdealTransitions.ShouldBeLessThanOrEqualTo(rotationBefore.NonIdealTransitions);

        evaluator.Evaluate(balanced, context);
        evaluator.Evaluate(scenario, context);
        evaluator.Compare(balanced, scenario).ShouldBeLessThan(0);
        balanced.FitnessStage0.ShouldBe(scenario.FitnessStage0);
        balanced.Tokens.Sum(t => t.TotalHours).ShouldBe(scenario.Tokens.Sum(t => t.TotalHours));
    }

    [Test]
    public void Apply_TheDayToExchangeIsLocked_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(lockFirstAgentLate: true);

        var balanced = new PurityBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    [Test]
    public void Apply_CancelledBeforeStart_ReturnsThePlanUnchanged()
    {
        var (context, scenario, evaluator) = BuildPlan();

        var balanced = new PurityBalancer().Apply(
            scenario, context, evaluator, cancellationToken: new CancellationToken(canceled: true));

        balanced.ShouldBeSameAs(scenario);
    }

    [Test]
    public void Apply_RunTwice_ProducesTheIdenticalPlan()
    {
        var (context, scenario, evaluator) = BuildPlan();
        var (secondContext, secondScenario, secondEvaluator) = BuildPlan();

        var first = new PurityBalancer().Apply(scenario, context, evaluator);
        var second = new PurityBalancer().Apply(secondScenario, secondContext, secondEvaluator);
        var again = new PurityBalancer().Apply(first, context, evaluator);

        first.ShouldNotBeSameAs(scenario);
        OwnershipOf(second).ShouldBe(OwnershipOf(first));
        again.ShouldBeSameAs(first);
    }
}
