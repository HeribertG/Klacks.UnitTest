// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for UnstaffedShiftPeriodDetector (Paket D): one collective event per staffed root group over
/// [today, max(end of the running pay period, today + 7)], the schedule filter it scans with, holiday-calendar
/// clusters with their own holidays and single ownership of a shift, seal days and company holidays removed
/// root-wide, Individual roots skipped, the company-day boundary in a far-east zone, the memoised scan and the
/// fingerprint set that equals the emitted events.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.CalendarSelections;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UnstaffedShiftPeriodDetectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private IGroupRepository _groupRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IShiftScheduleRepository _scheduleRepository = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private IClientHolidayCalendarResolver _holidayResolver = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IGroupAbsenceReadRepository _absenceRepository = null!;
    private FixedCompanyClock _clock = null!;
    private List<Group> _groups = null!;
    private List<ShiftScheduleFilter> _filters = null!;
    private Func<ShiftScheduleFilter, List<ShiftDayAssignment>> _rows = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _scheduleRepository = Substitute.For<IShiftScheduleRepository>();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _holidayResolver = Substitute.For<IClientHolidayCalendarResolver>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _absenceRepository = Substitute.For<IGroupAbsenceReadRepository>();
        _clock = new FixedCompanyClock(NowUtc);
        _groups = new List<Group>();
        _filters = new List<ShiftScheduleFilter>();
        _rows = _ => new List<ShiftDayAssignment>();

        _groupRepository.List().Returns(_ => _groups);
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<Guid>)_groups.Select(group => group.Id).ToList());
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => MondayOf(call.Arg<DateOnly>()));
        _scheduleRepository.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<ShiftScheduleFilter>();
                _filters.Add(filter);
                var rows = _rows(filter);
                return (rows, rows.Select(row => row.ShiftId).Distinct().Count());
            });
        _holidayResolver.GetCalculatorAsync(Arg.Any<Guid?>(), Arg.Any<int>())
            .Returns(Task.FromResult<IHolidaysListCalculator?>(null));
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _absenceRepository.GetMembershipWindowsAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GroupMembershipWindow>)Array.Empty<GroupMembershipWindow>());
        _absenceRepository.GetFullDayAbsencesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ClientFullDayAbsence>)Array.Empty<ClientFullDayAbsence>());
    }

    private UnstaffedShiftPeriodDetector CreateSut() => new(
        _groupRepository,
        _weekConfiguration,
        _scheduleRepository,
        _groupScopeReader,
        _holidayResolver,
        _sealedDayRepository,
        _absenceRepository,
        _clock,
        NullLogger<UnstaffedShiftPeriodDetector>.Instance);

    private static DateOnly MondayOf(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private Group AddRoot(string name, PaymentInterval interval = PaymentInterval.Monthly, Guid? calendarSelectionId = null, int lft = 1, int rgt = 10)
    {
        var id = Guid.NewGuid();
        var root = new Group
        {
            Id = id,
            Name = name,
            Root = id,
            Parent = null,
            Lft = lft,
            Rgt = rgt,
            PaymentInterval = interval,
            CalendarSelectionId = calendarSelectionId,
            ValidFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
        };
        _groups.Add(root);
        return root;
    }

    private Group AddChild(Group parent, string name, int lft, int rgt, Guid? calendarSelectionId = null)
    {
        var child = new Group
        {
            Id = Guid.NewGuid(),
            Name = name,
            Root = parent.Root ?? parent.Id,
            Parent = parent.Id,
            Lft = lft,
            Rgt = rgt,
            PaymentInterval = parent.PaymentInterval,
            CalendarSelectionId = calendarSelectionId,
            ValidFrom = parent.ValidFrom
        };
        _groups.Add(child);
        return child;
    }

    private static ShiftDayAssignment Row(
        Guid shiftId,
        DateOnly date,
        int engaged = 0,
        int quantity = 1,
        int sumEmployees = 1,
        bool isSporadic = false,
        bool isInTemplateContainer = false) => new()
    {
        ShiftId = shiftId,
        Date = date,
        Engaged = engaged,
        Quantity = quantity,
        SumEmployees = sumEmployees,
        IsSporadic = isSporadic,
        IsInTemplateContainer = isInTemplateContainer,
        ShiftName = "Early",
        Abbreviation = "E"
    };

    private void ServeRowsFor(Guid selectedGroup, params ShiftDayAssignment[] rows)
    {
        var previous = _rows;
        _rows = filter => filter.SelectedGroup == selectedGroup ? rows.ToList() : previous(filter);
    }

    private void StubHolidays(Guid? calendarSelectionId, params DateOnly[] holidays)
    {
        var calculator = Substitute.For<IHolidaysListCalculator>();
        calculator.IsHoliday(Arg.Any<DateOnly>())
            .Returns(call => holidays.Contains(call.Arg<DateOnly>()) ? HolidayStatus.OfficialHoliday : HolidayStatus.NotAHoliday);
        _holidayResolver.GetCalculatorAsync(calendarSelectionId, Arg.Any<int>())
            .Returns(Task.FromResult<IHolidaysListCalculator?>(calculator));
    }

    private static UnstaffedShiftSummaryTriggerEvent Single(IReadOnlyList<IAgentTriggerEvent> events) =>
        events.ShouldHaveSingleItem().ShouldBeOfType<UnstaffedShiftSummaryTriggerEvent>();

    [Test]
    public async Task SeveralGapsOfOneRoot_AreOneCollectiveEvent()
    {
        var root = AddRoot("Bern");
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        ServeRowsFor(root.Id,
            Row(early, Today.AddDays(2)),
            Row(late, Today.AddDays(2)),
            Row(early, Today.AddDays(5), engaged: 1, quantity: 2),
            Row(late, Today.AddDays(6), engaged: 1));

        var summary = Single(await CreateSut().DetectAsync());

        summary.GroupId.ShouldBe(root.Id);
        summary.GroupName.ShouldBe("Bern");
        summary.GapCount.ShouldBe(3);
        summary.DayCount.ShouldBe(2);
        summary.FirstGapDay.ShouldBe(Today.AddDays(2));
        summary.DaysUntilFirstGap.ShouldBe(2);
        summary.PeriodStart.ShouldBe(new DateOnly(2026, 10, 1));
        summary.PeriodEnd.ShouldBe(new DateOnly(2026, 10, 31));
        summary.Severity.ShouldBe(AgentTriggerSeverity.High);
    }

    [Test]
    public async Task ScansTheRootSubtreeOfTheRealPlan_WithoutUngroupedSporadicOrContainerShifts()
    {
        var root = AddRoot("Bern");

        await CreateSut().DetectAsync();

        var filter = _filters.ShouldHaveSingleItem();
        filter.StartDate.ShouldBe(Today);
        filter.EndDate.ShouldBe(new DateOnly(2026, 10, 31));
        filter.SelectedGroup.ShouldBe(root.Id);
        filter.IsStandartShift.ShouldBeTrue();
        filter.IsTimeRange.ShouldBeTrue();
        filter.IsSporadic.ShouldBeFalse();
        filter.Container.ShouldBeFalse();
        filter.ShowUngroupedShifts.ShouldBeFalse();
        filter.AnalyseToken.ShouldBeNull();
        filter.StartRow.ShouldBe(0);
        filter.RowCount.ShouldBe(int.MaxValue);
        filter.HolidayDates.ShouldNotBeNull();
    }

    [Test]
    public async Task Horizon_ReachesAtLeastSevenDays_WhenThePeriodEndsSooner()
    {
        _clock.Now = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
        var root = AddRoot("Bern");
        var shift = Guid.NewGuid();
        ServeRowsFor(root.Id, Row(shift, new DateOnly(2026, 11, 3)));

        var summary = Single(await CreateSut().DetectAsync());

        _filters.Single().EndDate.ShouldBe(new DateOnly(2026, 11, 4));
        summary.PeriodStart.ShouldBe(new DateOnly(2026, 10, 1));
        summary.PeriodEnd.ShouldBe(new DateOnly(2026, 11, 4));
        summary.FirstGapDay.ShouldBe(new DateOnly(2026, 11, 3));
    }

    [Test]
    public async Task Horizon_IsTheRunningPeriod_WhenItEndsLaterThanSevenDays()
    {
        AddRoot("Bern");

        await CreateSut().DetectAsync();

        _filters.Single().EndDate.ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Test]
    public async Task IndividualRoot_IsSkippedWithoutAnyScan()
    {
        AddRoot("Custom", PaymentInterval.Individual);

        var events = await CreateSut().DetectAsync();

        events.ShouldBeEmpty();
        _filters.ShouldBeEmpty();
    }

    [Test]
    public async Task RootWithoutAnyStaffing_IsNotScanned()
    {
        AddRoot("Empty");
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Guid>)Array.Empty<Guid>());

        var events = await CreateSut().DetectAsync();

        events.ShouldBeEmpty();
        _filters.ShouldBeEmpty();
    }

    [Test]
    public async Task OnlyChildGroupsAreStaffed_TheRootIsStillScanned()
    {
        var root = AddRoot("Company", lft: 1, rgt: 4);
        var branch = AddChild(root, "Branch", 2, 3);
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Guid>)new[] { branch.Id });

        await CreateSut().DetectAsync();

        _filters.ShouldHaveSingleItem().SelectedGroup.ShouldBe(root.Id);
    }

    [Test]
    public async Task NoGap_EmitsNothing()
    {
        var root = AddRoot("Bern");
        ServeRowsFor(root.Id,
            Row(Guid.NewGuid(), Today.AddDays(1), engaged: 1),
            Row(Guid.NewGuid(), Today.AddDays(2), engaged: 2, sumEmployees: 2));

        var events = await CreateSut().DetectAsync();

        events.ShouldBeEmpty();
        await _sealedDayRepository.DidNotReceiveWithAnyArgs().GetRangeAsync(default, default, default, default);
    }

    [Test]
    public async Task PastSporadicAndTemplateCoveredDays_AreNoGaps()
    {
        var root = AddRoot("Bern");
        ServeRowsFor(root.Id,
            Row(Guid.NewGuid(), Today.AddDays(-1)),
            Row(Guid.NewGuid(), Today.AddDays(3), isSporadic: true),
            Row(Guid.NewGuid(), Today.AddDays(4), isInTemplateContainer: true));

        var events = await CreateSut().DetectAsync();

        events.ShouldBeEmpty();
    }

    [Test]
    public async Task EveryRootGetsItsOwnEvent()
    {
        var bern = AddRoot("Bern", lft: 1, rgt: 2);
        var zurich = AddRoot("Zurich", lft: 3, rgt: 4);
        ServeRowsFor(bern.Id, Row(Guid.NewGuid(), Today.AddDays(1)));
        ServeRowsFor(zurich.Id, Row(Guid.NewGuid(), Today.AddDays(9)), Row(Guid.NewGuid(), Today.AddDays(10)));

        var events = (await CreateSut().DetectAsync()).Cast<UnstaffedShiftSummaryTriggerEvent>().ToList();

        events.Select(e => e.GroupId).ShouldBe(new[] { bern.Id, zurich.Id }, ignoreOrder: true);
        events.Single(e => e.GroupId == zurich.Id).GapCount.ShouldBe(2);
        events.Single(e => e.GroupId == zurich.Id).Severity.ShouldBe(AgentTriggerSeverity.Low);
    }

    [Test]
    public async Task SealedDays_GlobalOrInsideTheSubtree_AreRemovedRootWide_ForeignSealsAreNot()
    {
        var root = AddRoot("Company", lft: 1, rgt: 4);
        var branch = AddChild(root, "Branch", 2, 3);
        var foreignRoot = AddRoot("Other", lft: 5, rgt: 6);
        var shift = Guid.NewGuid();
        ServeRowsFor(root.Id,
            Row(shift, Today.AddDays(1)),
            Row(shift, Today.AddDays(2)),
            Row(shift, Today.AddDays(3)));
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Is<Guid?>(groupId => groupId == null), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>
            {
                new() { Date = Today.AddDays(1), GroupId = null },
                new() { Date = Today.AddDays(2), GroupId = branch.Id },
                new() { Date = Today.AddDays(3), GroupId = foreignRoot.Id }
            });

        var events = (await CreateSut().DetectAsync()).Cast<UnstaffedShiftSummaryTriggerEvent>().ToList();

        var summary = events.Single(e => e.GroupId == root.Id);
        summary.GapCount.ShouldBe(1);
        summary.FirstGapDay.ShouldBe(Today.AddDays(3));
        await _sealedDayRepository.Received(1).GetRangeAsync(Today, Arg.Any<DateOnly>(), Arg.Is<Guid?>(groupId => groupId == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompanyHolidays_AllActiveMembersAwayAllDay_AreRemoved()
    {
        var root = AddRoot("Bern");
        var shift = Guid.NewGuid();
        var anna = Guid.NewGuid();
        ServeRowsFor(root.Id, Row(shift, Today.AddDays(1)), Row(shift, Today.AddDays(2)));
        _absenceRepository.GetMembershipWindowsAsync(root.Id, Today, new DateOnly(2026, 10, 31), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GroupMembershipWindow>)new[] { new GroupMembershipWindow(anna, new DateOnly(2020, 1, 1), null, null, null) });
        _absenceRepository.GetFullDayAbsencesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Today, new DateOnly(2026, 10, 31), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ClientFullDayAbsence>)new[] { new ClientFullDayAbsence(anna, Today.AddDays(1)) });

        var summary = Single(await CreateSut().DetectAsync());

        summary.GapCount.ShouldBe(1);
        summary.FirstGapDay.ShouldBe(Today.AddDays(2));
    }

    [Test]
    public async Task CompanyHolidaysCoveringEveryGap_EmitNothing()
    {
        var root = AddRoot("Bern");
        var anna = Guid.NewGuid();
        ServeRowsFor(root.Id, Row(Guid.NewGuid(), Today.AddDays(1)));
        _absenceRepository.GetMembershipWindowsAsync(root.Id, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GroupMembershipWindow>)new[] { new GroupMembershipWindow(anna, new DateOnly(2020, 1, 1), null, null, null) });
        _absenceRepository.GetFullDayAbsencesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ClientFullDayAbsence>)new[] { new ClientFullDayAbsence(anna, Today.AddDays(1)) });

        var events = await CreateSut().DetectAsync();

        events.ShouldBeEmpty();
    }

    [Test]
    public async Task Holidays_OfTheRootCalendar_AreHandedToTheScanAsUtcDates()
    {
        var calendar = Guid.NewGuid();
        AddRoot("Bern", calendarSelectionId: calendar);
        var holiday = new DateOnly(2026, 10, 20);
        StubHolidays(calendar, holiday, new DateOnly(2026, 12, 25));

        await CreateSut().DetectAsync();

        var holidays = _filters.Single().HolidayDates.ShouldNotBeNull();
        holidays.ShouldBe(new[] { new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) });
        holidays.ShouldAllBe(date => date.Kind == DateTimeKind.Utc);
    }

    [Test]
    public async Task ChildWithItsOwnCalendar_IsScannedWithItsOwnHolidays_AndOwnsItsShifts()
    {
        var rootCalendar = Guid.NewGuid();
        var branchCalendar = Guid.NewGuid();
        var root = AddRoot("Company", calendarSelectionId: rootCalendar, lft: 1, rgt: 6);
        var branch = AddChild(root, "Ticino", 2, 3, branchCalendar);
        var sameCalendarChild = AddChild(root, "Bern", 4, 5);
        var branchHoliday = new DateOnly(2026, 10, 19);
        StubHolidays(rootCalendar);
        StubHolidays(branchCalendar, branchHoliday);

        var branchShift = Guid.NewGuid();
        var rootShift = Guid.NewGuid();
        ServeRowsFor(root.Id,
            Row(branchShift, branchHoliday),
            Row(branchShift, Today.AddDays(1)),
            Row(rootShift, Today.AddDays(2)));
        ServeRowsFor(branch.Id, Row(branchShift, Today.AddDays(1)));
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader,
            (branchShift, new[] { branch.Id, root.Id }),
            (rootShift, new[] { sameCalendarChild.Id }));

        var summary = Single(await CreateSut().DetectAsync());

        _filters.Select(filter => filter.SelectedGroup).ShouldBe(new Guid?[] { root.Id, branch.Id }, ignoreOrder: true);
        _filters.Single(filter => filter.SelectedGroup == branch.Id).HolidayDates
            .ShouldBe(new[] { branchHoliday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) });
        _filters.Single(filter => filter.SelectedGroup == root.Id).HolidayDates.ShouldBeEmpty();
        summary.GapCount.ShouldBe(2);
        summary.DayCount.ShouldBe(2);
        summary.FirstGapDay.ShouldBe(Today.AddDays(1));
    }

    [Test]
    public async Task ChildWithTheSameCalendarAsTheRoot_IsNoSeparateCluster()
    {
        var calendar = Guid.NewGuid();
        var root = AddRoot("Company", calendarSelectionId: calendar, lft: 1, rgt: 6);
        AddChild(root, "Explicit", 2, 3, calendar);
        AddChild(root, "Inherited", 4, 5);

        await CreateSut().DetectAsync();

        _filters.ShouldHaveSingleItem().SelectedGroup.ShouldBe(root.Id);
        await _groupScopeReader.DidNotReceiveWithAnyArgs().GetGroupIdsByShiftIdsAsync(default!, default);
    }

    [Test]
    public async Task CompanyDay_IsTheConfiguredZonesDay_NotTheUtcDay()
    {
        _clock.Now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        _clock.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var root = AddRoot("Auckland");
        ServeRowsFor(root.Id,
            Row(Guid.NewGuid(), new DateOnly(2026, 10, 13)),
            Row(Guid.NewGuid(), new DateOnly(2026, 10, 14)));

        var summary = Single(await CreateSut().DetectAsync());

        _filters.Single().StartDate.ShouldBe(new DateOnly(2026, 10, 14));
        summary.GapCount.ShouldBe(1);
        summary.FirstGapDay.ShouldBe(new DateOnly(2026, 10, 14));
        summary.DaysUntilFirstGap.ShouldBe(0);
    }

    [Test]
    public async Task DetectAndFingerprints_ShareOneScan_AndTheFingerprintsEqualTheEvents()
    {
        var bern = AddRoot("Bern", lft: 1, rgt: 2);
        var zurich = AddRoot("Zurich", lft: 3, rgt: 4);
        ServeRowsFor(bern.Id, Row(Guid.NewGuid(), Today.AddDays(1)));
        ServeRowsFor(zurich.Id, Row(Guid.NewGuid(), Today.AddDays(2)));
        var sut = CreateSut();

        var events = await sut.DetectAsync();
        var fingerprints = await sut.GetActiveFingerprintsAsync();

        _filters.Count.ShouldBe(2);
        await _groupRepository.Received(1).List();
        fingerprints.ShouldBe(
            events.Select(e => AgentConditionLedgerPolicy.FingerprintFor(sut.Kind, e.DedupKey)).ToHashSet(),
            ignoreOrder: true);
        fingerprints.ShouldContain(AgentConditionLedgerPolicy.FingerprintFor(
            AgentTriggerKinds.UnstaffedShift,
            UnstaffedShiftSummaryTriggerEvent.DedupKeyFor(bern.Id, new DateOnly(2026, 10, 1))));
    }

    [Test]
    public async Task FingerprintsWithoutGaps_AreEmpty_SoReconcileResolvesTheOldRows()
    {
        AddRoot("Bern");

        var fingerprints = await CreateSut().GetActiveFingerprintsAsync();

        fingerprints.ShouldBeEmpty();
    }
}
