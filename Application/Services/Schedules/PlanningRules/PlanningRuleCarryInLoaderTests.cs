// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

[TestFixture]
public class PlanningRuleCarryInLoaderTests
{
    private static readonly DateOnly From = new(2026, 11, 9);
    private static readonly DateOnly Until = new(2026, 11, 22);
    private static readonly CoreNightWindow NightWindow = new(new TimeOnly(23, 0), new TimeOnly(6, 0));

    private static PeriodCountRule YearNights(int threshold) =>
        new(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleCounterEvent.NightShift, RuleCalendarPeriod.Year, threshold);

    [Test]
    public void Ranges_CoverTheRestOfTheCountedPeriod_OnBothSides_OutsideTheCoveredWindow()
    {
        var ranges = PlanningRuleCarryInLoader.CarryInRanges([YearNights(25)], From, Until, coveredBoundaryDays: 7);

        ranges.Count.ShouldBe(2);
        ranges[0].ShouldBe((new DateOnly(2026, 1, 1), From.AddDays(-8)));
        ranges[1].ShouldBe((Until.AddDays(8), new DateOnly(2026, 12, 31)));
    }

    [Test]
    public void Ranges_AreEmpty_WhenTheCoveredWindowAlreadyHoldsEverything()
    {
        var run = new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1d, RuleShiftKind.Night, 3);

        PlanningRuleCarryInLoader.CarryInRanges([run], From, Until, coveredBoundaryDays: 7).ShouldBeEmpty();
        PlanningRuleCarryInLoader.CarryInRanges([run], From, Until, coveredBoundaryDays: 0)
            .ShouldBe([(From.AddDays(-3), From.AddDays(-1)), (Until.AddDays(1), Until.AddDays(3))]);
    }

    [Test]
    public void Ranges_RejectNegativeCoveredDays()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PlanningRuleCarryInLoader.CarryInRanges([YearNights(1)], From, Until, -1));
    }

    [Test]
    public async Task EngineBoundaryPlusCarryIn_CountsEveryNightExactlyOnce()
    {
        const int coveredDays = 7;
        var agentId = Guid.NewGuid();
        var agent = agentId.ToString();
        var store = new[]
        {
            Work(agentId, new DateOnly(2026, 3, 2)),
            Work(agentId, From.AddDays(-coveredDays)),
            Work(agentId, From.AddDays(-1)),
            Work(agentId, Until.AddDays(2)),
            Work(agentId, new DateOnly(2026, 12, 30)),
        };
        var reader = Substitute.For<IPlanningRuleDataReader>();
        reader.GetWorkSpansAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, Arg.Any<CancellationToken>())
            .Returns(call => store.Where(w => w.Date >= call.ArgAt<DateOnly>(1) && w.Date <= call.ArgAt<DateOnly>(2)).ToList());
        var rule = YearNights(6);

        var carryIn = await new PlanningRuleCarryInLoader(reader).LoadAsync([agentId], From, Until, [rule], null, coveredDays);
        var engineBoundary = store
            .Where(w => w.Date >= From.AddDays(-coveredDays) && w.Date <= Until.AddDays(coveredDays))
            .Select(PlanningRuleCarryInLoader.ToSegment);

        carryIn.Count.ShouldBe(2);
        var context = new RuleEvaluationContext(From, Until, [new RuleAgent(agent, NightWindow, 100m)], [.. engineBoundary, .. carryIn]);
        var plan = new RulePlan(context);
        plan.TryAdd(new RuleSegment(agent, From.AddDays(3), new TimeOnly(22, 0), new TimeOnly(6, 0), 2, 8m)).ShouldBeTrue();

        var evaluation = PlanRuleEvaluatorFactory.Create([rule], context).Evaluate(plan);

        var finding = evaluation.Findings.ShouldHaveSingleItem();
        finding.Observed.ShouldBe(6m, "5 stored nights + 1 planned night, none counted twice");
    }

    [Test]
    public void ToSegment_KeepsClockTimes_AndInfersTheShiftType()
    {
        var segment = PlanningRuleCarryInLoader.ToSegment(
            new PlanningRuleWorkSpan(Guid.NewGuid(), From, new TimeOnly(6, 0), new TimeOnly(14, 0), 8m));

        segment.Start.ShouldBe(new TimeOnly(6, 0));
        segment.End.ShouldBe(new TimeOnly(14, 0));
        segment.ShiftTypeIndex.ShouldBe(RuleTimeConstants.EarlyShiftTypeIndex);
        segment.Hours.ShouldBe(8m);
    }

    private static PlanningRuleWorkSpan Work(Guid clientId, DateOnly date) =>
        new(clientId, date, new TimeOnly(22, 0), new TimeOnly(6, 0), 8m);
}
