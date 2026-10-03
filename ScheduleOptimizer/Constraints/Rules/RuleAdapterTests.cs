// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Engine adapters: Wizard-1 context/scenario (night window, workload, boundary locked and existing works,
/// in-period existing works) and the bitmap paths (break assignments as free days, boundary breaks left out,
/// merged cells), plus the missing-clock-time trap of default DateTime spans.
/// </summary>
[TestFixture]
public class RuleAdapterTests
{
    private const string AgentId = "agent-1";
    private static readonly DateOnly From = new(2026, 7, 13);
    private static readonly DateOnly Until = From.AddDays(13);
    private const int NoMinimumOverlap = 0;
    private const int DefaultMinimumOverlap = 60;

    private static readonly CoreNightWindow Window = new(new TimeOnly(23, 0), new TimeOnly(6, 0));

    [Test]
    public void FromWizardContext_MapsAgentNightWindowAndWorkload()
    {
        var wizard = WizardContext([WizardAgent() with { NightWindow = Window, FullTime = 180, GuaranteedHours = 135 }]);

        var context = RuleEvaluationContextFactory.FromWizardContext(wizard, NoMinimumOverlap);

        var agent = context.Agents.ShouldHaveSingleItem();
        agent.NightWindow.ShouldBe(Window);
        agent.WorkloadPercent.ShouldBe(75m);
        context.DayCount.ShouldBe(14);
    }

    [Test]
    public void BothFactories_PassTheNightMinimumOverlapOnToEveryAgent()
    {
        RuleEvaluationContextFactory.FromWizardContext(WizardContext([WizardAgent()]), DefaultMinimumOverlap)
            .Agents.ShouldHaveSingleItem().NightRuleMinOverlapMinutes.ShouldBe(DefaultMinimumOverlap);
    }

    [Test]
    public void FromWizardContext_WithoutFullTime_CountsAsFullTimeAndWithoutWindow()
    {
        var context = RuleEvaluationContextFactory.FromWizardContext(WizardContext([WizardAgent()]), NoMinimumOverlap);

        context.Agents[0].WorkloadPercent.ShouldBe(100m);
        context.Agents[0].NightWindow.ShouldBeNull();
    }

    [Test]
    public void FromWizardContext_BoundaryLockedAndExistingWorks_ExtendRunAcrossPeriodStart()
    {
        var wizard = new CoreWizardContext
        {
            PeriodFrom = From,
            PeriodUntil = Until,
            Agents = [WizardAgent() with { NightWindow = Window }],
            BoundaryLockedWorks = [new CoreLockedWork("w1", AgentId, From.AddDays(-2), 2, 8m, At(From.AddDays(-2), 22), At(From.AddDays(-1), 6), Guid.NewGuid(), null)],
            BoundaryExistingWorkBlockers = [new CoreExistingWorkBlocker(AgentId, From.AddDays(-1), At(From.AddDays(-1), 22), At(From, 6))],
            BoundaryBreakBlockers = [new CoreBreakBlocker(AgentId, From.AddDays(-4), From.AddDays(-3), "vacation")],
        };
        var context = RuleEvaluationContextFactory.FromWizardContext(wizard, NoMinimumOverlap);
        var scenario = new CoreScenario { Tokens = [Token(From, 22, 6, 2)] };
        var plan = RulePlanFactory.FromScenario(context, scenario, wizard);

        var finding = Evaluate(context, plan, MaxNights(2)).Findings.ShouldHaveSingleItem();

        finding.Date.ShouldBe(From.AddDays(-2));
        finding.Observed.ShouldBe(3m);
    }

    [Test]
    public void FromScenario_AddsInPeriodExistingWorks()
    {
        var wizard = new CoreWizardContext
        {
            PeriodFrom = From,
            PeriodUntil = Until,
            Agents = [WizardAgent() with { NightWindow = Window }],
            ExistingWorkBlockers = [new CoreExistingWorkBlocker(AgentId, From.AddDays(1), At(From.AddDays(1), 22), At(From.AddDays(2), 6))],
        };
        var context = RuleEvaluationContextFactory.FromWizardContext(wizard, NoMinimumOverlap);
        var scenario = new CoreScenario { Tokens = [Token(From, 22, 6, 2), Token(From.AddDays(2), 22, 6, 2)] };

        var plan = RulePlanFactory.FromScenario(context, scenario, wizard);

        Evaluate(context, plan, MaxNights(2)).HardCount.ShouldBe(1);
        Evaluate(context, RulePlanFactory.FromAssignments(context, [.. scenario.Tokens.Select(AssignmentView.FromToken)]), MaxNights(2)).HardCount.ShouldBe(0);
    }

    [Test]
    public void AssignmentWithoutClockTimes_IsNotA24HourNightShift()
    {
        var context = new RuleEvaluationContext(From, Until, [new RuleAgent(AgentId, Window, 100m)], []);
        var timeless = new AssignmentView(AgentId, From, Guid.NewGuid(), 0, 8m, default, default, null, false);

        var plan = RulePlanFactory.FromAssignments(context, [timeless]);

        var day = plan.Get(0, 0);
        day.IsWork.ShouldBeTrue();
        day.Has(RuleShiftKind.Night).ShouldBeFalse();
        day.CountSegmentsLongerThan(8 * 60).ShouldBe(0);
        day.CountSegmentsLongerThan((8 * 60) - 1).ShouldBe(1);
    }

