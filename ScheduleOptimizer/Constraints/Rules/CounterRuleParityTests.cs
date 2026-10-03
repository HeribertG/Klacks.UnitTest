// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// Side-by-side parity of the pure PeriodCount evaluator with the API CounterRuleEvaluator: every counting
/// scenario of CounterRuleEvaluatorTests runs through both, and the fired/not-fired decision and the count
/// must agree. The evaluation day (asOf resp. the planned-slot date) is the planning period, every other
/// work is boundary carry-in. Enforcement mode and industry scoping are loader concerns and not part of
/// the pure evaluator, so those cases are not mirrored here.
/// </summary>
[TestFixture]
public class CounterRuleParityTests
{
    private const string AgentId = "parity-agent";
    private static readonly DateOnly Monday = new(2026, 7, 13);

    private static IEnumerable<CounterRuleParityCase> Cases()
    {
        yield return new("NoWorks", CounterEventType.NightShift, CounterPeriod.Year, 3, null, Monday, [], []);
        yield return new("NightShiftsReachYearThreshold", CounterEventType.NightShift, CounterPeriod.Year, 3, null, Monday,
        [
            (new DateOnly(2026, 2, 3), new TimeOnly(22, 0), new TimeOnly(6, 0)),
            (new DateOnly(2026, 4, 10), new TimeOnly(23, 30), new TimeOnly(7, 0)),
            (new DateOnly(2026, 7, 11), new TimeOnly(22, 0), new TimeOnly(7, 0)),
            (new DateOnly(2026, 5, 5), new TimeOnly(8, 0), new TimeOnly(16, 0)),
        ], []);
        yield return new("NightShiftsBelowThreshold", CounterEventType.NightShift, CounterPeriod.Year, 3, null, Monday,
        [
            (new DateOnly(2026, 2, 3), new TimeOnly(22, 0), new TimeOnly(6, 0)),
            (new DateOnly(2026, 4, 10), new TimeOnly(23, 30), new TimeOnly(7, 0)),
        ], []);
        yield return new("DayShiftsAreNotNight", CounterEventType.NightShift, CounterPeriod.Year, 1, null, Monday,
        [
            (new DateOnly(2026, 2, 3), new TimeOnly(8, 0), new TimeOnly(16, 0)),
            (new DateOnly(2026, 2, 4), new TimeOnly(6, 30), new TimeOnly(14, 0)),
        ], []);
        yield return new("ContractNightWindowOverride", CounterEventType.NightShift, CounterPeriod.Year, 1, null, Monday,
        [
            (new DateOnly(2026, 3, 3), new TimeOnly(20, 30), new TimeOnly(22, 0)),
        ], [], ("20:00", "05:00"));
        yield return new("DefaultWindowExcludesEveningShift", CounterEventType.NightShift, CounterPeriod.Year, 1, null, Monday,
        [
            (new DateOnly(2026, 3, 3), new TimeOnly(20, 30), new TimeOnly(22, 0)),
        ], []);
        yield return new("CrossMidnightOverlongShift", CounterEventType.ShiftExceedingHours, CounterPeriod.Month, 1, 13m, Monday,
        [
            (new DateOnly(2026, 7, 2), new TimeOnly(18, 0), new TimeOnly(8, 0)),
        ], []);
        yield return new("SixDistinctWorkedDaysInWeek", CounterEventType.WorkedDayInWeek, CounterPeriod.Week, 6, null, Monday.AddDays(5),
        [
            .. Enumerable.Range(0, 6).Select(i => (Monday.AddDays(i), new TimeOnly(8, 0), new TimeOnly(16, 0))),
            (Monday, new TimeOnly(18, 0), new TimeOnly(20, 0)),
        ], []);
        yield return new("FiveWorkedDaysInWeek", CounterEventType.WorkedDayInWeek, CounterPeriod.Week, 6, null, Monday.AddDays(4),
        [
            .. Enumerable.Range(0, 5).Select(i => (Monday.AddDays(i), new TimeOnly(8, 0), new TimeOnly(16, 0))),
        ], []);
        yield return new("OverlongShiftsReachMonthThreshold", CounterEventType.ShiftExceedingHours, CounterPeriod.Month, 2, 13m, Monday,
        [
            (new DateOnly(2026, 7, 2), new TimeOnly(7, 0), new TimeOnly(21, 30)),
            (new DateOnly(2026, 7, 9), new TimeOnly(6, 0), new TimeOnly(20, 0)),
            (new DateOnly(2026, 7, 15), new TimeOnly(8, 0), new TimeOnly(20, 0)),
        ], []);
        yield return new("PlannedNightSlotCompletesCount", CounterEventType.NightShift, CounterPeriod.Year, 2, null, new DateOnly(2026, 7, 11),
        [
            (new DateOnly(2026, 2, 3), new TimeOnly(22, 0), new TimeOnly(6, 0)),
        ],
        [
            (new DateOnly(2026, 7, 11), new TimeOnly(22, 0), new TimeOnly(7, 0)),
        ]);
        yield return new("WorkedDaysAcrossWeekBoundaryNotCombined", CounterEventType.WorkedDayInWeek, CounterPeriod.Week, 6, null, Monday.AddDays(7),
        [
            .. Enumerable.Range(3, 7).Select(i => (Monday.AddDays(i), new TimeOnly(8, 0), new TimeOnly(16, 0))),
        ], []);
        yield return new("PreviousYearNightsIgnored", CounterEventType.NightShift, CounterPeriod.Year, 2, null, new DateOnly(2026, 1, 5),
        [
            (new DateOnly(2025, 12, 30), new TimeOnly(22, 0), new TimeOnly(6, 0)),
            (new DateOnly(2026, 1, 5), new TimeOnly(22, 0), new TimeOnly(6, 0)),
        ], []);
    }

