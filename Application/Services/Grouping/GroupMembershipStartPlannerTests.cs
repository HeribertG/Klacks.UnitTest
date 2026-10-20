// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupMembershipStartPlanner and GroupMembershipStartPlan: Fixed mode starts everybody on the given date
/// (else the caller's default, else the company today) and counts the persons whose employment entry lies before it; EntryDate
/// mode starts each person on their own entry and falls back to the company today for a person without a membership record;
/// all dates are UTC midnight; the group start never lies after the company today.
/// </summary>

using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupMembershipStartPlannerTests
{
    private static readonly DateTime Today = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Early = Guid.NewGuid();
    private static readonly Guid Late = Guid.NewGuid();
    private static readonly Guid NoRecord = Guid.NewGuid();

    private ICompanyClock _clock = null!;

    [SetUp]
    public void Setup()
    {
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
    }

    private async Task<GroupMembershipStartPlan> PlanAsync(
        GroupMembershipStartMode mode, DateTime? given = null, DateTime? defaultStart = null)
    {
        var planner = TestGroupMembershipStart.Planner(
            _clock, (Early, new DateOnly(2024, 2, 1)), (Late, new DateOnly(2026, 11, 3)));
        return await planner.PlanAsync([Early, Late, NoRecord], given, mode, defaultStart, CancellationToken.None);
    }

    [Test]
    public async Task Fixed_WithoutAnyDate_StartsEverybodyOnTheCompanyToday()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.Fixed);

        plan.StartFor(Early).ShouldBe(Today);
        plan.StartFor(NoRecord).ShouldBe(Today);
        plan.StartFor(Early).Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task Fixed_PrefersTheGivenDate_OverTheCallersDefault()
    {
        var given = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var defaultStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        (await PlanAsync(GroupMembershipStartMode.Fixed, given, defaultStart)).StartFor(Early).ShouldBe(given);
        (await PlanAsync(GroupMembershipStartMode.Fixed, null, defaultStart)).StartFor(Early).ShouldBe(defaultStart);
    }

    [Test]
    public async Task Fixed_DescribeCountsThePersonsWhoEnteredBeforeTheStart()
    {
        var info = (await PlanAsync(GroupMembershipStartMode.Fixed)).Describe([Early, Late, NoRecord]);

        info.Mode.ShouldBe(GroupMembershipStartMode.Fixed);
        info.StartDateGiven.ShouldBeFalse();
        info.StartDate.ShouldBe("2026-10-20");
        info.PersonCount.ShouldBe(3);
        info.PersonsEnteredBeforeStartCount.ShouldBe(1);
        info.EarliestEntryDate.ShouldBe("2024-02-01");
        info.PersonsWithoutEntryDateCount.ShouldBe(1);
    }

    [Test]
    public async Task Fixed_EntryOnTheStartDay_IsNotCounted()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.Fixed, new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        plan.Describe([Early]).PersonsEnteredBeforeStartCount.ShouldBe(0);
        plan.Describe([Early]).EarliestEntryDate.ShouldBeNull();
    }

    [Test]
    public async Task EntryDate_StartsEachPersonOnTheirOwnEntry_AndNoRecordOnTheCompanyToday()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.EntryDate, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        plan.StartFor(Early).ShouldBe(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        plan.StartFor(Late).ShouldBe(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));
        plan.StartFor(NoRecord).ShouldBe(Today);
        plan.StartFor(Early).Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task EntryDate_DescribeReportsTheRange_AndNoPersonIsBlue()
    {
        var info = (await PlanAsync(GroupMembershipStartMode.EntryDate)).Describe([Early, Late, NoRecord]);

        info.Mode.ShouldBe(GroupMembershipStartMode.EntryDate);
        info.StartDate.ShouldBeNull();
        info.EarliestStartDate.ShouldBe("2024-02-01");
        info.LatestStartDate.ShouldBe("2026-11-03");
        info.PersonsEnteredBeforeStartCount.ShouldBe(0);
        info.PersonsWithoutEntryDateCount.ShouldBe(1);
    }

    [Test]
    public async Task GroupStart_InEntryMode_IsTheEarliestStart_ButNeverAfterToday()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.EntryDate);

        plan.GroupStart([Early, Late]).ShouldBe(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        plan.GroupStart([Late]).ShouldBe(Today);
        plan.GroupStart([]).ShouldBe(Today);
    }

    [Test]
    public async Task GroupStart_InFixedMode_IsTheFixedDate()
    {
        var given = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        (await PlanAsync(GroupMembershipStartMode.Fixed, given)).GroupStart([Early]).ShouldBe(given);
    }

    [Test]
    public async Task Describe_WithoutPersons_HasNoDates()
    {
        var info = (await PlanAsync(GroupMembershipStartMode.Fixed)).Describe([]);

        info.PersonCount.ShouldBe(0);
        info.EarliestStartDate.ShouldBeNull();
        info.LatestStartDate.ShouldBeNull();
    }

    [Test]
    public async Task Fixed_APersonWhoLeftBeforeTheStart_IsNotCountedAsEnteredEarlier()
    {
        var left = Guid.NewGuid();
        var planner = TestGroupMembershipStart.PlannerWithExitDates(
            _clock, (left, new DateOnly(2020, 1, 1), new DateOnly(2025, 12, 31)), (Early, new DateOnly(2024, 2, 1), null));
        var plan = await planner.PlanAsync([left, Early], null, GroupMembershipStartMode.Fixed, null, CancellationToken.None);

        var info = plan.Describe([left, Early]);

        info.PersonCount.ShouldBe(2);
        info.PersonsEnteredBeforeStartCount.ShouldBe(1);
        info.EarliestEntryDate.ShouldBe("2024-02-01");
    }

    [Test]
    public async Task Fixed_AnExitOnTheStartDay_StillCounts()
    {
        var leaves = Guid.NewGuid();
        var planner = TestGroupMembershipStart.PlannerWithExitDates(_clock, (leaves, new DateOnly(2020, 1, 1), new DateOnly(2026, 10, 20)));
        var plan = await planner.PlanAsync([leaves], null, GroupMembershipStartMode.Fixed, null, CancellationToken.None);

        plan.Describe([leaves]).PersonsEnteredBeforeStartCount.ShouldBe(1);
    }

    [Test]
    public async Task EntryDate_ExistingMembersWithAnOpenStartOrAStartBeforeTheEntryOrNoRecord_AreNotCounted()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.EntryDate);
        var later = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

        var info = plan.Describe(
            [],
            [
                new ExistingMembershipStart(Early, later),
                new ExistingMembershipStart(Late, later),
                new ExistingMembershipStart(NoRecord, later),
                new ExistingMembershipStart(Early, null)
            ]);

        info.PersonCount.ShouldBe(0);
        info.ExistingMembersStartingAfterEntryCount.ShouldBe(0);
    }

    [Test]
    public async Task EntryDate_ExistingMembersCount_UsesEachPersonsEarliestStart()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.EntryDate);
        var later = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var sameAsEntry = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var info = plan.Describe(
            [],
            [new ExistingMembershipStart(Early, later), new ExistingMembershipStart(NoRecord, later)]);
        var matching = plan.Describe([], [new ExistingMembershipStart(Early, sameAsEntry)]);

        info.ExistingMembersStartingAfterEntryCount.ShouldBe(1);
        info.ExistingMembersEarliestEntryDate.ShouldBe("2024-02-01");
        matching.ExistingMembersStartingAfterEntryCount.ShouldBe(0);
    }

    [Test]
    public async Task Fixed_DoesNotReportExistingMembers()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.Fixed);

        plan.Describe([], [new ExistingMembershipStart(Early, Today)]).ExistingMembersStartingAfterEntryCount.ShouldBe(0);
    }

    [Test]
    public async Task EntryDate_ExistingMemberWhoLeftBeforeTheirStart_IsNotReported()
    {
        var left = Guid.NewGuid();
        var planner = TestGroupMembershipStart.PlannerWithExitDates(
            _clock, (left, new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1)));
        var plan = await planner.PlanAsync([left], null, GroupMembershipStartMode.EntryDate, null, CancellationToken.None);

        plan.Describe([], [new ExistingMembershipStart(left, Today)]).ExistingMembersStartingAfterEntryCount.ShouldBe(0);
    }

    [Test]
    public void TheModeIsSerializedAsItsName()
    {
        var info = new Klacks.Api.Application.DTOs.Groups.GroupMembershipStartInfo(
            GroupMembershipStartMode.EntryDate, true, null, null, null, 0, 0, 0, null);

        System.Text.Json.JsonSerializer.Serialize(info).ShouldContain("\"Mode\":\"EntryDate\"");
    }

    [Test]
    public async Task GroupStartsPerMemberSet_NeverPastToday()
    {
        var plan = await PlanAsync(GroupMembershipStartMode.EntryDate);

        plan.GroupStart([Early]).ShouldBe(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        plan.GroupStart([Late]).ShouldBe(Today);
    }
}
