// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The whole-block rotation repair on a plan built so that exactly one swap fixes the rotation of both employees:
/// MA-1 runs early then night, MA-2 late then late. The opening blocks are offset by one day, so the only same-day pair
/// is the two closing blocks; exchanging them (same days, different kinds)
/// gives early then late and late then night — both ideal successors (SPEC-ROTATION-2026-10-08).
/// </summary>
[TestFixture]
public class RotationBalancerTests
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
    private const double ShortHours = 4;
    private const string DateFormat = "yyyy-MM-dd";
    private const string CompareRejectedMarker = "compareRejected";
    private const string CensusSeparator = " ";
    private const string PassedSlotFilterOnce = "PassedSlotFilter=1";
    private const string BetterRotationOnce = "BetterRotation=1";
    private const string NoHigherBlockOrder = "HigherBlockOrder=0";
    private const string NoneAccepted = "Accepted=0";

    private const string SplitEarlyStart = "06:00";
    private const string SplitEarlyEnd = "07:00";
    private const string SplitLateStart = "19:00";
    private const string SplitLateEnd = "20:00";
    private const string DayEarlyStart = "08:00";
    private const string DayEarlyEnd = "14:00";
    private const string DayLateStart = "15:00";
    private const string DayLateEnd = "21:00";
    private const double SplitHours = 1;
    private const double DayHours = 6;
    private const int SplitEarlyHour = 6;
    private const int SplitLateHour = 19;
    private const int DayEarlyHour = 8;
    private const int DayLateHour = 15;

    private const int EarlyKind = 0;
    private const int LateKind = 1;
    private const int NightKind = 2;

    private const int EarlyHour = 6;
    private const int LateHour = 14;
    private const int NightHour = 23;

    private static readonly DateOnly FirstDay = new(2026, 4, 20);
    private static readonly DateOnly SecondDay = FirstDay.AddDays(1);
    private static readonly DateOnly ThirdDay = FirstDay.AddDays(2);
    private static readonly DateOnly SixthDay = FirstDay.AddDays(5);
    private static readonly DateOnly SeventhDay = FirstDay.AddDays(6);
    private static readonly DateOnly LastDay = FirstDay.AddDays(7);

    private static readonly Guid EarlySlot = new("00000000-0000-0000-0000-00000000a0e0");
    private static readonly Guid FirstLateSlot = new("00000000-0000-0000-0000-00000000a0e1");
    private static readonly Guid SecondLateSlot = new("00000000-0000-0000-0000-00000000a0e2");
    private static readonly Guid NightSlot = new("00000000-0000-0000-0000-00000000a0e3");
    private static readonly Guid SplitEarlySlot = new("00000000-0000-0000-0000-00000000a0e4");
    private static readonly Guid SplitLateSlot = new("00000000-0000-0000-0000-00000000a0e5");
    private static readonly Guid DayEarlySlot = new("00000000-0000-0000-0000-00000000a0e6");
    private static readonly Guid DayLateSlot = new("00000000-0000-0000-0000-00000000a0e7");

    /// <param name="lockFirstAgentNights">Marks MA-1's night block as locked work</param>
    /// <param name="secondLateHours">Hours of MA-2's second late block; fewer hours make the swap cost MA-1 hours</param>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator) BuildPlan(
        bool lockFirstAgentNights = false, double secondLateHours = FullHours)
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = LastDay,
            Agents = [Agent(FirstAgent), Agent(SecondAgent)],
            Shifts =
            [
                Slot(FirstDay, EarlySlot, EarlyStart, EarlyEnd, FullHours),
                Slot(SecondDay, EarlySlot, EarlyStart, EarlyEnd, FullHours),
                Slot(SecondDay, FirstLateSlot, LateStart, LateEnd, FullHours),
                Slot(ThirdDay, FirstLateSlot, LateStart, LateEnd, FullHours),
                Slot(SixthDay, SecondLateSlot, LateStart, LateEnd, secondLateHours),
                Slot(SeventhDay, SecondLateSlot, LateStart, LateEnd, secondLateHours),
                Slot(SixthDay, NightSlot, NightStart, NightEnd, FullHours),
                Slot(SeventhDay, NightSlot, NightStart, NightEnd, FullHours),
            ],
        };

        var scenario = new CoreScenario
        {
            Id = "rotation-balance",
            Tokens =
            [
                Token(FirstAgent, FirstDay, EarlySlot, EarlyKind, EarlyHour, FullHours),
                Token(FirstAgent, SecondDay, EarlySlot, EarlyKind, EarlyHour, FullHours),
                Token(FirstAgent, SixthDay, NightSlot, NightKind, NightHour, FullHours, lockFirstAgentNights),
                Token(FirstAgent, SeventhDay, NightSlot, NightKind, NightHour, FullHours, lockFirstAgentNights),
                Token(SecondAgent, SecondDay, FirstLateSlot, LateKind, LateHour, FullHours),
                Token(SecondAgent, ThirdDay, FirstLateSlot, LateKind, LateHour, FullHours),
                Token(SecondAgent, SixthDay, SecondLateSlot, LateKind, LateHour, secondLateHours),
                Token(SecondAgent, SeventhDay, SecondLateSlot, LateKind, LateHour, secondLateHours),
            ],
        };

        var evaluator = TokenFitnessEvaluator.Create(context);
        evaluator.Evaluate(scenario, context);
        return (context, scenario, evaluator);
    }

    /// <summary>
    /// MA-1 works a split duty on day 6 (early 06-07 plus 19-20) and a single early on day 7, so its package is pure
    /// early, but only day 7 forms a kind block (a split-duty day is never part of one). MA-2 works night on day 3 and
    /// late on day 7; after a night the ideal successor is early. Exchanging the two day-7 blocks gives MA-2 the ideal
    /// early and leaves MA-1 with early then late in one package.
    /// </summary>
    private static (CoreWizardContext Context, CoreScenario Scenario, TokenFitnessEvaluator Evaluator) BuildSplitDutyPlan()
    {
        var context = new CoreWizardContext
        {
            PeriodFrom = FirstDay,
            PeriodUntil = LastDay,
            Agents = [Agent(FirstAgent), Agent(SecondAgent)],
            Shifts =
            [
                Slot(ThirdDay, NightSlot, NightStart, NightEnd, FullHours),
                Slot(SixthDay, SplitEarlySlot, SplitEarlyStart, SplitEarlyEnd, SplitHours),
                Slot(SixthDay, SplitLateSlot, SplitLateStart, SplitLateEnd, SplitHours),
                Slot(SeventhDay, DayEarlySlot, DayEarlyStart, DayEarlyEnd, DayHours),
                Slot(SeventhDay, DayLateSlot, DayLateStart, DayLateEnd, DayHours),
            ],
        };

        var scenario = new CoreScenario
        {
            Id = "rotation-balance-split-duty",
            Tokens =
            [
                Token(FirstAgent, SixthDay, SplitEarlySlot, EarlyKind, SplitEarlyHour, SplitHours),
                Token(FirstAgent, SixthDay, SplitLateSlot, LateKind, SplitLateHour, SplitHours),
                Token(FirstAgent, SeventhDay, DayEarlySlot, EarlyKind, DayEarlyHour, DayHours),
                Token(SecondAgent, ThirdDay, NightSlot, NightKind, NightHour, FullHours),
                Token(SecondAgent, SeventhDay, DayLateSlot, LateKind, DayLateHour, DayHours),
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

    private static CoreShift Slot(DateOnly date, Guid shiftRefId, string start, string end, double hours)
        => new(
            Id: shiftRefId.ToString(),
            Name: Order,
            Date: date.ToString(DateFormat),
            StartTime: start,
            EndTime: end,
            Hours: hours,
            RequiredAssignments: 1,
            Priority: 0)
        {
            LocationContext = Order,
        };

    private static CoreToken Token(
        string agentId, DateOnly date, Guid shiftRefId, int kind, int startHour, double hours, bool locked = false)
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

    [Test]
    public void Apply_SwapGivesBothEmployeesTheIdealSuccessor_ExchangesTheWholeBlocks()
    {
        var (context, scenario, evaluator) = BuildPlan();
        var before = evaluator.EvaluateDetailed(scenario, context);

        var balanced = new RotationBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldNotBeSameAs(scenario);
        KindsOf(balanced, FirstAgent).ShouldBe([EarlyKind, EarlyKind, LateKind, LateKind]);
        KindsOf(balanced, SecondAgent).ShouldBe([LateKind, LateKind, NightKind, NightKind]);

        var after = evaluator.EvaluateDetailed(balanced, context);
        evaluator.Evaluate(scenario, context);
        evaluator.Compare(balanced, scenario).ShouldBeLessThan(0);
        after.Stage3Components.BlockOrder.ShouldBeGreaterThan(before.Stage3Components.BlockOrder);
        after.Stage0.ShouldBe(before.Stage0);
        balanced.Tokens.Count.ShouldBe(scenario.Tokens.Count);
        balanced.Tokens.Sum(t => t.TotalHours).ShouldBe(scenario.Tokens.Sum(t => t.TotalHours));
    }

    [Test]
    public void Apply_OneBlockIsLocked_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(lockFirstAgentNights: true);

        var balanced = new RotationBalancer().Apply(scenario, context, evaluator);

        balanced.ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// The second late block carries fewer hours, so the swap would move hours between the two employees. The rotation
    /// gain may not pay for the hours rule that ranks above it; the census shows the comparison as the refusing stage,
    /// so the test proves the gate and not an earlier filter.
    /// </summary>
    [Test]
    public void Apply_TheSwapWouldWorsenTheHoursStage_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildPlan(secondLateHours: ShortHours);
        var census = new List<string>();

        var balanced = new RotationBalancer().Apply(scenario, context, evaluator, census.Add);

        balanced.ShouldBeSameAs(scenario);
        census.ShouldContain(line => line.Contains(CompareRejectedMarker));
    }

    [Test]
    public void Apply_RunTwice_ProducesTheIdenticalPlan()
    {
        var (context, scenario, evaluator) = BuildPlan();
        var (secondContext, secondScenario, secondEvaluator) = BuildPlan();

        var first = new RotationBalancer().Apply(scenario, context, evaluator);
        var second = new RotationBalancer().Apply(secondScenario, secondContext, secondEvaluator);
        var again = new RotationBalancer().Apply(first, context, evaluator);

        OwnershipOf(second).ShouldBe(OwnershipOf(first));
        again.ShouldBeSameAs(first);
    }

    [Test]
    public void Apply_CancelledBeforeStart_ReturnsThePlanUnchanged()
    {
        var (context, scenario, evaluator) = BuildPlan();

        var balanced = new RotationBalancer().Apply(
            scenario, context, evaluator, cancellationToken: new CancellationToken(canceled: true));

        balanced.ShouldBeSameAs(scenario);
    }

    /// <summary>
    /// The only same-day pair is the two day-7 blocks. The hand-built exchange shows that it passes every gate except
    /// rule 7: rotation and block order rise and the lexicographic fitness improves, yet MA-1's pure early package
    /// becomes early-late. The census shows the candidate passed the slot filter and the rotation gate and stopped
    /// before the block-order gate — the place of the mixed-package check — so the refusal is that check alone.
    /// </summary>
    [Test]
    public void Apply_WholeBlockSwapWouldRaiseMixedPackages_LeavesThePlanAlone()
    {
        var (context, scenario, evaluator) = BuildSplitDutyPlan();
        var exchanged = new CoreScenario
        {
            Id = scenario.Id + "-exchanged",
            Tokens = scenario.Tokens
                .Select(t => t.Date != SeventhDay
                    ? t
                    : t with { AgentId = t.AgentId == FirstAgent ? SecondAgent : FirstAgent })
                .ToList(),
        };

        KindsOf(exchanged, FirstAgent).ShouldBe([EarlyKind, LateKind, LateKind]);
        KindsOf(exchanged, SecondAgent).ShouldBe([NightKind, EarlyKind]);
        MixedKindPackageTrace.Count(exchanged).ShouldBeGreaterThan(MixedKindPackageTrace.Count(scenario));
        var exchangedDetail = evaluator.EvaluateDetailed(exchanged, context);
        var scenarioDetail = evaluator.EvaluateDetailed(scenario, context);
        exchangedDetail.Stage3Components.BlockOrder.ShouldBeGreaterThan(scenarioDetail.Stage3Components.BlockOrder);
        exchangedDetail.Stage0.ShouldBe(scenarioDetail.Stage0);
        evaluator.Compare(exchanged, scenario).ShouldBeLessThan(0);

        var census = new List<string>();
        var balanced = new RotationBalancer().Apply(scenario, context, evaluator, census.Add);

        balanced.ShouldBeSameAs(scenario);
        var stages = census[0].Split(CensusSeparator);
        stages.ShouldContain(PassedSlotFilterOnce);
        stages.ShouldContain(BetterRotationOnce);
        stages.ShouldContain(NoHigherBlockOrder);
        stages.ShouldContain(NoneAccepted);
    }

    private static IReadOnlyList<string> OwnershipOf(CoreScenario scenario) => scenario.Tokens
        .OrderBy(t => t.Date)
        .ThenBy(t => t.ShiftRefId)
        .Select(t => $"{t.Date:yyyy-MM-dd}|{t.ShiftRefId}|{t.AgentId}|{t.ShiftTypeIndex}")
        .ToList();
}