    [Test]
    public void AssignmentWithoutClockTimes_FallsBackToNightShiftType()
    {
        var context = new RuleEvaluationContext(From, Until, [new RuleAgent(AgentId, Window, 100m)], []);
        var timelessNight = new AssignmentView(AgentId, From, Guid.NewGuid(), 2, 8m, default, default, null, false);

        RulePlanFactory.FromAssignments(context, [timelessNight]).Get(0, 0).Has(RuleShiftKind.Night).ShouldBeTrue();
    }

    [Test]
    public void FromBitmapInput_BreakAssignmentIsFree_AndBoundaryBreakIsIgnored()
    {
        var input = new BitmapInput(
            [new BitmapAgent(AgentId, "n/a", 0m, new HashSet<CellSymbol>(), NightWindow: Window, WorkloadPercent: 60m)],
            From,
            Until,
            [
                NightAssignment(From),
                NightAssignment(From.AddDays(1)),
                new BitmapAssignment(AgentId, From.AddDays(2), CellSymbol.Break, Guid.Empty, [], true, Hours: 8m),
                NightAssignment(From.AddDays(3)),
            ],
            BoundaryAssignments:
            [
                new BitmapAssignment(AgentId, From.AddDays(-1), CellSymbol.Break, Guid.Empty, [], true, Hours: 8m),
                NightAssignment(From.AddDays(-2)),
            ]);

        var context = RuleEvaluationContextFactory.FromBitmap(input, NoMinimumOverlap);
        var plan = RulePlanFactory.FromBitmapInput(context, input);

        context.Agents[0].WorkloadPercent.ShouldBe(60m);
        plan.Get(0, 2).IsWork.ShouldBeFalse();
        Evaluate(context, plan, MaxNights(2)).Findings.ShouldBeEmpty();
        Evaluate(context, plan, MaxNights(1)).HardCount.ShouldBe(1);
    }

    [Test]
    public void FromBitmapInput_KeepsOneSegmentPerWork_FromBitmapMergesTheCell()
    {
        var agent = new BitmapAgent(AgentId, "n/a", 0m, new HashSet<CellSymbol>(), NightWindow: Window);
        var input = new BitmapInput(
            [agent],
            From,
            Until,
            [
                new BitmapAssignment(AgentId, From, CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, At(From, 0), At(From, 3), 3m),
                new BitmapAssignment(AgentId, From, CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, At(From, 23), At(From.AddDays(1), 2), 3m),
            ]);
        var context = RuleEvaluationContextFactory.FromBitmap(input, NoMinimumOverlap);
        var cells = new Cell[1, 14];
        for (var d = 0; d < 14; d++)
        {
            cells[0, d] = Cell.Free();
        }

        cells[0, 0] = new Cell(CellSymbol.Night, Guid.NewGuid(), [], false, At(From, 0), At(From.AddDays(1), 2), 6m);
        var bitmap = new HarmonyBitmap([agent], [.. Enumerable.Range(0, 14).Select(From.AddDays)], cells);

        RulePlanFactory.FromBitmapInput(context, input).Get(0, 0).NightSegmentCount.ShouldBe(2);
        RulePlanFactory.FromBitmap(context, bitmap).Get(0, 0).NightSegmentCount.ShouldBe(1);
    }

    [Test]
    public void OtherSymbol_IsNeitherEarlyNorLate()
    {
        RuleSegmentMapper.ToShiftTypeIndex(CellSymbol.Other).ShouldBe(RuleTimeConstants.UnknownShiftTypeIndex);
        RuleSegmentMapper.IsWorked(CellSymbol.Other).ShouldBeTrue();
        RuleSegmentMapper.IsWorked(CellSymbol.Break).ShouldBeFalse();
        RuleSegmentMapper.IsWorked(CellSymbol.Free).ShouldBeFalse();
    }

    private static RuleEvaluation Evaluate(RuleEvaluationContext context, RulePlan plan, PlanRule rule)
        => PlanRuleEvaluatorFactory.Create([rule], context).Evaluate(plan);

    private static MaxConsecutiveOfKindRule MaxNights(int maxRun)
        => new(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, maxRun);

    private static CoreWizardContext WizardContext(IReadOnlyList<CoreAgent> agents)
        => new() { PeriodFrom = From, PeriodUntil = Until, Agents = agents };

    private static CoreAgent WizardAgent() => new(AgentId, 0, 0, 6, 11, 0.5, 10, 50, 2);

    private static DateTime At(DateOnly date, int hour) => date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);

    private static CoreToken Token(DateOnly date, int startHour, int endHour, int shiftType)
    {
        var start = At(date, startHour);
        var end = endHour > startHour ? At(date, endHour) : At(date.AddDays(1), endHour);
        return new CoreToken(["w"], shiftType, date, 8m, start, end, Guid.NewGuid(), 0, false, null, Guid.NewGuid(), AgentId);
    }

    private static BitmapAssignment NightAssignment(DateOnly date)
        => new(AgentId, date, CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false, At(date, 22), At(date.AddDays(1), 6), 8m);
}
