// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The sort keys of the automatic roster order are scaled to the planned period, member days only, so weekly and
/// monthly contracts compare fairly (owner decision 2026-11-10) and an entry inside the period counts only its member days.
/// </summary>
[TestFixture]
public class RosterRankHoursTests
{
    private const double Tolerance = 0.0000001;
    private static readonly DateOnly WeekStart = new(2026, 1, 5);
    private static readonly DateOnly WeekEnd = new(2026, 1, 11);
    private static readonly int JanuaryDays = DateTime.DaysInMonth(2026, 1);
    private static readonly int WeekDays = WeekEnd.DayNumber - WeekStart.DayNumber + 1;

    [Test]
    public void Weekly40_RanksAboveMonthly100_OnAOneWeekPlan()
    {
        var weekly = Guid.NewGuid();
        var monthly = Guid.NewGuid();
        var data = Week(new()
        {
            [weekly] = Data(PaymentInterval.Weekly, guaranteed: 40m, fullTime: 42m),
            [monthly] = Data(PaymentInterval.Monthly, guaranteed: 100m, fullTime: 180m),
        });

        var rank = RosterRankHours.Compute([weekly, monthly], WeekStart, WeekEnd, data, new Dictionary<Guid, MembershipWindow>());

        rank.GuaranteedHoursOf(weekly).ShouldBe(40, Tolerance);
        rank.GuaranteedHoursOf(monthly).ShouldBe(100.0 * WeekDays / JanuaryDays, Tolerance);
        rank.GuaranteedHoursOf(weekly).ShouldBeGreaterThan(rank.GuaranteedHoursOf(monthly));
        rank.FullTimeOf(weekly).ShouldBe(42, Tolerance);
        rank.FullTimeOf(monthly).ShouldBe(180.0 * WeekDays / JanuaryDays, Tolerance);
    }

    [Test]
    public void EntryInsideThePeriod_CountsOnlyTheMemberDays()
    {
        const int memberDays = 3;
        var joiner = Guid.NewGuid();
        var data = Week(new() { [joiner] = Data(PaymentInterval.Monthly, guaranteed: 160m, fullTime: 160m) });
        var windows = new Dictionary<Guid, MembershipWindow>
        {
            [joiner] = new(WeekEnd.AddDays(1 - memberDays), null),
        };

        var rank = RosterRankHours.Compute([joiner], WeekStart, WeekEnd, data, windows);

        rank.GuaranteedHoursOf(joiner).ShouldBe(160.0 * memberDays / JanuaryDays, Tolerance);
        rank.FullTimeOf(joiner).ShouldBe(160.0 * memberDays / JanuaryDays, Tolerance);
    }

    [Test]
    public void InheritedMonthlyValueOnAWeeklyContract_IsScaledByTheCalendarMonth()
    {
        var agent = Guid.NewGuid();
        var data = Week(new()
        {
            [agent] = Data(PaymentInterval.Weekly, guaranteed: 160m, fullTime: 42m) with
            {
                GuaranteedHoursBasisInterval = (int)PaymentInterval.MonthlyTargetHours,
            },
        });

        var rank = RosterRankHours.Compute([agent], WeekStart, WeekEnd, data, new Dictionary<Guid, MembershipWindow>());

        rank.GuaranteedHoursOf(agent).ShouldBe(160.0 * WeekDays / JanuaryDays, Tolerance);
        rank.FullTimeOf(agent).ShouldBe(42, Tolerance);
    }

    [Test]
    public void RepeatedAgentId_DoesNotThrowAndIsCountedOnce()
    {
        var agent = Guid.NewGuid();
        var data = Week(new() { [agent] = Data(PaymentInterval.Weekly, guaranteed: 40m, fullTime: 42m) });
        var windows = new Dictionary<Guid, MembershipWindow>();

        var once = RosterRankHours.Compute([agent], WeekStart, WeekEnd, data, windows);
        var repeated = RosterRankHours.Compute([agent, agent], WeekStart, WeekEnd, data, windows);

        repeated.GuaranteedHoursOf(agent).ShouldBe(once.GuaranteedHoursOf(agent));
        repeated.FullTimeOf(agent).ShouldBe(once.FullTimeOf(agent));
        repeated.GuaranteedHoursOf(agent).ShouldBe(40, Tolerance);
    }

    [Test]
    public void UnknownAgent_RanksWithZero()
    {
        var rank = RosterRankHours.Compute([], WeekStart, WeekEnd, Week(new()), new Dictionary<Guid, MembershipWindow>());

        rank.GuaranteedHoursOf(Guid.NewGuid()).ShouldBe(0.0);
        rank.FullTimeOf(Guid.NewGuid()).ShouldBe(0.0);
    }

    private static EffectiveContractData Data(PaymentInterval interval, decimal guaranteed, decimal fullTime) => new()
    {
        HasActiveContract = true,
        PaymentInterval = (int)interval,
        GuaranteedHours = guaranteed,
        FullTime = fullTime,
    };

    private static Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>> Week(Dictionary<Guid, EffectiveContractData> perAgent)
    {
        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = new Dictionary<Guid, EffectiveContractData>(perAgent);
        }

        return range;
    }
}
