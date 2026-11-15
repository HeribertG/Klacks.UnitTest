// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// On the real plan (no scenario) a harmonizer run (Wizard 2, Wizard 4, stage 3 outside the chain) holds the works of its
/// agents in every group. Anna is in the groups Care and Night and holds a Night work; Bob is only in Care. A swap must
/// never hand the Night work to Bob, because Bob is not a member of the Night shift's group on that day. A member of a
/// group below the shift's group counts as a member; the last day of a membership counts (inclusive). A shift without a
/// group link stays unrestricted, and the current holder of a work is never refused her own work.
/// </summary>
[TestFixture]
public class HarmonizerContextBuilderShiftGroupMembershipTests
{
    private static readonly DateOnly WeekStart = new(2026, 1, 5);
    private static readonly DateOnly WeekEnd = new(2026, 1, 11);
    private static readonly DateOnly Day = WeekStart.AddDays(2);
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private const decimal ShiftHours = 8m;
    private const string NotEligibleReason = "not qualified for shift";

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractProvider = null!;
    private Guid _anna;
    private Guid _bob;
    private Guid _careGroup;
    private Guid _nightGroup;
    private Guid _nightShift;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _contractProvider = Substitute.For<IClientContractDataProvider>();

        _anna = Guid.NewGuid();
        _bob = Guid.NewGuid();
        StubContracts(_anna, _bob);
        _careGroup = SeedGroup("Care", root: null, lft: 1, rgt: 2);
        _nightGroup = SeedGroup("Night", root: null, lft: 1, rgt: 4);
        _nightShift = SeedShift("Night");
        LinkShift(_nightShift, _nightGroup);
        SeedMember(_anna, _careGroup, until: null);
        SeedMember(_anna, _nightGroup, until: null);
        SeedMember(_bob, _careGroup, until: null);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task RealPlan_SwapHandingAnnasNightWorkToBob_IsRefused_BecauseBobIsNoMemberOfTheNightGroup()
    {
        await SeedAnnasNightAsync();

        var rejection = await SwapAnnaWithBobAsync();

        rejection.ShouldNotBeNull("Bob is not a member of the Night shift's group, so he must not receive the Night work");
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(NotEligibleReason);
    }

    [Test]
    public async Task RealPlan_BobsNightMembershipEndingThatDay_StillCounts()
    {
        SeedMember(_bob, _nightGroup, until: Day);
        await SeedAnnasNightAsync();

        var rejection = await SwapAnnaWithBobAsync();

        rejection.ShouldBeNull($"the last day of a membership is a member day (inclusive): {rejection?.Detail}");
    }

    [Test]
    public async Task RealPlan_BobsNightMembershipEndingTheDayBefore_IsRefused()
    {
        SeedMember(_bob, _nightGroup, until: Day.AddDays(-1));
        await SeedAnnasNightAsync();

        var rejection = await SwapAnnaWithBobAsync();

        rejection.ShouldNotBeNull("Bob's Night membership ended the day before the work");
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(NotEligibleReason);
    }

    [Test]
    public async Task RealPlan_MemberOfASubgroupOfTheShiftsGroup_MayReceiveTheWork()
    {
        var nightTeam = SeedGroup("Night team", root: _nightGroup, lft: 2, rgt: 3);
        SeedMember(_bob, nightTeam, until: null);
        await SeedAnnasNightAsync();

        var rejection = await SwapAnnaWithBobAsync();

        rejection.ShouldBeNull($"a member of a group below the shift's group belongs to that group: {rejection?.Detail}");
    }

    [Test]
    public async Task RealPlan_ShiftWithoutGroupLink_StaysUnrestricted()
    {
        var unlinkedShift = SeedShift("Unlinked");
        SeedWork(_anna, unlinkedShift, analyseToken: null);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(analyseToken: null), CancellationToken.None);

