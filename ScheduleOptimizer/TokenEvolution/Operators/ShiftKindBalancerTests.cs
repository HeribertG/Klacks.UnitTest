// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The Pareto acceptance path of the shift-kind balancer, measured on a plan built so that exactly one
/// swap is possible and its whole price is order loyalty (rule 10) — the component the priority order
/// places BELOW shift-kind fairness (rule 9) and which the lexicographic gate nevertheless protects,
/// because Stage 3 mixes it with the rotation rules into a single number.
/// </summary>
[TestFixture]
public class ShiftKindBalancerTests
{
    private const string EarlyAgent = "MA-1";
    private const string LateAgent = "MA-2";
    private const string DayAgent = "MA-3";

    private const string OrderX = "order-x";
    private const string OrderY = "order-y";
    private const string OrderZ = "order-z";

    private const string EarlyStart = "06:00";
    private const string EarlyEnd = "14:00";
    private const string LateStart = "14:00";
    private const string LateEnd = "22:00";
    private const string NightStart = "23:00";
    private const string NightEnd = "07:00";

    private const double ShiftHours = 8;
    private const string DateFormat = "yyyy-MM-dd";

    private const int EarlyKind = 0;
    private const int LateKind = 1;
    private const int NightKind = 2;

    private static readonly DateOnly FirstDay = new(2026, 4, 20);
    private static readonly DateOnly SecondDay = FirstDay.AddDays(1);
    private static readonly DateOnly SixthDay = FirstDay.AddDays(5);
    private static readonly DateOnly SeventhDay = FirstDay.AddDays(6);
    private static readonly DateOnly EighthDay = FirstDay.AddDays(7);

    private static readonly Guid EarlyOnX = new("00000000-0000-0000-0000-0000000051e0");
    private static readonly Guid LateOnY = new("00000000-0000-0000-0000-0000000051e1");
    private static readonly Guid NightOnX = new("00000000-0000-0000-0000-0000000051e2");
    private static readonly Guid NightOnY = new("00000000-0000-0000-0000-0000000051e3");
    private static readonly Guid EarlyOnZ = new("00000000-0000-0000-0000-0000000051e4");

