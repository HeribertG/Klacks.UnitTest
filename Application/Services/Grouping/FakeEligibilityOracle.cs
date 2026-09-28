// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Table-driven eligibility for plan and capacity tests: listed pairs are eligible on the listed
/// weekdays (all weekdays when none are given), every other pair is ineligible with the default reason
/// or a per-pair override. A client marked WithoutContract has no active contract day in the period and is
/// ineligible with NoActiveContract for every pair that is not explicitly allowed.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Grouping;

public sealed class FakeEligibilityOracle : IGroupingEligibilityOracle
{
    private static readonly IReadOnlySet<DayOfWeek> AllWeekdays = Enum.GetValues<DayOfWeek>().ToHashSet();

    private readonly Dictionary<GroupingEntityPair, IReadOnlySet<DayOfWeek>> _eligible = new();
    private readonly Dictionary<GroupingEntityPair, GroupingIneligibilityReason> _reasons = new();
    private readonly HashSet<Guid> _withoutContract = [];

    public GroupingIneligibilityReason DefaultReason { get; set; } = GroupingIneligibilityReason.MandatoryQualificationMissing;

    public FakeEligibilityOracle Allow(Guid clientId, Guid shiftId, params DayOfWeek[] weekdays)
    {
        _eligible[new GroupingEntityPair(clientId, shiftId)] = weekdays.Length == 0 ? AllWeekdays : weekdays.ToHashSet();
        return this;
    }

    public FakeEligibilityOracle Deny(Guid clientId, Guid shiftId, GroupingIneligibilityReason reason)
    {
        _reasons[new GroupingEntityPair(clientId, shiftId)] = reason;
        return this;
    }

    public FakeEligibilityOracle WithoutContract(Guid clientId)
    {
        _withoutContract.Add(clientId);
        return this;
    }

    public EligibilityVerdict Evaluate(Guid clientId, Guid shiftId)
    {
        var key = new GroupingEntityPair(clientId, shiftId);
        if (_eligible.ContainsKey(key))
        {
            return EligibilityVerdict.Eligible;
        }

        return EligibilityVerdict.Ineligible(_withoutContract.Contains(clientId)
            ? GroupingIneligibilityReason.NoActiveContract
            : _reasons.GetValueOrDefault(key, DefaultReason));
    }

    public bool HasActiveContractInPeriod(Guid clientId) => !_withoutContract.Contains(clientId);

    public bool IsEligible(Guid clientId, Guid shiftId) => Evaluate(clientId, shiftId).IsEligible;

    public IReadOnlySet<DayOfWeek> EligibleWeekdays(Guid clientId, Guid shiftId) =>
        _eligible.GetValueOrDefault(new GroupingEntityPair(clientId, shiftId)) ?? new HashSet<DayOfWeek>();
}
