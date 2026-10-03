// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Frozen copy of the CounterRuleEvaluator as it was before it became an adapter over the shared planning-rule
/// evaluator (Api 0d4d5b59d). Test oracle only: CounterRuleLegacyParityTests run a generated corpus through
/// this copy and through the production adapter and require byte-identical notification lists.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

public sealed class LegacyCounterRuleEvaluatorOracle
{
    private const int DaysPerWeek = 7;
    private const int MinutesPerDay = 24 * 60;

    private readonly ICounterRuleRepository _ruleRepository;
    private readonly DataBaseContext _context;
    private readonly IComplianceEnforcementResolver _enforcementResolver;
    private readonly IClientContractDataProvider _contractDataProvider;

    public LegacyCounterRuleEvaluatorOracle(
        ICounterRuleRepository ruleRepository,
        DataBaseContext context,
        IComplianceEnforcementResolver enforcementResolver,
        IClientContractDataProvider contractDataProvider)
    {
        _ruleRepository = ruleRepository;
        _context = context;
        _enforcementResolver = enforcementResolver;
        _contractDataProvider = contractDataProvider;
    }

    public async Task<List<ScheduleValidationNotificationDto>> EvaluateAsync(
        Guid clientId,
        string clientName,
        DateOnly asOfDate,
        Guid? analyseToken = null,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateCoreAsync(clientId, clientName, [asOfDate], [], analyseToken, cancellationToken);
    }

