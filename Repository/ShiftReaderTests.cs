// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

/// <summary>
/// The materialising shift reads of ShiftReader and
/// ContainerTemplateRepository.GetContainerIdsWithTemplateAsync over EF InMemory: every RealPlanShiftScan restriction,
/// the base order versus the from-date order with its cap, the order-lineage lookups, the group read of
/// find_split_shift_candidates and the visible by-ids read. Orders are asserted only where InMemory and PostgreSQL agree (InMemory sorts a null
/// original id first and compares Guids like .NET); SQL translation (ILike, NULLS LAST, uuid order) is pinned
/// by the integration test ShiftReadShapesTests. The full-day scan (UncutFullDayShiftDetector) deliberately keeps
/// time-range shifts with equal bounds (owner decision 2026-09-27), while the order review's FullDayOrderRule drops them.
/// </summary>
[TestFixture]
public class ShiftReaderTests
{
    private static readonly DateOnly Day = new(2026, 6, 10);
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    private InMemoryShiftStore _store = null!;

    [SetUp]
    public void SetUp() => _store = new InMemoryShiftStore();

    [TearDown]
    public void TearDown() => _store.Dispose();

    [Test]
    public async Task Scan_KeepsOnlyTheStatus_AndDropsScenarioRowsAndSoftDeletedRows()
    {
        var kept = Make("kept", ShiftStatus.OriginalOrder, Day);
        var otherStatus = Make("otherStatus", ShiftStatus.SealedOrder, Day);
        var token = Make("token", ShiftStatus.OriginalOrder, Day);
        token.AnalyseToken = Guid.NewGuid();
        var sourceLinked = Make("sourceLinked", ShiftStatus.OriginalOrder, Day);
        sourceLinked.ScenarioSourceShiftId = kept.Id;
        var deleted = Make("deleted", ShiftStatus.OriginalOrder, Day);
        deleted.IsDeleted = true;
        _store.Seed(kept, otherStatus, token, sourceLinked, deleted);

        var rows = await _store.Reader.ScanRealPlanAsync(new RealPlanShiftScan(ShiftStatus.OriginalOrder));

        rows.Select(s => s.Name).ShouldBe(["kept"]);
        _store.Context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task Scan_AppliesTypeDateFullDayAndIdRestrictions()
    {
        var match = Make("match", ShiftStatus.OriginalShift, Day, untilDate: Day.AddDays(5));
        var container = Make("container", ShiftStatus.OriginalShift, Day);
        container.ShiftType = ShiftType.IsContainer;
        var startsBefore = Make("startsBefore", ShiftStatus.OriginalShift, Day.AddDays(-1));
        var openEnded = Make("openEnded", ShiftStatus.OriginalShift, Day.AddDays(1));
        var ended = Make("ended", ShiftStatus.OriginalShift, Day, untilDate: Day.AddDays(1));
        var partDay = Make("partDay", ShiftStatus.OriginalShift, Day);
        partDay.EndShift = partDay.StartShift.AddHours(8);
        _store.Seed(match, container, startsBefore, openEnded, ended, partDay);

        var typed = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { ShiftType = ShiftType.IsTask });
        var starting = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { StartsOnOrAfter = Day });
        var running = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { NotEndedBefore = Day.AddDays(2) });
        var fullDay = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { FullDayOnly = true });
        var only = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { OnlyIds = [match.Id, ended.Id] });
        var none = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { OnlyIds = [] });
        var excluded = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { ExcludedIds = [match.Id, container.Id] });
        var noExclusion = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { ExcludedIds = [] });

        typed.ShouldNotContain("container");
        typed.Count.ShouldBe(5);
        starting.ShouldNotContain("startsBefore");
        starting.Count.ShouldBe(5);
        running.ShouldBe(["startsBefore", "match", "container", "partDay", "openEnded"], ignoreOrder: true);
        fullDay.ShouldNotContain("partDay");
        fullDay.Count.ShouldBe(5);
        only.ShouldBe(["match", "ended"], ignoreOrder: true);
        none.ShouldBeEmpty();
        excluded.ShouldBe(["startsBefore", "openEnded", "ended", "partDay"], ignoreOrder: true);
        noExclusion.Count.ShouldBe(6);
    }

    [Test]
    public async Task FullDayScan_KeepsATimeRangeShiftWithEqualBounds_UnlikeTheOrderReviewRule()
    {
        var timeRange = Make("timeRange", ShiftStatus.OriginalShift, Day, start: TimeOnly.MinValue);
        timeRange.IsTimeRange = true;
        _store.Seed(timeRange);

        var fullDay = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalShift) { FullDayOnly = true });

        fullDay.ShouldBe(["timeRange"]);
        FullDayOrderRule.IsFullDay(timeRange).ShouldBeFalse();
    }

    [Test]
    public async Task AvailableTaskCandidates_DropScenarioRows_AndKeepOnlyTasksValidDuringTheContainersPeriod()
    {
        var inside = Candidate("inside", Day);
        var openBefore = Candidate("openBefore", Day.AddDays(-30));
        var nextMonth = Candidate("nextMonth", Day.AddDays(31));
        var endedBefore = Candidate("endedBefore", Day.AddDays(-30), untilDate: Day.AddDays(-1));
        var endsOnFirstDay = Candidate("endsOnFirstDay", Day.AddDays(-30), untilDate: Day);
        var startsOnLastDay = Candidate("startsOnLastDay", Day.AddDays(30));
        var scenario = Candidate("scenario", Day);
        scenario.AnalyseToken = Guid.NewGuid();
        _store.Seed(inside, openBefore, nextMonth, endedBefore, endsOnFirstDay, startsOnLastDay, scenario);

        var period = await CandidateNamesAsync(Slot() with { ValidFrom = Day, ValidUntil = Day.AddDays(30) });
        var openEnded = await CandidateNamesAsync(Slot() with { ValidFrom = Day });
        var unbounded = await CandidateNamesAsync(Slot());

        period.ShouldBe(["inside", "openBefore", "endsOnFirstDay", "startsOnLastDay"], ignoreOrder: true);
        openEnded.ShouldBe(["inside", "openBefore", "nextMonth", "endsOnFirstDay", "startsOnLastDay"], ignoreOrder: true);
        unbounded.ShouldBe(
            ["inside", "openBefore", "nextMonth", "endedBefore", "endsOnFirstDay", "startsOnLastDay"], ignoreOrder: true);
    }

    [Test]
    public async Task ValidityPeriod_IsProjectedUntracked_AndNullForAnUnknownOrDeletedShift()
    {
        var open = Make("open", ShiftStatus.OriginalShift, Day);
        var bounded = Make("bounded", ShiftStatus.OriginalShift, Day, untilDate: Day.AddDays(9));
        var deleted = Make("deleted", ShiftStatus.OriginalShift, Day);
        deleted.IsDeleted = true;
        _store.Seed(open, bounded, deleted);

        (await _store.Reader.GetValidityPeriodAsync(open.Id)).ShouldBe(new ShiftValidityPeriod(Day, null));
        (await _store.Reader.GetValidityPeriodAsync(bounded.Id)).ShouldBe(new ShiftValidityPeriod(Day, Day.AddDays(9)));
        (await _store.Reader.GetValidityPeriodAsync(deleted.Id)).ShouldBeNull();
        (await _store.Reader.GetValidityPeriodAsync(Guid.NewGuid())).ShouldBeNull();
        _store.Context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task Scan_DefaultsToRepositoryOrder_AndTheFromDateOrderTakesTheCapAfterSorting()
    {
        var late = Make("late", ShiftStatus.OriginalOrder, Day.AddDays(3), start: new TimeOnly(6, 0));
        var earlyNoon = Make("earlyNoon", ShiftStatus.OriginalOrder, Day, start: new TimeOnly(12, 0));
        var earlyMorning = Make("earlyMorning", ShiftStatus.OriginalOrder, Day, start: new TimeOnly(6, 0));
        var middle = Make("middle", ShiftStatus.OriginalOrder, Day.AddDays(1), start: new TimeOnly(6, 0));
        _store.Seed(late, earlyNoon, earlyMorning, middle);

        var repositoryOrder = await NamesAsync(new RealPlanShiftScan(ShiftStatus.OriginalOrder));
        var soonestTwo = await _store.Reader.ScanRealPlanAsync(
            new RealPlanShiftScan(ShiftStatus.OriginalOrder) { OrderByFromDateThenId = true, MaxCount = 2 });
        var keys = await _store.Reader.ScanRealPlanKeysAsync(
            new RealPlanShiftScan(ShiftStatus.OriginalOrder) { OrderByFromDateThenId = true, MaxCount = 3 });

        repositoryOrder.ShouldBe(["earlyMorning", "earlyNoon", "middle", "late"]);
        soonestTwo.Select(s => s.FromDate).ShouldBe([Day, Day]);
        soonestTwo.Select(s => s.Id).ShouldBe([earlyNoon.Id, earlyMorning.Id], ignoreOrder: true);
        keys.Take(2).ShouldBe(
            [new ShiftDateKey(earlyNoon.Id, earlyNoon.FromDate), new ShiftDateKey(earlyMorning.Id, earlyMorning.FromDate)],
            ignoreOrder: true);
        keys[2].ShouldBe(new ShiftDateKey(middle.Id, middle.FromDate));
    }

    [Test]
    public async Task OrderLineage_PicksTheFirstInRepositoryOrder_AndCountsPerStatus()
    {
        var planLate = Make("planLate", ShiftStatus.OriginalShift, Day.AddDays(5), originalId: OrderId);
        var planEarly = Make("planEarly", ShiftStatus.OriginalShift, Day.AddDays(1), originalId: OrderId);
        var partA = Make("partA", ShiftStatus.SplitShift, Day, originalId: OrderId);
        var partB = Make("partB", ShiftStatus.SplitShift, Day, originalId: OrderId);
        var foreignPart = Make("foreignPart", ShiftStatus.SplitShift, Day, originalId: Guid.NewGuid());
        _store.Seed(planLate, planEarly, partA, partB, foreignPart);

        var first = await _store.Reader.FindFirstByOriginalIdAsync(OrderId, ShiftStatus.OriginalShift);
        var parts = await _store.Reader.CountByOriginalIdAsync(OrderId, ShiftStatus.SplitShift);
        var hasParts = await _store.Reader.AnyByOriginalIdAsync(OrderId, ShiftStatus.SplitShift);
        var hasSealedChild = await _store.Reader.AnyByOriginalIdAsync(OrderId, ShiftStatus.SealedOrder);
        var missing = await _store.Reader.FindFirstByOriginalIdAsync(Guid.NewGuid(), ShiftStatus.OriginalShift);

        first!.Name.ShouldBe("planEarly");
        _store.Context.Entry(first).State.ShouldBe(EntityState.Detached);
        parts.ShouldBe(2);
        hasParts.ShouldBeTrue();
        hasSealedChild.ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Test]
    public async Task OrderFamilyInGroup_KeepsOrderStatusesWithALiveLink_InRepositoryOrder()
    {
        var sealedOrder = Make("sealed", ShiftStatus.SealedOrder, Day, groupId: GroupId);
        var planLater = Make("planLater", ShiftStatus.OriginalShift, Day.AddDays(2), originalId: sealedOrder.Id, groupId: GroupId);
        var part = Make("part", ShiftStatus.SplitShift, Day, originalId: sealedOrder.Id, groupId: GroupId);
        var draft = Make("draft", ShiftStatus.OriginalOrder, Day, groupId: GroupId);
        var deletedLink = Make("deletedLink", ShiftStatus.SealedOrder, Day, groupId: GroupId);
        deletedLink.GroupItems.Single().IsDeleted = true;
        var otherGroup = Make("otherGroup", ShiftStatus.SealedOrder, Day, groupId: Guid.NewGuid());
        _store.Seed(sealedOrder, planLater, part, draft, deletedLink, otherGroup);

        var rows = await _store.Reader.GetOrderFamilyShiftsInGroupAsync(GroupId, nameFragment: "  ");

        rows.Select(s => s.Name).ShouldBe(["sealed", "part", "planLater"], ignoreOrder: true);
        rows.Where(s => s.OriginalId == sealedOrder.Id).Select(s => s.Name).ShouldBe(["part", "planLater"]);
    }

    [Test]
    public async Task VisibleRealPlanWithClientByIds_DropsScenarioRowsAndHiddenGroups_AndLoadsTheClientsAddresses()
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Customer",
            Addresses = [new Address { Id = Guid.NewGuid(), Street = "Hauptstrasse 1", City = "Biel", ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }],
        };
        var groupLess = Make("groupLess", ShiftStatus.OriginalShift, Day);
        groupLess.ClientId = client.Id;
        groupLess.Client = client;
        var hidden = Make("hidden", ShiftStatus.OriginalShift, Day, groupId: GroupId);
        var token = Make("token", ShiftStatus.OriginalShift, Day);
        token.AnalyseToken = Guid.NewGuid();
        _store.Seed(groupLess, hidden, token);
        var ids = new[] { groupLess.Id, hidden.Id, token.Id };

        var unrestricted = await _store.Reader.GetVisibleRealPlanWithClientByIdsAsync(ids, visibleGroupIds: null);
        var restricted = await _store.Reader.GetVisibleRealPlanWithClientByIdsAsync(ids, visibleGroupIds: []);

        unrestricted.Select(s => s.Name).ShouldBe(["groupLess", "hidden"], ignoreOrder: true);
        restricted.Select(s => s.Name).ShouldBe(["groupLess"]);
        restricted.Single().Client!.Addresses.Count.ShouldBe(1);
    }

    [Test]
    public async Task ContainerIdsWithTemplate_AreDistinct_AndIgnoreDeletedTemplates()
    {
        var withTwo = Guid.NewGuid();
        var withDeleted = Guid.NewGuid();
        _store.SeedTemplates(Template(withTwo, weekday: 1), Template(withTwo, weekday: 2), Template(withDeleted, weekday: 1, isDeleted: true));

        var ids = await _store.ContainerTemplates.GetContainerIdsWithTemplateAsync();

        ids.ShouldBe([withTwo]);
    }

    private async Task<List<string>> NamesAsync(RealPlanShiftScan scan) =>
        (await _store.Reader.ScanRealPlanAsync(scan)).Select(s => s.Name).ToList();

    private static ContainerTemplate Template(Guid containerId, int weekday, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = containerId,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(16, 0),
        Weekday = weekday,
        IsDeleted = isDeleted,
    };

    private async Task<List<string>> CandidateNamesAsync(AvailableTaskCandidateCriteria criteria) =>
        (await _store.Reader.GetAvailableTaskCandidatesAsync(criteria)).Select(s => s.Name).ToList();

    private static AvailableTaskCandidateCriteria Slot() => new(1, new TimeOnly(7, 0), new TimeOnly(18, 0));

    private static Shift Candidate(string name, DateOnly fromDate, DateOnly? untilDate = null)
    {
        var shift = Make(name, ShiftStatus.OriginalShift, fromDate, untilDate, new TimeOnly(8, 0));
        shift.EndShift = new TimeOnly(17, 0);
        shift.IsTimeRange = true;
        shift.IsMonday = true;
        return shift;
    }

    private static Shift Make(
        string name,
        ShiftStatus status,
        DateOnly fromDate,
        DateOnly? untilDate = null,
        TimeOnly? start = null,
        Guid? originalId = null,
        Guid? groupId = null)
    {
        var id = Guid.NewGuid();
        var startShift = start ?? new TimeOnly(7, 0);
        return new Shift
        {
            Id = id,
            Name = name,
            Abbreviation = name,
            Status = status,
            ShiftType = ShiftType.IsTask,
            OriginalId = originalId,
            FromDate = fromDate,
            UntilDate = untilDate,
            StartShift = startShift,
            EndShift = startShift,
            GroupItems = groupId is { } g ? [new GroupItem { Id = Guid.NewGuid(), GroupId = g, ShiftId = id }] : [],
        };
    }
}