        input.IneligibleAssignments!.ShouldNotContain((_bob.ToString(), unlinkedShift, Day));
    }

    [Test]
    public async Task RealPlan_TheHolderIsNeverRefusedHerOwnWork_EvenWithoutMembership()
    {
        var foreignShift = SeedShift("Foreign");
        LinkShift(foreignShift, SeedGroup("Foreign", root: null, lft: 1, rgt: 2));
        SeedWork(_anna, foreignShift, analyseToken: null);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(analyseToken: null), CancellationToken.None);

        input.IneligibleAssignments!.ShouldNotContain((_anna.ToString(), foreignShift, Day));
        input.IneligibleAssignments!.ShouldContain((_bob.ToString(), foreignShift, Day));
    }

    [Test]
    public async Task Scenario_GroupMembershipIsNotTested_TheScenarioLocksForeignRealWorksInstead()
    {
        var token = Guid.NewGuid();
        SeedWork(_anna, _nightShift, token);
        await _context.SaveChangesAsync();

        var input = await BuildSut().BuildContextAsync(Request(token), CancellationToken.None);

        input.IneligibleAssignments!.ShouldNotContain((_bob.ToString(), _nightShift, Day),
            "a scenario run keeps its own semantics: real works outside the scenario are locked cells");
    }

    private async Task SeedAnnasNightAsync()
    {
        SeedWork(_anna, _nightShift, analyseToken: null);
        await _context.SaveChangesAsync();
    }

    private async Task<PlanMutationRejection?> SwapAnnaWithBobAsync()
    {
        var input = await BuildSut().BuildContextAsync(Request(analyseToken: null), CancellationToken.None);
        var bitmap = BitmapBuilder.Build(input);
        var annaRow = RowOf(bitmap, _anna);
        var dayIndex = DayIndexOf(bitmap, Day);
        bitmap.GetCell(annaRow, dayIndex).IsLocked.ShouldBeFalse("the Night work itself stays movable among members");

        return new PlanMutationValidator(new DomainAwareReplaceValidator(input.Availability, ineligibleAssignments: input.IneligibleAssignments))
            .Validate(bitmap, new PlanCellSwap(annaRow, dayIndex, RowOf(bitmap, _bob), dayIndex, "test"));
    }

    private HarmonizerContextRequest Request(Guid? analyseToken)
        => new(WeekStart, WeekEnd, [_anna, _bob], analyseToken, LoadPlanningRules: false);

    private static int RowOf(HarmonyBitmap bitmap, Guid agent)
        => bitmap.Rows.Select((row, index) => (row, index)).Single(r => r.row.Id == agent.ToString()).index;

    private static int DayIndexOf(HarmonyBitmap bitmap, DateOnly date)
        => bitmap.Days.Select((day, index) => (day, index)).Single(d => d.day == date).index;

    private Guid SeedGroup(string name, Guid? root, int lft, int rgt)
    {
        var id = Guid.NewGuid();
        _context.Group.Add(new Group { Id = id, Name = name, Root = root ?? id, Parent = root, Lft = lft, Rgt = rgt, ValidFrom = LongAgo });
        return id;
    }

    private Guid SeedShift(string name)
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = id,
            Name = name,
            Status = ShiftStatus.SplitShift,
            StartShift = NightStart,
            EndShift = NightEnd,
        });
        return id;
    }

    private void LinkShift(Guid shiftId, Guid groupId)
        => _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = groupId });

    private void SeedMember(Guid clientId, Guid groupId, DateOnly? until)
        => _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = LongAgo,
            ValidUntil = until?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });

    private void SeedWork(Guid agent, Guid shiftId, Guid? analyseToken)
        => _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agent,
            ShiftId = shiftId,
            CurrentDate = Day,
            StartTime = NightStart,
            EndTime = NightEnd,
            WorkTime = ShiftHours,
            AnalyseToken = analyseToken,
            LockLevel = WorkLockLevel.None,
        });

    private void StubContracts(params Guid[] agents)
    {
        var data = new EffectiveContractData
        {
            GuaranteedHours = 160m,
            PaymentInterval = (int)PaymentInterval.Monthly,
            MaxConsecutiveDays = 7,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
            PerformsShiftWork = true,
        };
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>())
            .Returns(agents.ToDictionary(agent => agent, _ => data));

        var range = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
        for (var date = WeekStart; date <= WeekEnd; date = date.AddDays(1))
        {
            range[date] = agents.ToDictionary(agent => agent, _ => data);
        }
        _contractProvider.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(range);

        foreach (var agent in agents)
        {
            _context.Client.Add(new Client { Id = agent, Name = "Agent", FirstName = agent.ToString() });
            _context.Membership.Add(new Membership { Id = Guid.NewGuid(), ClientId = agent, ValidFrom = LongAgo });
        }
        _context.SaveChanges();
    }

    private HarmonizerContextBuilder BuildSut()
    {
        var softening = Substitute.For<IWorkSofteningRepository>();
        softening.LoadAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<WorkSoftening>>([]));

        var eligibility = Substitute.For<IEligibilityMatrixBuilder>();
        eligibility.BuildAsync(default!, default!, default!, default).ReturnsForAnyArgs(new EligibilityMatrix
        {
            Ineligible = new HashSet<(string, Guid, DateOnly)>(),
            Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<QualificationGap>>(),
            QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
            ShiftNames = new Dictionary<Guid, string>(),
        });

        var availability = Substitute.For<IAvailabilityIneligibilityService>();
        availability.GetAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlySet<(string, Guid, DateOnly)>>(new HashSet<(string, Guid, DateOnly)>()));

        var windows = Substitute.For<IWizardRestrictedWindowBuilder>();
        windows.BuildAsync(default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<CoreRestrictedTimeWindow>>([]));

        var keywords = Substitute.For<IScheduleCommandKeywordProvider>();
        keywords.GetAsync(default).ReturnsForAnyArgs(ScheduleCommandKeywordTestFactory.Default);

        return new HarmonizerContextBuilder(
            _context, _contractProvider, softening, eligibility, availability, windows, keywords, Substitute.For<IPlanningRuleSetLoader>());
    }
}
