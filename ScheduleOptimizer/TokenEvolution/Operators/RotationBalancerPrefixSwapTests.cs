// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Common.Rotation;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The prefix-swap fallback of the rotation repair. MA-1 works early on days 1-2 and night on days 6-7 (early to night
/// misses the ideal late). MA-2 works late on day 3 and late on day 6 (late to late misses the ideal night) and, in the
/// accepted variant, night on day 7. No whole-block pair covers the same days, so only the prefix path can act: handing
/// day 6 of MA-1's night block to MA-2 in exchange for MA-2's late gives MA-1 late-night and MA-2 night-night, fixing
/// both block changes (SPEC-ROTATION-2026-10-08). With MA-2's night on day 7 the swap only moves the mixed package
/// from MA-2 to MA-1; without it the swap would create a mixed package and must be refused.
/// </summary>
[TestFixture]
public class RotationBalancerPrefixSwapTests
{
    private const string FirstAgent = "MA-1";
    private const string SecondAgent = "MA-2";
    private const string Order = "order-x";

    private const string EarlyStart = "06:00";
    private const string EarlyEnd = "14:00";
    private const string LateStart = "14:00";
    private const string LateEnd = "22:00";
    private const string NightStart = "23:00";
    private const string NightEnd = "07:00";

    private const double FullHours = 8;
    private const string DateFormat = "yyyy-MM-dd";

    private const int EarlyKind = 0;
    private const int LateKind = 1;
    private const int NightKind = 2;

    private const int EarlyHour = 6;
    private const int LateHour = 14;
    private const int NightHour = 23;

    private const int ExpectedMixedPackagesOfAcceptedPlan = 1;

    private static readonly DateOnly FirstDay = new(2026, 4, 20);
    private static readonly DateOnly SecondDay = FirstDay.AddDays(1);
    private static readonly DateOnly ThirdDay = FirstDay.AddDays(2);
    private static readonly DateOnly SixthDay = FirstDay.AddDays(5);
    private static readonly DateOnly SeventhDay = FirstDay.AddDays(6);
    private static readonly DateOnly LastDay = FirstDay.AddDays(7);

    private static readonly Guid EarlySlot = new("00000000-0000-0000-0000-00000000b0e0");
    private static readonly Guid FirstLateSlot = new("00000000-0000-0000-0000-00000000b0e1");
    private static readonly Guid SecondLateSlot = new("00000000-0000-0000-0000-00000000b0e2");
    private static readonly Guid FirstNightSlot = new("00000000-0000-0000-0000-00000000b0e3");
    private static readonly Guid SecondNightSlot = new("00000000-0000-0000-0000-00000000b0e4");

