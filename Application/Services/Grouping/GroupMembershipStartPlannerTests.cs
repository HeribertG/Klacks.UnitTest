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
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;

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

    private static readonly Guid ItemOfEarly = Guid.NewGuid();
    private static readonly Guid TargetGroup = Guid.NewGuid();
    private static readonly DateTime LaterStart = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EarlyEntry = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private IGroupItemRepository _items = null!;
    private IGroupRepository _groups = null!;

    private async Task<(IGroupMembershipStartPlanner Planner, GroupMembershipStartPlan Plan)> EntryPlanAsync(
        params (Guid ClientId, DateOnly EntryDate)[] entries)
    {
        _items = Substitute.For<IGroupItemRepository>();
        _groups = Substitute.For<IGroupRepository>();
        var planner = TestGroupMembershipStart.Planner(_clock, _items, _groups, entries);
        var plan = await planner.PlanAsync(
            entries.Select(e => e.ClientId).ToList(), null, GroupMembershipStartMode.EntryDate, null, CancellationToken.None);
        return (planner, plan);
    }

    private static ExistingMembershipStart Held(Guid clientId, DateTime? start, Guid? itemId = null, Guid? groupId = null) =>
        new(clientId, start, itemId ?? ItemOfEarly, groupId ?? TargetGroup);

    [Test]
    public async Task Describe_CountsPullableExistingMembershipsSeparatelyFromUnchangeable()
    {
        var (_, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)), (Late, new DateOnly(2025, 5, 1)));

        var info = plan.Describe(
            [],
            [
                Held(Early, LaterStart),
                new ExistingMembershipStart(Late, LaterStart)
            ]);

        info.ExistingMembersPulledForwardCount.ShouldBe(1);
        info.PulledForwardEarliestEntryDate.ShouldBe("2024-02-01");
        info.ExistingMembersStartingAfterEntryCount.ShouldBe(1);
        info.ExistingMembersEarliestEntryDate.ShouldBe("2025-05-01");
    }

    [Test]
    public async Task Stage_PullsAnExistingMembershipForwardToTheEntryDate()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);

        batch.Count.ShouldBe(1);
        await _items.Received(1).PullForwardValidFromAsync(
            Arg.Is<IReadOnlyDictionary<Guid, DateTime>>(map => map.Count == 1 && map[ItemOfEarly] == EarlyEntry),
            Arg.Any<CancellationToken>());
        await _groups.Received(1).LowerValidFromAsync(
            Arg.Is<IReadOnlyDictionary<Guid, DateTime>>(map => map.Count == 1 && map[TargetGroup] == EarlyEntry),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Stage_NeverMovesAStartLater_NorOnTheEntryDay()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));

        var earlier = await planner.StagePullForwardAsync(plan, [Held(Early, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc))], CancellationToken.None);
        var same = await planner.StagePullForwardAsync(plan, [Held(Early, EarlyEntry)], CancellationToken.None);
        var open = await planner.StagePullForwardAsync(plan, [Held(Early, null)], CancellationToken.None);

        earlier.Count.ShouldBe(0);
        same.Count.ShouldBe(0);
        open.Count.ShouldBe(0);
        await _items.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
        await _groups.DidNotReceiveWithAnyArgs().LowerValidFromAsync(default!, default);
    }

    [Test]
    public async Task Stage_LeavesOutAPersonWithoutAMembershipRecord()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));

        var batch = await planner.StagePullForwardAsync(plan, [Held(NoRecord, LaterStart)], CancellationToken.None);

        batch.Count.ShouldBe(0);
        await _items.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
    }

    [Test]
    public async Task Stage_LeavesOutAPersonWhoLeftBeforeTheExistingStart()
    {
        var left = Guid.NewGuid();
        _items = Substitute.For<IGroupItemRepository>();
        _groups = Substitute.For<IGroupRepository>();
        var windows = new Dictionary<Guid, Klacks.Api.Domain.Models.Associations.MembershipWindow>
        {
            [left] = new(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1))
        };
        var reader = Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IMembershipWindowReader>();
        reader.GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(windows);
        _items.GetEndedMembershipsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid ClientId, Guid GroupId, DateTime ValidUntil)>());
        var planner = new GroupMembershipStartPlanner(reader, _clock, _items, _groups, Substitute.For<Klacks.Api.Domain.Interfaces.IUnitOfWork>());
        var plan = await planner.PlanAsync([left], null, GroupMembershipStartMode.EntryDate, null, CancellationToken.None);

        var batch = await planner.StagePullForwardAsync(plan, [Held(left, LaterStart)], CancellationToken.None);

        batch.Count.ShouldBe(0);
        plan.Describe([], [Held(left, LaterStart)]).ExistingMembersPulledForwardCount.ShouldBe(0);
    }

    [Test]
    public async Task Stage_InFixedMode_NeverTouchesExistingMemberships()
    {
        _items = Substitute.For<IGroupItemRepository>();
        _groups = Substitute.For<IGroupRepository>();
        var planner = TestGroupMembershipStart.Planner(_clock, _items, _groups, (Early, new DateOnly(2024, 2, 1)));
        var plan = await planner.PlanAsync([Early], null, GroupMembershipStartMode.Fixed, null, CancellationToken.None);

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);

        batch.Count.ShouldBe(0);
        plan.Describe([], [Held(Early, LaterStart)]).ExistingMembersPulledForwardCount.ShouldBe(0);
        await _items.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
        await _groups.DidNotReceiveWithAnyArgs().LowerValidFromAsync(default!, default);
    }

    [Test]
    public async Task Stage_AGroupGetsItsMembersEarliestEntry_AndAnItemWithoutAnIdIsSkipped()
    {
        var otherGroup = Guid.NewGuid();
        var (planner, plan) = await EntryPlanAsync(
            (Early, new DateOnly(2024, 2, 1)), (Late, new DateOnly(2022, 3, 1)), (NoRecord, new DateOnly(2021, 1, 1)));
        var secondItem = Guid.NewGuid();
        var thirdItem = Guid.NewGuid();

        var batch = await planner.StagePullForwardAsync(
            plan,
            [
                Held(Early, LaterStart),
                Held(Late, LaterStart, secondItem, TargetGroup),
                Held(NoRecord, LaterStart, thirdItem, otherGroup),
                new ExistingMembershipStart(Early, LaterStart)
            ],
            CancellationToken.None);

        batch.Count.ShouldBe(3);
        batch.GroupStarts[TargetGroup].ShouldBe(new DateTime(2022, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        batch.GroupStarts[otherGroup].ShouldBe(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task Stage_FiveThousandPersons_AreWrittenInOneBatchedCallPerRepository()
    {
        var clients = Enumerable.Range(0, 5000).Select(_ => Guid.NewGuid()).ToList();
        var (planner, plan) = await EntryPlanAsync(clients.Select(c => (c, new DateOnly(2024, 2, 1))).ToArray());

        var batch = await planner.StagePullForwardAsync(
            plan, clients.Select(c => Held(c, LaterStart, Guid.NewGuid(), TargetGroup)).ToList(), CancellationToken.None);

        batch.Count.ShouldBe(5000);
        await _items.Received(1).PullForwardValidFromAsync(Arg.Any<IReadOnlyDictionary<Guid, DateTime>>(), Arg.Any<CancellationToken>());
        await _groups.Received(1).LowerValidFromAsync(Arg.Any<IReadOnlyDictionary<Guid, DateTime>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Verify_PassesWhenEveryItemStartsOnOrBeforeItsNewStart()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));
        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);
        _items.GetValidFromByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DateTime?> { [ItemOfEarly] = EarlyEntry });

        await planner.VerifyPullForwardAsync(batch, CancellationToken.None);
    }

    [Test]
    public async Task Verify_ThrowsWhenAnItemWasNotMoved_SoTheTransactionRollsBack()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));
        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);
        _items.GetValidFromByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DateTime?> { [ItemOfEarly] = LaterStart });

        await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.SkillVerificationException>(
            () => planner.VerifyPullForwardAsync(batch, CancellationToken.None));
    }

    [Test]
    public async Task Verify_OfAnEmptyBatch_ReadsNothing()
    {
        var (planner, _) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));

        await planner.VerifyPullForwardAsync(PullForwardBatch.Empty, CancellationToken.None);

        await _items.DidNotReceiveWithAnyArgs().GetValidFromByIdsAsync(default!, default);
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private async Task<(IGroupMembershipStartPlanner Planner, GroupMembershipStartPlan Plan)> EntryPlanWithEndedAsync(
        IEnumerable<(Guid ClientId, Guid GroupId, DateTime ValidUntil)> ended,
        params (Guid ClientId, DateOnly EntryDate)[] entries)
    {
        _items = Substitute.For<IGroupItemRepository>();
        _groups = Substitute.For<IGroupRepository>();
        var planner = TestGroupMembershipStart.Planner(_clock, _items, _groups, entries);
        _items.GetEndedMembershipsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(ended.ToList());
        var plan = await planner.PlanAsync(
            entries.Select(e => e.ClientId).ToList(), null, GroupMembershipStartMode.EntryDate, null, CancellationToken.None);
        return (planner, plan);
    }

    [Test]
    public async Task Stage_NeverPullsAnEndedMembershipForward_AndCountsIt()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));
        var ended = new ExistingMembershipStart(Early, LaterStart, ItemOfEarly, TargetGroup, ValidUntil: Utc(2026, 12, 31));

        var batch = await planner.StagePullForwardAsync(plan, [ended], CancellationToken.None);

        batch.Count.ShouldBe(0);
        plan.Describe([], [ended]).ExistingMembersNotPulledForwardCount.ShouldBe(1);
        plan.Describe([], [ended]).ExistingMembersPulledForwardCount.ShouldBe(0);
        await _items.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
    }

    [Test]
    public async Task Stage_AfterAGroupChange_NeverGoesBeforeTheDayAfterTheEarlierGroupEnded()
    {
        var otherGroup = Guid.NewGuid();
        var (planner, plan) = await EntryPlanWithEndedAsync(
            [(Early, otherGroup, Utc(2025, 6, 30))], (Early, new DateOnly(2024, 2, 1)));

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);

        batch.ItemStarts[ItemOfEarly].ShouldBe(Utc(2025, 7, 1));
        plan.Describe([], [Held(Early, LaterStart)]).PulledForwardEarliestEntryDate.ShouldBe("2025-07-01");
    }

    [Test]
    public async Task Stage_AnEndedMembershipOfTheSameGroup_DoesNotHoldTheStartBack()
    {
        var (planner, plan) = await EntryPlanWithEndedAsync(
            [(Early, TargetGroup, Utc(2025, 6, 30))], (Early, new DateOnly(2024, 2, 1)));

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);

        batch.ItemStarts[ItemOfEarly].ShouldBe(Utc(2024, 2, 1));
    }

    [Test]
    public async Task Stage_SkipsAndCounts_WhenTheGroupChangeLeavesNothingToPullForward()
    {
        var otherGroup = Guid.NewGuid();
        var (planner, plan) = await EntryPlanWithEndedAsync(
            [(Early, otherGroup, Utc(2026, 10, 19))], (Early, new DateOnly(2024, 2, 1)));

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);

        batch.Count.ShouldBe(0);
        plan.Describe([], [Held(Early, LaterStart)]).ExistingMembersNotPulledForwardCount.ShouldBe(1);
        await _items.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
    }

    [Test]
    public async Task PlanAsync_ReadsTheEndedMembershipsOnce_AndOnlyInEntryDateMode()
    {
        var (_, _) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)), (Late, new DateOnly(2022, 3, 1)));
        await _items.Received(1).GetEndedMembershipsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());

        var planner = TestGroupMembershipStart.Planner(_clock, _items, _groups, (Early, new DateOnly(2024, 2, 1)));
        _items.ClearReceivedCalls();
        await planner.PlanAsync([Early], null, GroupMembershipStartMode.Fixed, null, CancellationToken.None);

        await _items.DidNotReceiveWithAnyArgs().GetEndedMembershipsAsync(default!, default);
    }

    [TestCase(1, 1, 1)]
    [TestCase(1900, 1, 1)]
    public async Task AnImplausibleEntryDate_IsNeverUsed_NotToPullForward_NorForANewMembership(int year, int month, int day)
    {
        var placeholder = new DateOnly(year, month, day);
        var (planner, plan) = await EntryPlanAsync((Early, placeholder));

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);
        var info = plan.Describe([Early], [Held(Early, LaterStart)]);

        batch.Count.ShouldBe(0);
        plan.StartFor(Early).ShouldBe(Today);
        info.PersonsWithImplausibleEntryDateCount.ShouldBe(1);
        info.PersonsEnteredBeforeStartCount.ShouldBe(0);
        info.ExistingMembersPulledForwardCount.ShouldBe(0);
        await _items.DidNotReceiveWithAnyArgs().PullForwardValidFromAsync(default!, default);
    }

    [Test]
    public async Task APlausibleEntryDate_IsNotCountedAsImplausible()
    {
        var (_, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));

        plan.Describe([Early]).PersonsWithImplausibleEntryDateCount.ShouldBe(0);
    }

    [Test]
    public async Task Stage_ALoweredGroupStart_IsNeverAfterToday()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2030, 1, 1)));
        var farFuture = Utc(2099, 1, 1);

        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, farFuture)], CancellationToken.None);

        batch.ItemStarts[ItemOfEarly].ShouldBe(Utc(2030, 1, 1));
        batch.GroupStarts[TargetGroup].ShouldBe(Today);
    }

    [Test]
    public async Task Stage_ReportsTheNumberOfItemsTheRepositoryActuallyChanged()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)), (Late, new DateOnly(2024, 2, 1)));
        _items.PullForwardValidFromAsync(Arg.Any<IReadOnlyDictionary<Guid, DateTime>>(), Arg.Any<CancellationToken>()).Returns(1);

        var batch = await planner.StagePullForwardAsync(
            plan, [Held(Early, LaterStart), Held(Late, LaterStart, Guid.NewGuid(), TargetGroup)], CancellationToken.None);

        batch.Count.ShouldBe(2);
        batch.ChangedCount.ShouldBe(1);
        batch.ReportOn(plan.Describe([], [])).ExistingMembersPulledForwardCount.ShouldBe(1);
    }

    [Test]
    public async Task Verify_OnAMismatch_DiscardsTheTrackedChanges_AndExplainsWhatWentWrong()
    {
        _items = Substitute.For<IGroupItemRepository>();
        _groups = Substitute.For<IGroupRepository>();
        var unitOfWork = Substitute.For<Klacks.Api.Domain.Interfaces.IUnitOfWork>();
        var planner = TestGroupMembershipStart.Planner(_clock, _items, _groups, unitOfWork, (Early, new DateOnly(2024, 2, 1)));
        var plan = await planner.PlanAsync([Early], null, GroupMembershipStartMode.EntryDate, null, CancellationToken.None);
        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);
        _items.GetValidFromByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DateTime?>());

        var ex = await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.SkillVerificationException>(
            () => planner.VerifyPullForwardAsync(batch, CancellationToken.None));

        unitOfWork.Received(1).DiscardTrackedChanges();
        ex.Message.ShouldContain("could not be found");
        ex.Message.ShouldContain("nothing was saved");
    }

    [Test]
    public async Task Verify_OfAnItemThatStillStartsLater_NamesThatReason()
    {
        var (planner, plan) = await EntryPlanAsync((Early, new DateOnly(2024, 2, 1)));
        var batch = await planner.StagePullForwardAsync(plan, [Held(Early, LaterStart)], CancellationToken.None);
        _items.GetValidFromByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DateTime?> { [ItemOfEarly] = LaterStart });

        var ex = await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.SkillVerificationException>(
            () => planner.VerifyPullForwardAsync(batch, CancellationToken.None));

        ex.Message.ShouldContain("still start later");
    }
}
