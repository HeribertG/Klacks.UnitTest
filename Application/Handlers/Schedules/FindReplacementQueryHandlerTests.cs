// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for FindReplacementQueryHandler: hard-exclusion on collision / rest-time / blacklist /
/// absence, soft-ranking by aggregate findings (less headroom -> lower rank), preferred-first ordering.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class FindReplacementQueryHandlerTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 10);
    private static readonly TimeOnly Start = new(22, 0);
    private static readonly TimeOnly End = new(6, 0);

    private IClientRepository _clientRepo = null!;
    private IPreCommitConflictChecker _checker = null!;
    private IClientShiftPreferenceRepository _prefRepo = null!;
    private IScheduleEntriesService _scheduleEntries = null!;
    private FindReplacementQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepo = Substitute.For<IClientRepository>();
        _checker = Substitute.For<IPreCommitConflictChecker>();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _prefRepo = Substitute.For<IClientShiftPreferenceRepository>();
        _prefRepo.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientShiftPreference>());
        _scheduleEntries = Substitute.For<IScheduleEntriesService>();
        SetOnLeave();

        _handler = new FindReplacementQueryHandler(_clientRepo, _checker, _prefRepo, _scheduleEntries);
    }

    private void SetMembers(params Client[] members)
        => _clientRepo.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(members.ToList());

    private void SetConflicts(params ScheduleValidationNotificationDto[] conflicts)
        => _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts.ToList()));

    private void SetOnLeave(params ScheduleCell[] breakCells)
        => _scheduleEntries.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(breakCells));

    private void SetPreferences(params ClientShiftPreference[] prefs)
        => _prefRepo.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(prefs.ToList());

    private Task<ReplacementSearchResult> Find()
        => _handler.Handle(new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null), CancellationToken.None);

    private static Client Member(Guid id, string name) => new() { Id = id, Name = name, FirstName = string.Empty };

    private static ScheduleValidationNotificationDto Conflict(Guid clientId, ScheduleValidationType type, string comment)
        => new() { Type = type, ClientId = clientId, Date = Date, Comment = comment };

    [Test]
    public async Task ExcludesCollision_RanksCleanBeforeSoft()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        SetMembers(Member(a, "Anna"), Member(b, "Bob"), Member(c, "Cara"));
        SetConflicts(
            Conflict(a, ScheduleValidationType.Error, "schedule.error-list.collision"),
            Conflict(b, ScheduleValidationType.Warning, "schedule.error-list.weekly-overtime"));

        var result = await Find();

        result.Eligible.Count.ShouldBe(2);
        result.Eligible[0].ClientId.ShouldBe(c);
        result.Eligible[0].SoftConflicts.Count.ShouldBe(0);
        result.Eligible[1].ClientId.ShouldBe(b);
        result.Excluded.Single().ClientId.ShouldBe(a);
        result.Excluded.Single().Reason.ShouldBe("schedule.error-list.collision");
    }

    [Test]
    public async Task RestViolation_IsHardExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(Conflict(a, ScheduleValidationType.Warning, "schedule.error-list.rest-violation"));

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("schedule.error-list.rest-violation");
    }

    [Test]
    public async Task Blacklisted_IsExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetPreferences(new ClientShiftPreference { ClientId = a, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Blacklist });

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("blacklisted");
    }

    [Test]
    public async Task Preferred_RanksFirst_EvenWithSoftConflict()
    {
        var preferred = Guid.NewGuid();
        var clean = Guid.NewGuid();
        SetMembers(Member(preferred, "Pia"), Member(clean, "Cara"));
        SetConflicts(Conflict(preferred, ScheduleValidationType.Warning, "schedule.error-list.overtime"));
        SetPreferences(new ClientShiftPreference { ClientId = preferred, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Preferred });

        var result = await Find();

        result.Eligible[0].ClientId.ShouldBe(preferred);
        result.Eligible[0].IsPreferred.ShouldBeTrue();
    }

    [Test]
    public async Task OnLeaveMember_IsExcludedAsAbsent()
    {
        var onLeave = Guid.NewGuid();
        var free = Guid.NewGuid();
        SetMembers(Member(onLeave, "Lena"), Member(free, "Cara"));
        SetOnLeave(new ScheduleCell
        {
            ClientId = onLeave,
            EntryType = (int)ScheduleEntryType.Break,
            EntryDate = Date.ToDateTime(TimeOnly.MinValue)
        });

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(free);
        result.Excluded.Single().Reason.ShouldBe("absent");
    }

    [Test]
    public async Task NoMembers_ReturnsEmpty()
    {
        SetMembers();

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.ShouldBeEmpty();
    }
}