    /// <summary>
    /// Three employees over eight days. MA-1 owns an early pair on the first two days and a night pair
    /// on the weekend, MA-2 a late pair on the same first two days and its own night pair, MA-3 a
    /// three-day early package that keeps the kind counts from being symmetric. The only swappable pair
    /// is the early/late pair of the first two days: same first day, same length, different kind.
    /// <para>
    /// Every employee stays on one order in the unswapped plan, so exchanging the two opening packages
    /// costs order loyalty on both sides while the day set, the hours and the block lengths of every
    /// employee stay exactly as they were.
    /// </para>
    /// </summary>
    /// <param name="banEarlyAgentFromTheLastDay">
    /// Removes one eligible early day from MA-1 only. Without it the two employees are interchangeable
    /// and the swap merely permutes the kind shares, which the fairness term cannot reward.
    /// </param>
    /// <param name="preferences">Master-data shift preferences of the run</param>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator)
        BuildPlan(bool banEarlyAgentFromTheLastDay, params CoreShiftPreference[] preferences)
    {
        var ineligible = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)>();
        if (banEarlyAgentFromTheLastDay)
        {
            ineligible.Add((EarlyAgent, EarlyOnZ, EighthDay));
        }

        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = EighthDay,
            Agents = [Agent(EarlyAgent), Agent(LateAgent), Agent(DayAgent)],
            Shifts =
            [
                Slot(FirstDay, EarlyOnX, OrderX, EarlyStart, EarlyEnd),
                Slot(SecondDay, EarlyOnX, OrderX, EarlyStart, EarlyEnd),
                Slot(FirstDay, LateOnY, OrderY, LateStart, LateEnd),
                Slot(SecondDay, LateOnY, OrderY, LateStart, LateEnd),
                Slot(SixthDay, NightOnX, OrderX, NightStart, NightEnd),
                Slot(SeventhDay, NightOnX, OrderX, NightStart, NightEnd),
                Slot(SixthDay, NightOnY, OrderY, NightStart, NightEnd),
                Slot(SeventhDay, NightOnY, OrderY, NightStart, NightEnd),
                Slot(SixthDay, EarlyOnZ, OrderZ, EarlyStart, EarlyEnd),
                Slot(SeventhDay, EarlyOnZ, OrderZ, EarlyStart, EarlyEnd),
                Slot(EighthDay, EarlyOnZ, OrderZ, EarlyStart, EarlyEnd),
            ],
            ShiftPreferences = preferences,
            IneligibleAssignments = ineligible,
        };

        var scenario = new CoreScenario
        {
            Id = "kind-balance",
            Tokens =
            [
                Token(EarlyAgent, FirstDay, EarlyOnX, OrderX, EarlyKind, 6),
                Token(EarlyAgent, SecondDay, EarlyOnX, OrderX, EarlyKind, 6),
                Token(EarlyAgent, SixthDay, NightOnX, OrderX, NightKind, 23),
                Token(EarlyAgent, SeventhDay, NightOnX, OrderX, NightKind, 23),
                Token(LateAgent, FirstDay, LateOnY, OrderY, LateKind, 14),
                Token(LateAgent, SecondDay, LateOnY, OrderY, LateKind, 14),
                Token(LateAgent, SixthDay, NightOnY, OrderY, NightKind, 23),
                Token(LateAgent, SeventhDay, NightOnY, OrderY, NightKind, 23),
                Token(DayAgent, SixthDay, EarlyOnZ, OrderZ, EarlyKind, 6),
                Token(DayAgent, SeventhDay, EarlyOnZ, OrderZ, EarlyKind, 6),
                Token(DayAgent, EighthDay, EarlyOnZ, OrderZ, EarlyKind, 6),
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

    private static CoreShift Slot(DateOnly date, Guid shiftRefId, string order, string start, string end)
        => new(
            Id: shiftRefId.ToString(),
            Name: order,
            Date: date.ToString(DateFormat),
            StartTime: start,
            EndTime: end,
            Hours: ShiftHours,
            RequiredAssignments: 1,
            Priority: 0)
        {
            LocationContext = order,
        };

    private static CoreToken Token(
        string agentId, DateOnly date, Guid shiftRefId, string order, int kind, int startHour)
    {
        var start = date.ToDateTime(new TimeOnly(startHour, 0));
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: kind,
            Date: date,
            TotalHours: (decimal)ShiftHours,
            StartAt: start,
            EndAt: start.AddHours(ShiftHours),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: order,
            ShiftRefId: shiftRefId,
            AgentId: agentId);
    }

    private static IReadOnlyList<string> OwnershipOf(CoreScenario scenario) => scenario.Tokens
        .OrderBy(t => t.Date)
        .ThenBy(t => t.ShiftRefId)
        .Select(t => $"{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.AgentId}|{t.ShiftTypeIndex}")
        .ToList();

    private static IReadOnlyList<int> KindsOf(CoreScenario scenario, string agentId) => scenario.Tokens
        .Where(t => t.AgentId == agentId)
        .OrderBy(t => t.Date)
        .Select(t => t.ShiftTypeIndex)
        .ToList();

    [Test]
    public void Apply_FairnessRisesAndOnlyOrderLoyaltyFalls_TakesTheSwapTheLexicographicGateRefuses()
    {
        var (context, scenario, evaluator) = BuildPlan(banEarlyAgentFromTheLastDay: true);
        var before = evaluator.EvaluateDetailed(scenario, context);

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        KindsOf(balanced, EarlyAgent).ShouldBe([LateKind, LateKind, NightKind, NightKind]);
        KindsOf(balanced, LateAgent).ShouldBe([EarlyKind, EarlyKind, NightKind, NightKind]);

        var after = evaluator.EvaluateDetailed(balanced, context);
        evaluator.Evaluate(scenario, context);
        evaluator.Compare(balanced, scenario).ShouldBeGreaterThan(
            0,
            "the swap must be one the lexicographic gate refuses, otherwise the test proves nothing "
            + "about the Pareto path");

        after.Stage4Components.ShiftKindFairness
            .ShouldBeGreaterThan(before.Stage4Components.ShiftKindFairness);
        after.Stage3Components.Location.ShouldBeLessThan(before.Stage3Components.Location);
        after.Stage3Components.BlockOrder.ShouldBe(before.Stage3Components.BlockOrder);
        after.Stage3Components.Blacklist.ShouldBe(before.Stage3Components.Blacklist);
        after.Stage0.ShouldBe(before.Stage0);
        after.Stage1.ShouldBe(before.Stage1);
        after.Stage2.ShouldBe(before.Stage2);
        balanced.Tokens.Count.ShouldBe(scenario.Tokens.Count);
        balanced.Tokens.Sum(t => t.TotalHours).ShouldBe(scenario.Tokens.Sum(t => t.TotalHours));
    }

    /// <summary>
    /// Same plan, but the receiving employee has the late shift on its blacklist. Order loyalty may pay
    /// for fairness, the shift preference may not (owner decision B2) — and the assignment filter does
    /// not look at preferences, so only the gate can refuse here.
    /// </summary>
    [Test]
    public void Apply_TheSwapWouldCreateABlacklistViolation_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(
            banEarlyAgentFromTheLastDay: true,
            new CoreShiftPreference(EarlyAgent, LateOnY, ShiftPreferenceKind.Blacklist));

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// Without the eligibility difference the swap only permutes the kind shares between two
    /// interchangeable employees. Rule 9 has to rise strictly, so the loosened gate must stay shut —
    /// otherwise it would hand out order-loyalty losses for nothing.
    /// </summary>
    [Test]
    public void Apply_TheSwapDoesNotRaiseTheFairness_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(banEarlyAgentFromTheLastDay: false);

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// The rule-7 clause of the Pareto gate must not narrow what a FULL swap may do. A full swap replaces
    /// a whole kind block by a block of the same days, so a pure package stays pure and a mixed one keeps
    /// or loses its break — the mixed count can never rise and the clause can never bind. This measures
    /// that on the one swap the gate accepts here, so the claim is empirical and not only an argument.
    /// </summary>
    [Test]
    public void Apply_TheAcceptedFullSwap_LeavesTheMixedPackageCountUntouched()
    {
        var (context, scenario, evaluator) = BuildPlan(banEarlyAgentFromTheLastDay: true);

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        MixedKindPackageTrace.Count(balanced).ShouldBe(MixedKindPackageTrace.Count(scenario));
    }

    [Test]
    public void Apply_RunTwice_ProducesTheIdenticalPlan()
    {
        var (firstContext, firstScenario, firstEvaluator) = BuildPlan(banEarlyAgentFromTheLastDay: true);
        var (secondContext, secondScenario, secondEvaluator) = BuildPlan(banEarlyAgentFromTheLastDay: true);

        var first = new ShiftKindBalancer().Apply(firstScenario, firstContext, firstEvaluator);
        var second = new ShiftKindBalancer().Apply(secondScenario, secondContext, secondEvaluator);
        var again = new ShiftKindBalancer().Apply(first, firstContext, firstEvaluator);

        OwnershipOf(second).ShouldBe(OwnershipOf(first));
        again.ShouldBeSameAs(first);
    }
}
