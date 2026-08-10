// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The loosened reach of the shift-kind balancer: two blocks no longer have to cover the very same days
/// to be swappable, they only have to overlap, and the swap then exchanges their shared days. That
/// multiplies the candidates, and it lets a swap reach INTO a package — which is why a partial swap is
/// held to the Pareto gate alone and has to leave the hours of the day untouched.
/// <para>
/// The plan is built so that exactly one partial swap is interesting: MA-1 owns a five-day package that
/// opens early and closes late, MA-2 a two-day early package sitting on MA-1's late days. Exchanging
/// those two shared days makes MA-1's package almost pure and turns MA-2's into a pure late one, so the
/// block ordering RISES while the kind shares even out. MA-2 is barred from the early shift outside its
/// own two days, so the two employees are not interchangeable and the fairness term can actually reward
/// the exchange instead of merely permuting equal shares.
/// </para>
/// </summary>
[TestFixture]
public class ShiftKindBalancerPartialSwapTests
{
    private const string MixedAgent = "MA-1";
    private const string EarlyAgent = "MA-2";
    private const string NightAgent = "MA-3";

    private const string OrderX = "order-x";

    private const string EarlyStart = "06:00";
    private const string EarlyEnd = "14:00";
    private const string LateStart = "14:00";
    private const string LateEnd = "22:00";
    private const string NightStart = "23:00";
    private const string NightEnd = "07:00";

    private const double ShiftHours = 8;
    private const double LongLateHours = 10;
    private const string DateFormat = "yyyy-MM-dd";

    private const int EarlyKind = 0;
    private const int LateKind = 1;
    private const int NightKind = 2;

    private const int EarlyStartHour = 6;
    private const int LateStartHour = 14;
    private const int NightStartHour = 23;

    /// <summary>Longest run of the plan; keeps the overlong-package count at zero on both sides.</summary>
    private const int BlockIdeal = 5;

    private static readonly DateOnly D1 = new(2026, 4, 20);
    private static readonly DateOnly D2 = D1.AddDays(1);
    private static readonly DateOnly D3 = D1.AddDays(2);
    private static readonly DateOnly D4 = D1.AddDays(3);
    private static readonly DateOnly D5 = D1.AddDays(4);
    private static readonly DateOnly D6 = D1.AddDays(5);
    private static readonly DateOnly D7 = D1.AddDays(6);
    private static readonly DateOnly D8 = D1.AddDays(7);

    private static readonly Guid EarlySlot = new("00000000-0000-0000-0000-0000000052e0");
    private static readonly Guid LateSlot = new("00000000-0000-0000-0000-0000000052e1");
    private static readonly Guid NightSlot = new("00000000-0000-0000-0000-0000000052e2");

    /// <summary>Days the shared-day swap would exchange between MA-1 and MA-2.</summary>
    private static readonly DateOnly[] SharedDays = [D3, D4];

    /// <summary>
    /// Builds the plan described on the fixture.
    /// </summary>
    /// <param name="mixedAgentOpensEarly">
    /// Gives MA-1 its two opening early days. With them the exchange repairs MA-1's package and the
    /// block ordering rises; without them MA-1 owns a pure late package that the exchange BREAKS, which
    /// is the case the Pareto gate has to refuse.
    /// </param>
    /// <param name="lateShiftRunsLonger">
    /// Raises the late tokens to ten hours, so the two tokens of a shared day no longer carry the same
    /// hours and the partial swap must not be built at all.
    /// </param>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator)
        BuildPlan(bool mixedAgentOpensEarly = true, bool lateShiftRunsLonger = false)
    {
        var lateHours = lateShiftRunsLonger ? LongLateHours : ShiftHours;
        var context = new CoreWizardContext
        {
            PeriodFrom = D1,
            PeriodUntil = D8,
            Agents = [Agent(MixedAgent), Agent(EarlyAgent), Agent(NightAgent)],
            Shifts =
            [
                Slot(D1, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D2, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D3, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D4, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D6, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D7, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D8, EarlySlot, EarlyStart, EarlyEnd, ShiftHours),
                Slot(D3, LateSlot, LateStart, LateEnd, lateHours),
                Slot(D4, LateSlot, LateStart, LateEnd, lateHours),
                Slot(D5, LateSlot, LateStart, LateEnd, lateHours),
                Slot(D1, NightSlot, NightStart, NightEnd, ShiftHours),
                Slot(D2, NightSlot, NightStart, NightEnd, ShiftHours),
            ],
            IneligibleAssignments = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)>
            {
                (EarlyAgent, EarlySlot, D1),
                (EarlyAgent, EarlySlot, D2),
                (EarlyAgent, EarlySlot, D8),
            },
        };