    public async Task<List<ScheduleValidationNotificationDto>> EvaluatePlannedAsync(
        Guid clientId,
        string clientName,
        IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)> plannedSlots,
        Guid? analyseToken = null,
        CancellationToken cancellationToken = default)
    {
        if (plannedSlots.Count == 0)
        {
            return [];
        }

        var candidateDates = plannedSlots.Select(s => s.Date).Distinct().ToList();
        return await EvaluateCoreAsync(clientId, clientName, candidateDates, plannedSlots, analyseToken, cancellationToken);
    }

    private async Task<List<ScheduleValidationNotificationDto>> EvaluateCoreAsync(
        Guid clientId,
        string clientName,
        IReadOnlyList<DateOnly> candidateDates,
        IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)> plannedSlots,
        Guid? analyseToken,
        CancellationToken cancellationToken)
    {
        var (rules, nightWindow) = await ResolveApplicableRulesAndNightWindowAsync(clientId, candidateDates.Max());
        if (rules.Count == 0)
        {
            return [];
        }

        var mode = await _enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule);

        var entries = new List<ScheduleValidationNotificationDto>();
        var reportedPeriods = new HashSet<(Guid RuleId, DateOnly PeriodStart)>();

        foreach (var rule in rules)
        {
            foreach (var date in candidateDates)
            {
                var (periodStart, periodEnd) = ResolvePeriod(rule.Period, date);
                if (!reportedPeriods.Add((rule.Id, periodStart)))
                {
                    continue;
                }

                var works = await LoadWorksAsync(clientId, periodStart, periodEnd, analyseToken, cancellationToken);
                var slotsInPeriod = plannedSlots
                    .Where(s => s.Date >= periodStart && s.Date <= periodEnd)
                    .Select(s => new WorkSegment(s.Date, s.StartTime, s.EndTime))
                    .ToList();

                var count = CountEvents(rule, works.Concat(slotsInPeriod).ToList(), nightWindow);
                if (count < rule.Threshold)
                {
                    continue;
                }

                entries.Add(BuildEntry(clientId, clientName, date, rule, count, rule.Enforcement ?? mode));
            }
        }

        return entries;
    }

    private sealed record WorkSegment(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)
    {
        public decimal DurationHours
        {
            get
            {
                var start = StartTime.ToTimeSpan();
                var end = EndTime.ToTimeSpan();
                var duration = end > start ? end - start : TimeSpan.FromHours(24) - start + end;
                return (decimal)duration.TotalHours;
            }
        }
    }

    private static int CountEvents(CounterRule rule, IReadOnlyList<WorkSegment> segments, (TimeOnly Start, TimeOnly End) nightWindow)
    {
        return rule.EventType switch
        {
            CounterEventType.NightShift => segments.Count(s =>
                NightOverlapMinutes(s.StartTime, s.EndTime, nightWindow.Start, nightWindow.End) > 0),
            CounterEventType.WorkedDayInWeek => segments.Select(s => s.Date).Distinct().Count(),
            CounterEventType.ShiftExceedingHours => segments.Count(s =>
                rule.HoursThreshold.HasValue && s.DurationHours > rule.HoursThreshold.Value),
            _ => 0,
        };
    }

    // Both the segment and the night window may wrap midnight. Normalizing the segment to
    // [start, start+duration) minutes and testing the window at its own offset and shifted by +-24 h
    // covers every wrap combination (a Saturday 22:00-07:00 segment against a 23:00-06:00 window).
    private static int NightOverlapMinutes(TimeOnly segmentStart, TimeOnly segmentEnd, TimeOnly windowStart, TimeOnly windowEnd)
    {
        var segStart = (int)segmentStart.ToTimeSpan().TotalMinutes;
        var segEnd = (int)segmentEnd.ToTimeSpan().TotalMinutes;
        if (segEnd <= segStart)
        {
            segEnd += MinutesPerDay;
        }

        var winStart = (int)windowStart.ToTimeSpan().TotalMinutes;
        var winEnd = (int)windowEnd.ToTimeSpan().TotalMinutes;
        if (winEnd <= winStart)
        {
            winEnd += MinutesPerDay;
        }

        var overlap = 0;
        foreach (var shift in new[] { -MinutesPerDay, 0, MinutesPerDay })
        {
            var start = Math.Max(segStart, winStart + shift);
            var end = Math.Min(segEnd, winEnd + shift);
            overlap = Math.Max(overlap, end - start);
        }

        return overlap;
    }

    private static (DateOnly Start, DateOnly End) ResolvePeriod(CounterPeriod period, DateOnly date)
    {
        switch (period)
        {
            case CounterPeriod.Week:
                var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % DaysPerWeek));
                return (monday, monday.AddDays(DaysPerWeek - 1));
            case CounterPeriod.Month:
                var monthStart = new DateOnly(date.Year, date.Month, 1);
                return (monthStart, monthStart.AddMonths(1).AddDays(-1));
            case CounterPeriod.Year:
                return (new DateOnly(date.Year, 1, 1), new DateOnly(date.Year, 12, 31));
            default:
                throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown counter period.");
        }
    }

    private async Task<List<WorkSegment>> LoadWorksAsync(
        Guid clientId,
        DateOnly periodStart,
        DateOnly periodEnd,
        Guid? analyseToken,
        CancellationToken cancellationToken)
    {
        var works = await _context.Work
            .AsNoTracking()
            .Where(w => w.ClientId == clientId
                && !w.IsDeleted
                && w.AnalyseToken == analyseToken
                && w.CurrentDate >= periodStart
                && w.CurrentDate <= periodEnd)
            .Select(w => new { w.CurrentDate, w.StartTime, w.EndTime })
            .ToListAsync(cancellationToken);

        return works.Select(w => new WorkSegment(w.CurrentDate, w.StartTime, w.EndTime)).ToList();
    }

    // One contract resolution serves BOTH concerns: industry scoping (SchedulingRuleId) and the K2
    // night window (NightStart/NightEnd already resolved rule -> contract -> settings -> default by
    // ClientContractDataProvider). The call is skipped only when no rule needs either - i.e. all rules
    // are global AND none counts night shifts.
    private async Task<(List<CounterRule> Rules, (TimeOnly Start, TimeOnly End) NightWindow)> ResolveApplicableRulesAndNightWindowAsync(
        Guid clientId, DateOnly asOfDate)
    {
        var defaultWindow = (
            ParseTimeOrDefault(SurchargeDefaults.NightStart, SurchargeDefaults.NightStart),
            ParseTimeOrDefault(SurchargeDefaults.NightEnd, SurchargeDefaults.NightEnd));

        var rules = await _ruleRepository.GetAllActiveAsync();
        if (rules.Count == 0)
        {
            return (rules, defaultWindow);
        }

        var needsContract = rules.Any(r => r.SchedulingRuleId != null)
            || rules.Any(r => r.EventType == CounterEventType.NightShift);
        if (!needsContract)
        {
            return (rules, defaultWindow);
        }

        var effectiveData = await _contractDataProvider.GetEffectiveContractDataAsync(clientId, asOfDate);
        var nightWindow = (
            ParseTimeOrDefault(effectiveData.NightStart, SurchargeDefaults.NightStart),
            ParseTimeOrDefault(effectiveData.NightEnd, SurchargeDefaults.NightEnd));

        var applicable = rules
            .Where(r => r.SchedulingRuleId == null || r.SchedulingRuleId == effectiveData.SchedulingRuleId)
            .ToList();
        return (applicable, nightWindow);
    }

    private static TimeOnly ParseTimeOrDefault(string? value, string fallback)
    {
        return TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : TimeOnly.Parse(fallback, CultureInfo.InvariantCulture);
    }

    private static ScheduleValidationNotificationDto BuildEntry(
        Guid clientId,
        string clientName,
        DateOnly reportDate,
        CounterRule rule,
        int count,
        RuleEnforcementMode mode)
    {
        var isBlocked = mode == RuleEnforcementMode.Block;
        var commentParams = new Dictionary<string, string>
        {
            ["event"] = rule.EventType.ToString(),
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
            ["threshold"] = rule.Threshold.ToString(CultureInfo.InvariantCulture),
            ["period"] = rule.Period.ToString(),
        };
        if (isBlocked)
        {
            commentParams[ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.CounterRule;
        }

        return new ScheduleValidationNotificationDto
        {
            Type = isBlocked ? ScheduleValidationType.Error : ScheduleValidationType.Warning,
            ClientId = clientId,
            ClientName = clientName,
            Date = reportDate,
            Comment = ScheduleValidationKeys.CounterRule,
            CommentParams = commentParams,
        };
    }
}
