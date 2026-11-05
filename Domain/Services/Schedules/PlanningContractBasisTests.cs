// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// The one choice of an employee's planning master data that Wizard 1 and the Harmonizer family share: first member
/// day with an active contract, else first member day with the settings defaults, nothing without a member day.
/// </summary>
[TestFixture]
public class PlanningContractBasisTests
{
    private const int PeriodDays = 7;
    private const int JoinOffsetDays = 3;
    private static readonly DateOnly From = new(2026, 1, 5);
    private static readonly DateOnly Until = From.AddDays(PeriodDays - 1);
    private static readonly Guid AgentId = Guid.NewGuid();

    [Test]
    public void ActiveContractInsideThePeriod_WinsOverEarlierDefaults()
    {
        var byDate = Range(offset => offset < JoinOffsetDays ? Defaults(shiftWork: false) : Contract(shiftWork: true));

        var basis = PlanningContractBasis.Select([AgentId], From, Until, byDate, NoWindows());

        basis[AgentId].PerformsShiftWork.ShouldBeTrue();
        basis[AgentId].HasActiveContract.ShouldBeTrue();
    }

    [Test]
    public void WithoutAnyContract_TheFirstMemberDayDefaultsAreUsed()
    {
        var byDate = Range(_ => Defaults(shiftWork: true));

        var basis = PlanningContractBasis.Select([AgentId], From, Until, byDate, NoWindows());

        basis[AgentId].PerformsShiftWork.ShouldBeTrue();
        basis[AgentId].HasActiveContract.ShouldBeFalse();
    }

    [Test]
    public void MidPeriodJoiner_DaysBeforeTheEntryAreIgnored()
    {
        var byDate = Range(offset => Contract(shiftWork: offset >= JoinOffsetDays));
        var windows = new Dictionary<Guid, MembershipWindow> { [AgentId] = new(From.AddDays(JoinOffsetDays), null) };

        var basis = PlanningContractBasis.Select([AgentId], From, Until, byDate, windows);

        basis[AgentId].PerformsShiftWork.ShouldBeTrue();
    }

    [Test]
    public void NoMemberDayInThePeriod_NoBasis()
    {
        var byDate = Range(_ => Contract(shiftWork: true));
        var windows = new Dictionary<Guid, MembershipWindow> { [AgentId] = new(From.AddDays(-PeriodDays), From.AddDays(-1)) };

        var basis = PlanningContractBasis.Select([AgentId], From, Until, byDate, windows);

        basis.ContainsKey(AgentId).ShouldBeFalse();
    }

    [Test]
    public void MissingDateEntries_AreSkipped()
    {
        var byDate = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>
        {
            [Until] = new() { [AgentId] = Contract(shiftWork: true) },
        };

        var basis = PlanningContractBasis.Select([AgentId], From, Until, byDate, NoWindows());

        basis[AgentId].PerformsShiftWork.ShouldBeTrue();
    }

    private static Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>> Range(Func<int, EffectiveContractData> dataForOffset)
    {
        var result = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var offset = 0; offset < PeriodDays; offset++)
        {
            result[From.AddDays(offset)] = new Dictionary<Guid, EffectiveContractData> { [AgentId] = dataForOffset(offset) };
        }

        return result;
    }

    private static Dictionary<Guid, MembershipWindow> NoWindows() => new();

    private static EffectiveContractData Contract(bool shiftWork) => new() { HasActiveContract = true, PerformsShiftWork = shiftWork };

    private static EffectiveContractData Defaults(bool shiftWork) => new() { HasActiveContract = false, PerformsShiftWork = shiftWork };
}
