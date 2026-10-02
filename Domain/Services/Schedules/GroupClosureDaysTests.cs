// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the company-holiday rule of Paket D: a day closes only when the subtree has active members and every
/// one of them is away the whole day. Activity is the Membership window intersected with the optional
/// GroupItem validity.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class GroupClosureDaysTests
{
    private static readonly DateOnly Monday = new(2026, 12, 21);
    private static readonly DateOnly Friday = new(2026, 12, 25);

    private static GroupMembershipWindow Window(
        Guid clientId,
        DateOnly? membershipFrom = null,
        DateOnly? membershipUntil = null,
        DateOnly? groupItemFrom = null,
        DateOnly? groupItemUntil = null) =>
        new(clientId, membershipFrom ?? new DateOnly(2020, 1, 1), membershipUntil, groupItemFrom, groupItemUntil);

    [Test]
    public void AllActiveMembersAbsentTheWholeDay_IsAClosureDay()
    {
        var anna = Guid.NewGuid();
        var ben = Guid.NewGuid();

        var days = GroupClosureDays.Compute(
            [Window(anna), Window(ben)],
            [new ClientFullDayAbsence(anna, Monday), new ClientFullDayAbsence(ben, Monday)],
            Monday,
            Friday);

        days.ShouldBe(new[] { Monday }, ignoreOrder: true);
    }

    [Test]
    public void OneActiveMemberPresent_IsNoClosureDay()
    {
        var anna = Guid.NewGuid();
        var ben = Guid.NewGuid();

        var days = GroupClosureDays.Compute(
            [Window(anna), Window(ben)],
            [new ClientFullDayAbsence(anna, Monday)],
            Monday,
            Friday);

        days.ShouldBeEmpty();
    }

    [Test]
    public void NoActiveMember_IsNoClosureDay()
    {
        var days = GroupClosureDays.Compute([], [], Monday, Friday);

        days.ShouldBeEmpty();
    }

    [Test]
    public void MemberWhoseMembershipEnded_DoesNotKeepTheDayOpen()
    {
        var anna = Guid.NewGuid();
        var leaver = Guid.NewGuid();

        var days = GroupClosureDays.Compute(
            [Window(anna), Window(leaver, membershipUntil: Monday.AddDays(-1))],
            [new ClientFullDayAbsence(anna, Monday)],
            Monday,
            Monday);

        days.ShouldBe(new[] { Monday });
    }

    [Test]
    public void GroupItemValidity_LimitsActivity()
    {
        var anna = Guid.NewGuid();
        var later = Guid.NewGuid();

        var days = GroupClosureDays.Compute(
            [Window(anna), Window(later, groupItemFrom: Monday.AddDays(1))],
            [new ClientFullDayAbsence(anna, Monday), new ClientFullDayAbsence(anna, Monday.AddDays(1))],
            Monday,
            Monday.AddDays(1));

        days.ShouldBe(new[] { Monday });
    }

    [Test]
    public void MemberWithSeveralWindows_CountsOnce()
    {
        var anna = Guid.NewGuid();

        var days = GroupClosureDays.Compute(
            [Window(anna), Window(anna, groupItemFrom: Monday)],
            [new ClientFullDayAbsence(anna, Friday)],
            Monday,
            Friday);

        days.ShouldBe(new[] { Friday });
    }

    [Test]
    public void ActivityStartsAfterTheDay_IsNoClosureDay()
    {
        var future = Guid.NewGuid();

        var days = GroupClosureDays.Compute(
            [Window(future, membershipFrom: Friday)],
            [new ClientFullDayAbsence(future, Monday)],
            Monday,
            Monday);

        days.ShouldBeEmpty();
    }
}
