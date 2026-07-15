// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for FindReplacementQueryHandler: hard-exclusion on collision / rest-time / blacklist /
/// absence / explicit unavailability, soft-ranking by aggregate findings (less headroom -> lower rank),
/// preferred-first ordering.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

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
    private IClientAvailabilityRepository _availabilityRepo = null!;
    private IPeriodHoursService _periodHours = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
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
        _availabilityRepo = Substitute.For<IClientAvailabilityRepository>();
        SetAvailability();
        _periodHours = Substitute.For<IPeriodHoursService>();
        _periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        SetPeriodHours();

        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _handler = new FindReplacementQueryHandler(
            _clientRepo, _checker, _prefRepo, _scheduleEntries, _availabilityRepo, _periodHours,
            _overrideAuthorizer, _httpContextAccessor, NullLogger<FindReplacementQueryHandler>.Instance);
    }

    private void SetPeriodHours(Dictionary<Guid, PeriodHoursResource>? byClient = null)
        => _periodHours.GetPeriodHoursAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(byClient ?? new Dictionary<Guid, PeriodHoursResource>());

    private static PeriodHoursResource Hours(decimal target, decimal worked)
        => new() { GuaranteedHours = target, Hours = worked };

    private void SetAvailability(params ClientAvailability[] entries)
        => _availabilityRepo.GetByDateRange(Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(entries.ToList());

    private static ClientAvailability Unavailable(Guid clientId, int hour) => new()
    {
        ClientId = clientId,
        Date = Date,
        Hour = hour,
        IsAvailable = false
    };

    private static ClientAvailability Available(Guid clientId, int hour) => new()
    {
        ClientId = clientId,
        Date = Date,
        Hour = hour,
        IsAvailable = true
    };

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

    private Task<ReplacementSearchResult> Find(bool overrideBlock = false)
        => _handler.Handle(new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null, overrideBlock), CancellationToken.None);

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
    public async Task BlockModeEscalatedRestViolation_NoOverride_StaysExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = a,
            Date = Date,
            Comment = "schedule.error-list.rest-violation",
            CommentParams = new Dictionary<string, string> { ["enforcementRule"] = "minRestHours" },
        });
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("schedule.error-list.rest-violation");
    }

    [Test]
    public async Task BlockModeEscalatedRestViolation_WithAuthorizedOverride_StaysEligible()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = a,
            Date = Date,
            Comment = "schedule.error-list.rest-violation",
            CommentParams = new Dictionary<string, string> { ["enforcementRule"] = "minRestHours" },
        });
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await Find(overrideBlock: true);

        result.Excluded.ShouldBeEmpty();
        result.Eligible.Single().ClientId.ShouldBe(a);
    }

    [Test]
    public async Task Collision_NeverOverridable_EvenWhenAuthorized()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(Conflict(a, ScheduleValidationType.Error, "schedule.error-list.collision"));
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(true);

        var result = await Find(overrideBlock: true);

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("schedule.error-list.collision");
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
    public async Task ExplicitlyUnavailableInShiftHour_IsExcluded()
    {
        var blocked = Guid.NewGuid();
        var free = Guid.NewGuid();
        SetMembers(Member(blocked, "Bea"), Member(free, "Cara"));
        SetAvailability(Unavailable(blocked, 22));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(free);
        result.Excluded.Single().ClientId.ShouldBe(blocked);
        result.Excluded.Single().Reason.ShouldBe("unavailable");
    }

    [Test]
    public async Task UnavailableOutsideShiftHours_IsNotExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetAvailability(Unavailable(a, 12));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(a);
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task UnavailableNextDayHour_IsNotExcluded_DocumentedV1Limitation()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetAvailability(Unavailable(a, 2));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(a);
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task AvailableRecordsCoveringTheShift_IsNotExcluded()
    {
        // Shift 22:00-06:00 occupies same-day hours 22 and 23; both marked available -> eligible.
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetAvailability(Available(a, 22), Available(a, 23));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(a);
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task AvailableRecordsCoveringOnlyPartOfTheShift_IsExcluded()
    {
        // Marked available at 22 only; the 22:00-06:00 shift also needs hour 23 -> positively unavailable.
        var blocked = Guid.NewGuid();
        SetMembers(Member(blocked, "Bea"));
        SetAvailability(Available(blocked, 22));

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().ClientId.ShouldBe(blocked);
        result.Excluded.Single().Reason.ShouldBe("unavailable");
    }

    [Test]
    public async Task UnderTargetCandidate_RanksAboveCloserToTarget()
    {
        var under = Guid.NewGuid();
        var nearTarget = Guid.NewGuid();
        SetMembers(Member(nearTarget, "Nora"), Member(under, "Udo"));
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource>
        {
            [under] = Hours(target: 160m, worked: 40m),       // deficit 120
            [nearTarget] = Hours(target: 160m, worked: 120m)  // deficit 40
        });

        var result = await Find();

        result.Eligible[0].ClientId.ShouldBe(under);
        result.Eligible[0].TargetHoursDeficit.ShouldBe(120m);
        result.Eligible[1].ClientId.ShouldBe(nearTarget);
    }

    [Test]
    public async Task FewerSoftConflicts_OutranksLargerDeficit()
    {
        var clean = Guid.NewGuid();
        var soft = Guid.NewGuid();
        SetMembers(Member(clean, "Cara"), Member(soft, "Sven"));
        SetConflicts(Conflict(soft, ScheduleValidationType.Warning, "schedule.error-list.weekly-overtime"));
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource>
        {
            [clean] = Hours(target: 160m, worked: 150m), // small deficit 10, but no soft conflict
            [soft] = Hours(target: 160m, worked: 0m)     // large deficit 160, but has a soft conflict
        });

        var result = await Find();

        // Soft-conflict count is the higher-priority tiebreaker; the clean candidate wins.
        result.Eligible[0].ClientId.ShouldBe(clean);
        result.Eligible[1].ClientId.ShouldBe(soft);
    }

    [Test]
    public async Task NoTargetData_DeficitNeutral_FallsBackToName()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SetMembers(Member(b, "Bob"), Member(a, "Ann"));
        // No period-hours entries at all -> both deficit 0 -> alphabetical fallback.

        var result = await Find();

        result.Eligible[0].TargetHoursDeficit.ShouldBe(0m);
        result.Eligible[0].ClientId.ShouldBe(a);
        result.Eligible[1].ClientId.ShouldBe(b);
    }

    [Test]
    public async Task NoTarget_WorkedHours_SortBelowNoTargetIdle()
    {
        var idle = Guid.NewGuid();
        var busy = Guid.NewGuid();
        SetMembers(Member(busy, "Bea"), Member(idle, "Ida"));
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource>
        {
            [idle] = Hours(target: 0m, worked: 0m),   // deficit 0
            [busy] = Hours(target: 0m, worked: 30m)   // deficit -30 (already loaded) -> ranks lower
        });

        var result = await Find();

        result.Eligible[0].ClientId.ShouldBe(idle);
        result.Eligible[0].TargetHoursDeficit.ShouldBe(0m);
        result.Eligible[1].ClientId.ShouldBe(busy);
        result.Eligible[1].TargetHoursDeficit.ShouldBe(-30m);
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