        var tokens = new List<CoreToken>();
        if (mixedAgentOpensEarly)
        {
            tokens.Add(Token(MixedAgent, D1, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));
            tokens.Add(Token(MixedAgent, D2, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));
        }

        tokens.Add(Token(MixedAgent, D3, LateSlot, LateKind, LateStartHour, lateHours));
        tokens.Add(Token(MixedAgent, D4, LateSlot, LateKind, LateStartHour, lateHours));
        tokens.Add(Token(MixedAgent, D5, LateSlot, LateKind, LateStartHour, lateHours));
        tokens.Add(Token(EarlyAgent, D3, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));
        tokens.Add(Token(EarlyAgent, D4, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));
        tokens.Add(Token(EarlyAgent, D6, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));
        tokens.Add(Token(EarlyAgent, D7, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));
        tokens.Add(Token(NightAgent, D1, NightSlot, NightKind, NightStartHour, ShiftHours));
        tokens.Add(Token(NightAgent, D2, NightSlot, NightKind, NightStartHour, ShiftHours));
        tokens.Add(Token(NightAgent, D8, EarlySlot, EarlyKind, EarlyStartHour, ShiftHours));

        var scenario = new CoreScenario { Id = "partial-kind-balance", Tokens = tokens };
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
        MaxWorkDays = BlockIdeal,
    };

    private static CoreShift Slot(DateOnly date, Guid shiftRefId, string start, string end, double hours)
        => new(
            Id: shiftRefId.ToString(),
            Name: OrderX,
            Date: date.ToString(DateFormat),
            StartTime: start,
            EndTime: end,
            Hours: hours,
            RequiredAssignments: 1,
            Priority: 0)
        {
            LocationContext = OrderX,
        };

    private static CoreToken Token(
        string agentId, DateOnly date, Guid shiftRefId, int kind, int startHour, double hours)
    {
        var start = date.ToDateTime(new TimeOnly(startHour, 0));
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: kind,
            Date: date,
            TotalHours: (decimal)hours,
            StartAt: start,
            EndAt: start.AddHours(hours),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: OrderX,
            ShiftRefId: shiftRefId,
            AgentId: agentId);
    }

    /// <summary>
    /// The plan the shared-day exchange between MA-1 and MA-2 would produce, built by hand so a refusal
    /// can be shown to be the gate's decision and not the absence of a candidate.
    /// </summary>
    /// <param name="scenario">Plan to exchange the two shared days in</param>
    private static CoreScenario Exchanged(CoreScenario scenario)
    {
        var tokens = scenario.Tokens.Select(t =>
        {
            if (!SharedDays.Contains(t.Date))
            {
                return t;
            }

            return t.AgentId switch
            {
                MixedAgent => t with { AgentId = EarlyAgent },
                EarlyAgent => t with { AgentId = MixedAgent },
                _ => t,
            };
        }).ToList();

        return new CoreScenario { Id = scenario.Id + "-exchanged", Tokens = tokens };
    }

    private static IReadOnlyList<string> WorkedDaysOf(CoreScenario scenario, string agentId) => scenario.Tokens
        .Where(t => t.AgentId == agentId)
        .OrderBy(t => t.Date)
        .Select(t => t.Date.ToString(DateFormat))
        .ToList();

    private static IReadOnlyList<int> KindsOf(CoreScenario scenario, string agentId) => scenario.Tokens
        .Where(t => t.AgentId == agentId)
        .OrderBy(t => t.Date)
        .Select(t => t.ShiftTypeIndex)
        .ToList();

    private static IReadOnlyList<string> OwnershipOf(CoreScenario scenario) => scenario.Tokens
        .OrderBy(t => t.Date)
        .ThenBy(t => t.ShiftRefId)
        .Select(t => $"{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.AgentId}|{t.ShiftTypeIndex}")
        .ToList();

    [Test]
    public void Apply_TwoBlocksOnlyOverlap_ExchangesTheSharedDaysTheStrictReachCouldNotSee()
    {
        var (context, scenario, evaluator) = BuildPlan();
        var before = evaluator.EvaluateDetailed(scenario, context);

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        KindsOf(balanced, MixedAgent).ShouldBe([EarlyKind, EarlyKind, EarlyKind, EarlyKind, LateKind]);
        KindsOf(balanced, EarlyAgent).ShouldBe([LateKind, LateKind, EarlyKind, EarlyKind]);

        var after = evaluator.EvaluateDetailed(balanced, context);
        after.Stage4Components.ShiftKindFairness
            .ShouldBeGreaterThan(before.Stage4Components.ShiftKindFairness);
        after.Stage3Components.BlockOrder.ShouldBeGreaterThan(before.Stage3Components.BlockOrder);
    }

    /// <summary>
    /// The property the loosened reach rests on: an exchange of shared days moves kinds, never days. No
    /// employee gains or loses a worked day, so package count, package lengths, the short-package share
    /// and the overlong count cannot move — the risk that the loosening fragments the packages into
    /// one-day blocks is closed by construction, not by a gate.
    /// </summary>
    [Test]
    public void Apply_AfterAPartialSwap_EveryEmployeeWorksTheSameDaysAndTheSameHours()
    {
        var (context, scenario, evaluator) = BuildPlan();

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        foreach (var agent in context.Agents)
        {
            WorkedDaysOf(balanced, agent.Id).ShouldBe(WorkedDaysOf(scenario, agent.Id));
            balanced.Tokens.Where(t => t.AgentId == agent.Id).Sum(t => t.TotalHours)
                .ShouldBe(scenario.Tokens.Where(t => t.AgentId == agent.Id).Sum(t => t.TotalHours));
        }

        OverlongBlockTrace.Overlong(balanced, context).Count
            .ShouldBe(OverlongBlockTrace.Overlong(scenario, context).Count);
        balanced.Tokens.Count.ShouldBe(scenario.Tokens.Count);
    }

    /// <summary>
    /// Same overlap, but MA-1 now owns a PURE late package. Exchanging the shared days would raise the
    /// kind fairness and break that package into two kinds — rule 9 buying rule 7, which the priority
    /// order forbids. The Pareto gate prices the break through its block-ordering component and the
    /// partial swap has to be refused.
    /// </summary>
    [Test]
    public void Apply_ThePartialSwapWouldBreakThePackageKind_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(mixedAgentOpensEarly: false);
        var before = evaluator.EvaluateDetailed(scenario, context);
        var exchanged = Exchanged(scenario);
        var wouldBe = evaluator.EvaluateDetailed(exchanged, context);

        wouldBe.Stage4Components.ShiftKindFairness.ShouldBeGreaterThan(
            before.Stage4Components.ShiftKindFairness,
            "the refusal only proves something when the exchange really would raise the fairness");
        wouldBe.Stage3Components.BlockOrder.ShouldBeLessThan(
            before.Stage3Components.BlockOrder,
            "and only when its price really is the package kind");

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// The late shift now runs ten hours against the early shift's eight. Exchanging a shared day would
    /// move two hours between two employees; the hours of the day are the one thing a partial swap may
    /// never touch, because no gate below rule 5 would notice a monotonicity break.
    /// </summary>
    [Test]
    public void Apply_TheSharedDayPairsDifferInHours_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(lateShiftRunsLonger: true);

        var balanced = new ShiftKindBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    [Test]
    public void Apply_RunTwiceOnTheLoosenedReach_ProducesTheIdenticalPlan()
    {
        var (firstContext, firstScenario, firstEvaluator) = BuildPlan();
        var (secondContext, secondScenario, secondEvaluator) = BuildPlan();

        var first = new ShiftKindBalancer().Apply(firstScenario, firstContext, firstEvaluator);
        var second = new ShiftKindBalancer().Apply(secondScenario, secondContext, secondEvaluator);
        var again = new ShiftKindBalancer().Apply(first, firstContext, firstEvaluator);

        OwnershipOf(second).ShouldBe(OwnershipOf(first));
        again.ShouldBeSameAs(first);
    }
}