    [TestCaseSource(nameof(Cases))]
    public async Task PureEvaluator_MatchesCounterRuleEvaluator(CounterRuleParityCase parityCase)
    {
        var (apiFired, apiCount) = await RunApiAsync(parityCase);
        var (pureFired, pureCount) = RunPure(parityCase);

        pureFired.ShouldBe(apiFired);
        if (apiFired)
        {
            pureCount.ShouldBe(apiCount);
        }
    }

    private static async Task<(bool Fired, int Count)> RunApiAsync(CounterRuleParityCase parityCase)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new DataBaseContext(options, null!);

        var ruleRepository = Substitute.For<ICounterRuleRepository>();
        ruleRepository.GetAllActiveAsync().Returns(new List<CounterRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                EventType = parityCase.EventType,
                Period = parityCase.Period,
                Threshold = parityCase.Threshold,
                HoursThreshold = parityCase.HoursThreshold,
            },
        });
        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        var contractData = parityCase.NightWindowOverride is { } window
            ? new EffectiveContractData { NightStart = window.Start, NightEnd = window.End }
            : new EffectiveContractData();
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contractData);

        var clientId = Guid.NewGuid();
        foreach (var (date, start, end) in parityCase.Works)
        {
            context.Work.Add(new Klacks.Api.Domain.Models.Schedules.Work
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ShiftId = Guid.NewGuid(),
                CurrentDate = date,
                StartTime = start,
                EndTime = end,
                WorkTime = 8m,
            });
        }

        await context.SaveChangesAsync();

        var sut = new CounterRuleEvaluator(ruleRepository, context, enforcementResolver, contractDataProvider);
        var result = parityCase.PlannedSlots.Count > 0
            ? await sut.EvaluatePlannedAsync(clientId, AgentId, parityCase.PlannedSlots)
            : await sut.EvaluateAsync(clientId, AgentId, parityCase.AsOfDate);

        return result.Count == 0
            ? (false, 0)
            : (true, int.Parse(result.ShouldHaveSingleItem().CommentParams["count"], CultureInfo.InvariantCulture));
    }

    private static (bool Fired, int Count) RunPure(CounterRuleParityCase parityCase)
    {
        var (windowStart, windowEnd) = parityCase.NightWindowOverride ?? (SurchargeDefaults.NightStart, SurchargeDefaults.NightEnd);
        var agent = new RuleAgent(
            AgentId,
            new CoreNightWindow(
                TimeOnly.Parse(windowStart, CultureInfo.InvariantCulture),
                TimeOnly.Parse(windowEnd, CultureInfo.InvariantCulture)),
            100m);

        var segments = parityCase.Works.Concat(parityCase.PlannedSlots)
            .Select(w => new RuleSegment(AgentId, w.Date, w.Start, w.End, RuleTimeConstants.UnknownShiftTypeIndex, 0m))
            .ToList();
        var boundary = segments.Where(s => s.Date != parityCase.AsOfDate).ToList();
        var inPeriod = segments.Where(s => s.Date == parityCase.AsOfDate).ToArray();

        var context = new RuleEvaluationContext(parityCase.AsOfDate, parityCase.AsOfDate, [agent], boundary);
        var plan = RuleTestFactory.Plan(context, inPeriod);
        var rule = new PeriodCountRule(
            Guid.NewGuid(),
            RuleSeverity.Hard,
            1,
            Enum.Parse<RuleCounterEvent>(parityCase.EventType.ToString()),
            Enum.Parse<RuleCalendarPeriod>(parityCase.Period.ToString()),
            parityCase.Threshold,
            parityCase.HoursThreshold);

        var result = PlanRuleEvaluatorFactory.Create([rule], context).Evaluate(plan);
        return result.Findings.Count == 0
            ? (false, 0)
            : (true, (int)result.Findings.ShouldHaveSingleItem().Observed);
    }
}
