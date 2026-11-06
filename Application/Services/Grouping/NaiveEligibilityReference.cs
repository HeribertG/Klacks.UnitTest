// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Frozen copy of the original per-day eligibility scan (before the weekday-mask and per-client contract
/// index optimisation), adjusted to the rules that a day outside the client's membership window is OutsideMembership,
/// a member day with contract data (an active contract or the settings defaults) is evaluated with its weekday flags
/// and shift-work flag, and only a member day without any contract data is NoActiveContract. Serves only as the oracle of StaticEligibilityEvaluatorEquivalenceTests, which prove that the
/// optimised evaluator returns identical verdicts, reasons, weekday sets and active-contract flags.
/// </summary>
/// <param name="context">Pre-indexed inputs of one analysis.</param>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Application.Services.Grouping;

public sealed class NaiveEligibilityReference
{
    private static readonly IReadOnlyList<ShiftRequiredQualification> NoRequirements = [];
    private static readonly IReadOnlyList<ClientQualification> NoQualifications = [];
    private static readonly IReadOnlyList<ClientAvailability> NoAvailability = [];
    private static readonly IReadOnlyList<DateOnly> NoRunDays = [];

    private readonly GroupingEligibilityContext _context;
    private readonly Dictionary<GroupingEntityPair, EligibilityVerdict> _verdicts = new();
    private readonly Dictionary<GroupingEntityPair, IReadOnlySet<DayOfWeek>> _weekdays = new();

    public NaiveEligibilityReference(GroupingEligibilityContext context)
    {
        _context = context;
    }

    public EligibilityVerdict Evaluate(Guid clientId, Guid shiftId)
    {
        var key = new GroupingEntityPair(clientId, shiftId);
        if (_verdicts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var verdict = Compute(clientId, shiftId);
        _verdicts[key] = verdict;
        return verdict;
    }

    public bool HasActiveContractInPeriod(Guid clientId) =>
        _context.Contracts.Any(entry =>
            IsMember(clientId, entry.Key)
            && entry.Value.TryGetValue(clientId, out var contract)
            && contract.HasActiveContract);

    public IReadOnlySet<DayOfWeek> EligibleWeekdays(Guid clientId, Guid shiftId)
    {
        var key = new GroupingEntityPair(clientId, shiftId);
        if (_weekdays.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = new HashSet<DayOfWeek>();
        foreach (var day in RunDaysOf(shiftId))
        {
            if (result.Contains(day.DayOfWeek))
            {
                continue;
            }

            if (FirstFailure(clientId, shiftId, day) is null)
            {
                result.Add(day.DayOfWeek);
                if (result.Count == GroupingFeasibilityDefaults.DaysPerWeek)
                {
                    break;
                }
            }
        }

        _weekdays[key] = result;
        return result;
    }

    private EligibilityVerdict Compute(Guid clientId, Guid shiftId)
    {
        var blockedDays = new Dictionary<GroupingIneligibilityReason, int>();
        foreach (var day in RunDaysOf(shiftId))
        {
            var failure = FirstFailure(clientId, shiftId, day);
            if (failure is not GroupingIneligibilityReason reason)
            {
                return EligibilityVerdict.Eligible;
            }

            blockedDays[reason] = blockedDays.GetValueOrDefault(reason) + 1;
        }

        return EligibilityVerdict.Ineligible(DominantReason(blockedDays));
    }

    private static GroupingIneligibilityReason DominantReason(Dictionary<GroupingIneligibilityReason, int> blockedDays)
    {
        if (blockedDays.Count == 0)
        {
            return GroupingIneligibilityReason.NoActiveContract;
        }

        return blockedDays
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .First()
            .Key;
    }

    private GroupingIneligibilityReason? FirstFailure(Guid clientId, Guid shiftId, DateOnly day)
    {
        if (!IsMember(clientId, day))
        {
            return GroupingIneligibilityReason.OutsideMembership;
        }

        if (!_context.Contracts.TryGetValue(day, out var perClient)
            || !perClient.TryGetValue(clientId, out var contract))
        {
            return GroupingIneligibilityReason.NoActiveContract;
        }

        if (!WorksOn(contract, day.DayOfWeek))
        {
            return GroupingIneligibilityReason.WeekdayNotAllowed;
        }

        var shift = _context.Shifts[shiftId];
        if (!contract.PerformsShiftWork
            && ShiftTypeInference.FromSpan(shift.Start, shift.End) != ShiftTypeInference.EarlyIndex)
        {
            return GroupingIneligibilityReason.NotShiftWorker;
        }

        var requirements = _context.RequirementsByShift.GetValueOrDefault(shiftId, NoRequirements);
        if (requirements.Count > 0)
        {
            var held = _context.QualificationsByClient.GetValueOrDefault(clientId, NoQualifications);
            var blocking = EligibilityMatcher
                .FindMandatoryGaps(requirements, held, day, _context.ExpiredMandatoryBlocks)
                .Where(gap => gap.Severity == QualificationGapSeverity.Error)
                .ToList();
            if (blocking.Count > 0)
            {
                return blocking.All(gap => gap.IsBindingLevelBlock)
                    ? GroupingIneligibilityReason.MandatoryQualificationLevelTooLow
                    : GroupingIneligibilityReason.MandatoryQualificationMissing;
            }
        }

        if (_context.Blacklist.Contains(new GroupingEntityPair(clientId, shiftId)))
        {
            return GroupingIneligibilityReason.Blacklisted;
        }

        var availability = _context.AvailabilityByClientAndDay.GetValueOrDefault((clientId, day), NoAvailability)
            .Concat(_context.AvailabilityByClientAndDay.GetValueOrDefault((clientId, day.AddDays(1)), NoAvailability))
            .ToList();
        if (AvailabilityMatcher.IsUnavailable(availability, day, shift.Start, shift.End))
        {
            return GroupingIneligibilityReason.Unavailable;
        }

        return null;
    }

    private bool IsMember(Guid clientId, DateOnly day) =>
        !_context.MembershipWindows.TryGetValue(clientId, out var window) || window.Contains(day);

    private IReadOnlyList<DateOnly> RunDaysOf(Guid shiftId) =>
        _context.RunDays.GetValueOrDefault(shiftId, NoRunDays);

    private static bool WorksOn(EffectiveContractData contract, DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => contract.WorkOnMonday,
        DayOfWeek.Tuesday => contract.WorkOnTuesday,
        DayOfWeek.Wednesday => contract.WorkOnWednesday,
        DayOfWeek.Thursday => contract.WorkOnThursday,
        DayOfWeek.Friday => contract.WorkOnFriday,
        DayOfWeek.Saturday => contract.WorkOnSaturday,
        DayOfWeek.Sunday => contract.WorkOnSunday,
        _ => false,
    };
}