    /// <param name="secondAgentClosesWithNight">MA-2 also works night on day 7, so its day-6 package is already mixed</param>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator) BuildPlan(
        bool secondAgentClosesWithNight)
    {
        List<CoreShift> shifts =
        [
            Slot(FirstDay, EarlySlot, EarlyStart, EarlyEnd),
            Slot(SecondDay, EarlySlot, EarlyStart, EarlyEnd),
            Slot(ThirdDay, FirstLateSlot, LateStart, LateEnd),
            Slot(SixthDay, SecondLateSlot, LateStart, LateEnd),
            Slot(SixthDay, FirstNightSlot, NightStart, NightEnd),
            Slot(SeventhDay, FirstNightSlot, NightStart, NightEnd),
        ];

        List<CoreToken> tokens =
        [
            Token(FirstAgent, FirstDay, EarlySlot, EarlyKind, EarlyHour),
            Token(FirstAgent, SecondDay, EarlySlot, EarlyKind, EarlyHour),
            Token(FirstAgent, SixthDay, FirstNightSlot, NightKind, NightHour),
            Token(FirstAgent, SeventhDay, FirstNightSlot, NightKind, NightHour),
            Token(SecondAgent, ThirdDay, FirstLateSlot, LateKind, LateHour),
            Token(SecondAgent, SixthDay, SecondLateSlot, LateKind, LateHour),
        ];

        if (secondAgentClosesWithNight)
        {
            shifts.Add(Slot(SeventhDay, SecondNightSlot, NightStart, NightEnd));
            tokens.Add(Token(SecondAgent, SeventhDay, SecondNightSlot, NightKind, NightHour));
        }

        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = LastDay,
            Agents = [Agent(FirstAgent), Agent(SecondAgent)],
            Shifts = shifts,
        };

        var scenario = new CoreScenario { Id = "rotation-prefix-swap", Tokens = tokens };
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

    private static CoreToken Token(string agentId, DateOnly date, Guid shiftRefId, int kind, int startHour)
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
            IsLocked: false,
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

    private static int NonIdealTransitionsOf(CoreScenario scenario, CoreWizardContext context)
    {
        var rotation = RotationContext.For(context);
        var total = default(RotationAssessment);
        foreach (var agent in context.Agents)
        {
            total = total.Add(AgentRotationAssessor.Assess(
                scenario.Tokens.Where(t => t.AgentId == agent.Id), agent.Id, context, rotation));
        }

        return total.NonIdealTransitions;
    }

    /// <summary>The plan with the two agents' tokens on <paramref name="date"/> handed to each other.</summary>
    private static CoreScenario ExchangeOn(CoreScenario scenario, DateOnly date) => new()
    {
        Id = scenario.Id + "-exchanged",
        Tokens = scenario.Tokens
            .Select(t => t.Date != date
                ? t
                : t with { AgentId = t.AgentId == FirstAgent ? SecondAgent : FirstAgent })
            .ToList(),
    };

    [Test]
    public void Apply_PrefixSwapsEnabled_HandsTheFirstNightToTheLateWorker()
    {
        var (context, scenario, evaluator) = BuildPlan(secondAgentClosesWithNight: true);

        var balanced = new RotationBalancer(includePrefixSwaps: true).Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        KindsOf(balanced, FirstAgent).ShouldBe([EarlyKind, EarlyKind, LateKind, NightKind]);
        KindsOf(balanced, SecondAgent).ShouldBe([LateKind, NightKind, NightKind]);

        NonIdealTransitionsOf(balanced, context).ShouldBeLessThan(NonIdealTransitionsOf(scenario, context));
        MixedKindPackageTrace.Count(scenario).ShouldBe(ExpectedMixedPackagesOfAcceptedPlan);
        MixedKindPackageTrace.Count(balanced).ShouldBe(ExpectedMixedPackagesOfAcceptedPlan);

        evaluator.Evaluate(balanced, context);
        evaluator.Evaluate(scenario, context);
        evaluator.Compare(balanced, scenario).ShouldBeLessThan(0);
        balanced.FitnessStage0.ShouldBe(scenario.FitnessStage0);
        balanced.Tokens.Count.ShouldBe(scenario.Tokens.Count);
        balanced.Tokens.Sum(t => t.TotalHours).ShouldBe(scenario.Tokens.Sum(t => t.TotalHours));
    }

    [Test]
    public void Apply_PrefixSwapsDisabled_NoWholeBlockPairQualifies_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(secondAgentClosesWithNight: true);

        var balanced = new RotationBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    [Test]
    public void Apply_PrefixSwapsEnabled_RunTwice_IsStableAndIdentical()
    {
        var (context, scenario, evaluator) = BuildPlan(secondAgentClosesWithNight: true);
        var (secondContext, secondScenario, secondEvaluator) = BuildPlan(secondAgentClosesWithNight: true);

        var first = new RotationBalancer(includePrefixSwaps: true).Apply(scenario, context, evaluator);
        var second = new RotationBalancer(includePrefixSwaps: true).Apply(secondScenario, secondContext, secondEvaluator);
        var again = new RotationBalancer(includePrefixSwaps: true).Apply(first, context, evaluator);

        KindsOf(second, FirstAgent).ShouldBe(KindsOf(first, FirstAgent));
        KindsOf(second, SecondAgent).ShouldBe(KindsOf(first, SecondAgent));
        again.ShouldBeSameAs(first);
    }

    /// <summary>
    /// Without MA-2's closing night the same exchange would turn MA-1's pure night package into late-night while
    /// MA-2's single late becomes a single night, so the mixed-package count rises from zero to one. The hand-built
    /// exchange proves every other gate would let it through — rotation falls, legality is untouched and the
    /// lexicographic fitness improves — so only the mixed-package rule (rule 7 above rule 8) can refuse it. The same
    /// exchange passes the slot filter in the accepted variant, which holds one token more for the receiving agent.
    /// </summary>
    [Test]
    public void Apply_PrefixSwapWouldRaiseMixedPackages_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(secondAgentClosesWithNight: false);
        var exchanged = ExchangeOn(scenario, SixthDay);

        KindsOf(exchanged, FirstAgent).ShouldBe([EarlyKind, EarlyKind, LateKind, NightKind]);
        NonIdealTransitionsOf(exchanged, context).ShouldBeLessThan(NonIdealTransitionsOf(scenario, context));
        MixedKindPackageTrace.Count(exchanged).ShouldBeGreaterThan(MixedKindPackageTrace.Count(scenario));
        evaluator.Evaluate(exchanged, context);
        evaluator.Evaluate(scenario, context);
        exchanged.FitnessStage0.ShouldBe(scenario.FitnessStage0);
        evaluator.Compare(exchanged, scenario).ShouldBeLessThan(0);

        var withPrefix = new RotationBalancer(includePrefixSwaps: true).Apply(scenario, context, evaluator);
        var wholeBlockOnly = new RotationBalancer().Apply(scenario, context, evaluator);

        withPrefix.ShouldBeSameAs(scenario);
        wholeBlockOnly.ShouldBeSameAs(scenario);
    }
}
