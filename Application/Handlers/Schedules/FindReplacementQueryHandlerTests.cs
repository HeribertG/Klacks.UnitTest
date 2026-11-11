// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for FindReplacementQueryHandler: hard-exclusion on collision / rest-time / blacklist /
/// absence / explicit unavailability, soft-ranking by aggregate findings (less headroom -> lower rank),
/// preferred-first ordering, and group visibility: a hidden group is answered exactly like an unknown one and
/// the candidate and excluded lists only ever contain clients the caller may see (real guards over substituted scopes).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.Api.Infrastructure.Services.Clients;

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
    private IGroupRepository _groupRepo = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClientService = null!;
    private IPreCommitConflictChecker _checker = null!;
    private IClientShiftPreferenceRepository _prefRepo = null!;
    private IScheduleEntriesService _scheduleEntries = null!;
    private IClientAvailabilityRepository _availabilityRepo = null!;
    private IPeriodHoursService _periodHours = null!;
    private IClientContractDataProvider _contractProvider = null!;
    private readonly Dictionary<Guid, EffectiveContractData> _contracts = [];
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IAbsenceRepository _absenceRepo = null!;
    private IGroupVisibilityService _groupVisibilityService = null!;
    private IUserService _userService = null!;
    private IClientSearchRepository _clientSearchRepo = null!;
    private IClientGroupFilterService _clientGroupFilter = null!;
    private FindReplacementQueryHandler _handler = null!;
    private static readonly Guid OnCallAbsenceId = Guid.NewGuid();
    private static readonly Guid SickAbsenceId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepo = Substitute.For<IClientRepository>();
        _groupRepo = Substitute.For<IGroupRepository>();
        _groupRepo.Get(GroupId).Returns(new Group { Id = GroupId });
        _groupClientService = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _groupClientService.GetRealClientIdsFromGroupAndSubgroups(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());
        _contracts.Clear();
        _contractProvider = Substitute.For<IClientContractDataProvider>();
        _contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(_ => new Dictionary<Guid, EffectiveContractData>(_contracts));
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

        _absenceRepo = Substitute.For<IAbsenceRepository>();
        _absenceRepo.GetOnCallAbsenceIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { OnCallAbsenceId });

        _userService = Substitute.For<IUserService>();
        _userService.GetIdString().Returns(Guid.NewGuid().ToString());
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _clientSearchRepo = Substitute.For<IClientSearchRepository>();
        _clientGroupFilter = Substitute.For<IClientGroupFilterService>();
        SetAdminCaller();

        _handler = new FindReplacementQueryHandler(
            new ReplacementCandidatePoolResolver(_clientRepo, _groupRepo, _groupClientService), _checker, _prefRepo, _scheduleEntries,
            _availabilityRepo, _periodHours, _contractProvider,
            _overrideAuthorizer, _httpContextAccessor, _absenceRepo,
            new GroupVisibilityGuard(_groupVisibilityService, _userService),
            new ClientVisibilityGuard(_clientSearchRepo, _clientGroupFilter),
            Klacks.UnitTest.TestHelpers.TestScenarioTokenAccess.AllVisible(),
            NullLogger<FindReplacementQueryHandler>.Instance);
    }

    private static ScheduleCell BreakCell(Guid clientId, Guid absenceId, int startHour = 0, int endHour = 0) => new()
    {
        ClientId = clientId,
        EntryType = (int)ScheduleEntryType.Break,
        EntryDate = Date.ToDateTime(TimeOnly.MinValue),
        EntryId = absenceId,
        StartTime = TimeSpan.FromHours(startHour),
        EndTime = TimeSpan.FromHours(endHour)
    };

    [Test]
    public async Task OnCallBreakOutsideTheSlot_NeitherExcludesNorPrefers()
    {
        var onCallMorning = Guid.NewGuid();
        var other = Guid.NewGuid();
        SetMembers(Member(onCallMorning, "Anna"), Member(other, "Bea"));
        SetOnLeave(BreakCell(onCallMorning, OnCallAbsenceId, 8, 12));

        var result = await Find();

        result.Excluded.ShouldBeEmpty();
        result.Eligible.Count.ShouldBe(2);
        result.Eligible.ShouldAllBe(c => !c.IsOnCall);
    }

    [Test]
    public async Task PartialAbsenceOutsideTheNightSlot_DoesNotExcludeTheCandidate_ButOneOverlappingItDoes()
    {
        var morning = Guid.NewGuid();
        var evening = Guid.NewGuid();
        var wholeDay = Guid.NewGuid();
        SetMembers(Member(morning, "Anna"), Member(evening, "Bea"), Member(wholeDay, "Cora"));
        SetOnLeave(
            BreakCell(morning, SickAbsenceId, 8, 10),
            BreakCell(evening, SickAbsenceId, 20, 23),
            BreakCell(wholeDay, SickAbsenceId));

        var result = await Find();

        result.Eligible.Select(c => c.ClientId).ShouldBe(new[] { morning });
        result.Excluded.Select(c => c.ClientId).ShouldBe(new[] { evening, wholeDay }, ignoreOrder: true);
    }

    [Test]
    public async Task AbsenceInTheLeadOfTheSlot_ExcludesTheCandidate_OnlyWithTheLead()
    {
        var briefed = Guid.NewGuid();
        SetMembers(Member(briefed, "Anna"));
        SetOnLeave(new ScheduleCell
        {
            ClientId = briefed,
            EntryType = (int)ScheduleEntryType.Break,
            EntryDate = Date.ToDateTime(TimeOnly.MinValue),
            EntryId = SickAbsenceId,
            StartTime = new TimeSpan(20, 0, 0),
            EndTime = new TimeSpan(21, 30, 0),
        });

        var bare = await Find();
        var withLead = await _handler.Handle(
            new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null, false, 1.5m), CancellationToken.None);

        bare.Eligible.ShouldHaveSingleItem().ClientId.ShouldBe(briefed);
        withLead.Excluded.ShouldHaveSingleItem().ClientId.ShouldBe(briefed);
    }

    [Test]
    public async Task OnCallBreakCrossingMidnight_OverlappingTheNightSlot_IsOnCall()
    {
        var onCallNight = Guid.NewGuid();
        var other = Guid.NewGuid();
        SetMembers(Member(other, "Anna"), Member(onCallNight, "Zora"));
        SetOnLeave(BreakCell(onCallNight, OnCallAbsenceId, 23, 1));

        var result = await Find();

        result.Eligible[0].ClientId.ShouldBe(onCallNight);
        result.Eligible[0].IsOnCall.ShouldBeTrue();
    }

    [Test]
    public async Task LeadTrailAndExtraPaidHours_ReachTheCandidateCheck()
    {
        SetMembers(Member(Guid.NewGuid(), "Anna"), Member(Guid.NewGuid(), "Bea"));

        await _handler.Handle(
            new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null, false, 1.5m, 0.5m, 2m),
            CancellationToken.None);

        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows =>
                rows.Count == 2 && rows.All(r => r.LeadHours == 1.5m && r.TrailHours == 0.5m && r.ExtraPaidHours == 2m)),
            (Guid?)null,
            CancellationToken.None);
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
    {
        _clientRepo.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(members.ToList());
        _groupClientService.GetRealClientIdsFromGroupAndSubgroups(GroupId, Arg.Any<CancellationToken>())
            .Returns(members.Select(m => m.Id).ToList());
    }

    private void SetConflicts(params ScheduleValidationNotificationDto[] conflicts)
        => _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts.ToList()));

    private void SetOnLeave(params ScheduleCell[] breakCells)
        => ScheduleEntriesStub.Serve(_scheduleEntries, breakCells);

    private void SetPreferences(params ClientShiftPreference[] prefs)
        => _prefRepo.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(prefs.ToList());

    private Task<ReplacementSearchResult> Find(bool overrideBlock = false)
        => _handler.Handle(new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null, overrideBlock), CancellationToken.None);

    private Task<ReplacementSearchResult> FindIn(Guid groupId)
        => _handler.Handle(new FindReplacementQuery(ShiftId, Date, Start, End, groupId, null, false), CancellationToken.None);

    private static Client Member(Guid id, string name) => new() { Id = id, Name = name, FirstName = string.Empty };

    private static ScheduleCell NightWork(Guid clientId, DateOnly date) => new()
    {
        ClientId = clientId,
        EntryType = (int)ScheduleEntryType.Work,
        EntryDate = date.ToDateTime(TimeOnly.MinValue),
        EntryId = Guid.NewGuid(),
        StartTime = TimeSpan.FromHours(Start.Hour),
        EndTime = TimeSpan.FromHours(End.Hour)
    };

    [Test]
    public async Task CustomerUnderTheRoot_IsNeitherEligibleNorExcluded()
    {
        var anna = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var externalEmployee = Guid.NewGuid();
        SetMembers(
            Member(anna, "Anna"),
            new Client { Id = customer, Name = "Customer", FirstName = string.Empty, Type = EntityTypeEnum.Customer },
            new Client { Id = externalEmployee, Name = "External", FirstName = string.Empty, Type = EntityTypeEnum.ExternEmp });

        var result = await Find();

        result.Eligible.Select(c => c.ClientId).ShouldBe(new[] { anna, externalEmployee }, ignoreOrder: true);
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task CandidateWhoseNightBlockTheCoverCloses_RanksBeforeALonelyNight()
    {
        // Arrange: equally clean candidates; Zora works nights the day before and after, Anna is free around.
        var anna = Guid.NewGuid();
        var zora = Guid.NewGuid();
        SetMembers(Member(anna, "Anna"), Member(zora, "Zora"));
        SetOnLeave(NightWork(zora, Date.AddDays(-1)), NightWork(zora, Date.AddDays(1)));

        // Act
        var result = await Find();

        // Assert
        result.Eligible.Select(c => c.ClientId).ShouldBe(new[] { zora, anna });
        result.Eligible[0].RhythmDelta.ShouldBeLessThan(0);
        result.Eligible[1].RhythmDelta.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Rhythm_IsOnlyATieBreak_ThePreferredCandidateStillRanksFirst()
    {
        // Arrange: Anna prefers the shift but would work it alone; Zora would close her night block.
        var anna = Guid.NewGuid();
        var zora = Guid.NewGuid();
        SetMembers(Member(anna, "Anna"), Member(zora, "Zora"));
        SetOnLeave(NightWork(zora, Date.AddDays(-1)), NightWork(zora, Date.AddDays(1)));
        SetPreferences(new ClientShiftPreference { ClientId = anna, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Preferred });

        // Act
        var result = await Find();

        // Assert
        result.Eligible[0].ClientId.ShouldBe(anna);
    }

    private static ScheduleValidationNotificationDto Conflict(Guid clientId, ScheduleValidationType type, string comment)
        => new() { Type = type, ClientId = clientId, Date = Date, Comment = comment };

    // IClientRepository filters on the group ROOT (Group.Root ?? Group.Id). Stubbing on the root id is
    // what makes this test meaningful: handing the sub-group id straight to the repository - the state
    // before the fix - matches nothing and yields an empty candidate list without any error.
    private void SetMembersUnderRoot(Guid rootId, params Client[] members)
    {
        _clientRepo.GetActiveClientsWithAddressesForGroupsAsync(
                Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
        _clientRepo.GetActiveClientsWithAddressesForGroupsAsync(
                Arg.Is<List<Guid>>(ids => ids.Contains(rootId)), Arg.Any<CancellationToken>())
            .Returns(members.ToList());
    }

    [Test]
    public async Task SubGroupId_ResolvesRootBeforeQuerying_AndReturnsCandidates()
    {
        var rootId = Guid.NewGuid();
        var subGroupId = Guid.NewGuid();
        var anna = Member(Guid.NewGuid(), "Anna");

        _groupRepo.Get(subGroupId).Returns(new Group { Id = subGroupId, Root = rootId });
        SetMembersUnderRoot(rootId, anna);
        _groupClientService.GetRealClientIdsFromGroupAndSubgroups(subGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { anna.Id });

        var result = await FindIn(subGroupId);

        result.Eligible.Single().ClientId.ShouldBe(anna.Id);
    }

    [Test]
    public async Task SubGroupId_OffersSiblingGroupMembersOfTheSameRoot_AsBorrowed_AfterTheOwnGroup()
    {
        var rootId = Guid.NewGuid();
        var subGroupId = Guid.NewGuid();
        var anna = Member(Guid.NewGuid(), "Anna");
        var sibling = Member(Guid.NewGuid(), "Sibling");

        _groupRepo.Get(subGroupId).Returns(new Group { Id = subGroupId, Root = rootId });
        SetMembersUnderRoot(rootId, anna, sibling);
        _groupClientService.GetRealClientIdsFromGroupAndSubgroups(subGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { anna.Id });

        var result = await FindIn(subGroupId);

        result.Eligible.Select(c => (c.ClientId, c.IsBorrowed)).ShouldBe([(anna.Id, false), (sibling.Id, true)]);
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownGroupId_ReturnsEmptyInsteadOfThrowing()
    {
        var unknownGroupId = Guid.NewGuid();
        _groupRepo.Get(unknownGroupId).Returns((Group?)null);

        var result = await FindIn(unknownGroupId);

        result.Eligible.ShouldBeEmpty();
        result.Excluded.ShouldBeEmpty();
        await _clientRepo.DidNotReceive().GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>());
    }

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
    public async Task BlockEscalatedEnforcementError_OutsideLegacyCommentSet_IsHardExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = a,
            Date = Date,
            Comment = "schedule.error-list.restricted-time-window",
            CommentParams = new Dictionary<string, string> { ["enforcementRule"] = "restrictedTimeWindow" },
        });
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("schedule.error-list.restricted-time-window");
    }

    [Test]
    public async Task BlockEscalatedEnforcementError_WithAuthorizedOverride_StaysEligible()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = a,
            Date = Date,
            Comment = "schedule.error-list.period-cap",
            CommentParams = new Dictionary<string, string> { ["enforcementRule"] = "periodCap" },
        });
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await Find(overrideBlock: true);

        result.Excluded.ShouldBeEmpty();
        result.Eligible.Single().ClientId.ShouldBe(a);
    }

    [Test]
    public async Task ErrorWithoutEnforcementTag_OutsideLegacyCommentSet_IsExcluded_EvenWhenAuthorized()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(Conflict(a, ScheduleValidationType.Error, "schedule.error-list.counter-rule"));
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(true);

        var result = await Find(overrideBlock: true);

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("schedule.error-list.counter-rule");
    }

    [Test]
    public async Task WarningOutsideLegacyCommentSet_StaysSoftConflict_NotExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetConflicts(Conflict(a, ScheduleValidationType.Warning, "schedule.error-list.restricted-time-window"));

        var result = await Find();

        result.Excluded.ShouldBeEmpty();
        var candidate = result.Eligible.Single();
        candidate.ClientId.ShouldBe(a);
        candidate.SoftConflicts.Single().Comment.ShouldBe("schedule.error-list.restricted-time-window");
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
    public async Task OnCallMember_IsEligible_FlaggedAndRankedFirst()
    {
        var onCall = Guid.NewGuid();
        var preferred = Guid.NewGuid();
        SetMembers(Member(preferred, "Anna"), Member(onCall, "Zora"));
        SetPreferences(new ClientShiftPreference { ClientId = preferred, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Preferred });
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource> { [preferred] = Hours(160m, 40m) });
        SetOnLeave(BreakCell(onCall, OnCallAbsenceId));

        var result = await Find();

        result.Excluded.ShouldBeEmpty();
        result.Eligible.Count.ShouldBe(2);
        result.Eligible[0].ClientId.ShouldBe(onCall);
        result.Eligible[0].IsOnCall.ShouldBeTrue();
        result.Eligible[1].IsOnCall.ShouldBeFalse();
    }

    [Test]
    public async Task OnCallMember_WithOnCallOverlapWarning_IsNotExcluded()
    {
        var onCall = Guid.NewGuid();
        SetMembers(Member(onCall, "Zora"));
        SetOnLeave(BreakCell(onCall, OnCallAbsenceId));
        SetConflicts(Conflict(onCall, ScheduleValidationType.Warning, ScheduleValidationKeys.OnCallOverlap));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(onCall);
        result.Eligible.Single().IsOnCall.ShouldBeTrue();
    }

    [Test]
    public async Task OnCallAndSickOnTheSameDay_IsExcludedAsAbsent()
    {
        var both = Guid.NewGuid();
        SetMembers(Member(both, "Lena"));
        SetOnLeave(BreakCell(both, OnCallAbsenceId), BreakCell(both, SickAbsenceId));

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
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
    public async Task UnavailableEarlyHourOfTheShiftDate_IsNotExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        SetAvailability(Unavailable(a, 2));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(a);
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task UnavailableNextDayHourInsideTheNightShift_IsExcluded()
    {
        var a = Guid.NewGuid();
        SetMembers(Member(a, "Anna"));
        var nextMorning = Unavailable(a, 2);
        nextMorning.Date = Date.AddDays(1);
        SetAvailability(nextMorning);

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().Reason.ShouldBe("unavailable");
        await _availabilityRepo.Received().GetByDateRange(Date, Date.AddDays(1));
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

    private void SetAdminCaller()
    {
        _groupVisibilityService.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());
        _clientGroupFilter.IsCallerUnrestrictedAsync().Returns(true);
    }

    private void SetRestrictedCaller(IReadOnlyList<Guid> visibleGroupIds, IReadOnlyCollection<Guid> visibleClientIds)
    {
        _groupVisibilityService.GetVisibilityScopeAsync()
            .Returns(GroupVisibilityScope.Restricted(visibleGroupIds, visibleGroupIds));
        _clientGroupFilter.IsCallerUnrestrictedAsync().Returns(false);
        _clientSearchRepo.FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlySet<Guid>)ci.Arg<IReadOnlyCollection<Guid>>()
                .Where(visibleClientIds.Contains).ToHashSet());
    }

    [Test]
    public async Task HiddenGroup_ReturnsEmpty_AndNeverLoadsThePool()
    {
        SetMembers(Member(Guid.NewGuid(), "Anna"), Member(Guid.NewGuid(), "Bea"));
        SetRestrictedCaller([Guid.NewGuid()], []);

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.ShouldBeEmpty();
        await _groupRepo.DidNotReceive().Get(Arg.Any<Guid>());
        await _clientRepo.DidNotReceive().GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>());
        await _checker.DidNotReceive().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenGroup_IsAnsweredExactlyLikeAnUnknownGroup()
    {
        var unknownGroupId = Guid.NewGuid();
        _groupRepo.Get(unknownGroupId).Returns((Group?)null);
        SetMembers(Member(Guid.NewGuid(), "Anna"));
        SetRestrictedCaller([unknownGroupId], []);

        var unknown = await FindIn(unknownGroupId);
        var hidden = await Find();

        hidden.Eligible.Count.ShouldBe(unknown.Eligible.Count);
        hidden.Excluded.Count.ShouldBe(unknown.Excluded.Count);
        hidden.Eligible.ShouldBeEmpty();
        hidden.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task VisibleGroup_OffersOnlyClientsTheCallerMaySee()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        SetMembers(Member(visible, "Anna"), Member(hidden, "Bea"));
        SetRestrictedCaller([GroupId], [visible]);

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(visible);
        result.Excluded.ShouldBeEmpty();
        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.All(r => r.ClientId == visible)),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _periodHours.Received(1).GetPeriodHoursAsync(
            Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { visible })),
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task ExcludedList_NeverNamesAHiddenClient()
    {
        var visibleAbsent = Guid.NewGuid();
        var hiddenAbsent = Guid.NewGuid();
        var hiddenBlacklisted = Guid.NewGuid();
        SetMembers(Member(visibleAbsent, "Anna"), Member(hiddenAbsent, "Bea"), Member(hiddenBlacklisted, "Cara"));
        SetOnLeave(BreakCell(visibleAbsent, SickAbsenceId), BreakCell(hiddenAbsent, SickAbsenceId));
        SetPreferences(new ClientShiftPreference
        {
            ClientId = hiddenBlacklisted,
            ShiftId = ShiftId,
            PreferenceType = ShiftPreferenceType.Blacklist
        });
        SetRestrictedCaller([GroupId], [visibleAbsent]);

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.Single().ClientId.ShouldBe(visibleAbsent);
    }

    [Test]
    public async Task RestrictedCallerWithoutAnyVisibleClient_GetsNothing()
    {
        SetMembers(Member(Guid.NewGuid(), "Anna"), Member(Guid.NewGuid(), "Bea"));
        SetRestrictedCaller([GroupId], []);

        var result = await Find();

        result.Eligible.ShouldBeEmpty();
        result.Excluded.ShouldBeEmpty();
        await _checker.DidNotReceive().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkillPath_FindReplacement_OnlyReportsVisibleClients()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var hiddenAbsent = Guid.NewGuid();
        SetMembers(Member(visible, "Anna"), Member(hidden, "Bea"), Member(hiddenAbsent, "Cara"));
        SetOnLeave(BreakCell(hiddenAbsent, SickAbsenceId));
        SetRestrictedCaller([GroupId], [visible]);
        var skill = SkillDispatchingToRealHandler();

        var result = await skill.ExecuteAsync(SkillContext(), SkillParams(GroupId));

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(1);
        data.GetProperty("ExcludedCount").GetInt32().ShouldBe(0);
        data.GetProperty("Candidates")[0].GetProperty("ClientId").GetGuid().ShouldBe(visible);
    }

    [Test]
    public async Task SkillPath_FindReplacement_HiddenGroupReportsNoOne()
    {
        SetMembers(Member(Guid.NewGuid(), "Anna"));
        SetRestrictedCaller([Guid.NewGuid()], []);
        var skill = SkillDispatchingToRealHandler();

        var result = await skill.ExecuteAsync(SkillContext(), SkillParams(GroupId));

        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("EligibleCount").GetInt32().ShouldBe(0);
        data.GetProperty("ExcludedCount").GetInt32().ShouldBe(0);
    }

    private FindReplacementSkill SkillDispatchingToRealHandler()
    {
        var shiftRepo = Substitute.For<IShiftRepository>();
        shiftRepo.Get(ShiftId).Returns(new Shift { Id = ShiftId, Name = "Night", StartShift = Start, EndShift = End });
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci => _handler.Handle(ci.Arg<FindReplacementQuery>(), ci.Arg<CancellationToken>()));
        return new FindReplacementSkill(
            shiftRepo, mediator, new GroupVisibilityGuard(_groupVisibilityService, _userService));
    }

    private static SkillExecutionContext SkillContext() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string>()
    };

    private static Dictionary<string, object> SkillParams(Guid groupId) => new()
    {
        ["shiftId"] = ShiftId.ToString(),
        ["date"] = Date,
        ["groupId"] = groupId.ToString()
    };

    [Test]
    public async Task Admin_IsNotLimitedByVisibility()
    {
        var anna = Guid.NewGuid();
        var bea = Guid.NewGuid();
        SetMembers(Member(anna, "Anna"), Member(bea, "Bea"));
        SetOnLeave(BreakCell(bea, SickAbsenceId));

        var result = await Find();

        result.Eligible.Single().ClientId.ShouldBe(anna);
        result.Excluded.Single().ClientId.ShouldBe(bea);
        await _clientSearchRepo.DidNotReceive().FilterVisibleToCallerAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonBindingLevelGap_StaysEligible_RankedAfterTheSufficientlyQualified()
    {
        // Arrange
        var below = Guid.NewGuid();
        var furtherBelow = Guid.NewGuid();
        var qualified = Guid.NewGuid();
        SetMembers(Member(below, "Anna"), Member(furtherBelow, "Bea"), Member(qualified, "Zoe"));
        SetConflicts(
            LevelConflict(below, ScheduleValidationType.Warning, levelDelta: 1),
            LevelConflict(furtherBelow, ScheduleValidationType.Warning, levelDelta: 2),
            Conflict(qualified, ScheduleValidationType.Warning, "schedule.error-list.weekly-overtime"),
            Conflict(qualified, ScheduleValidationType.Warning, "schedule.error-list.consecutive-days"));

        // Act
        var result = await Find();

        // Assert
        result.Excluded.ShouldBeEmpty();
        result.Eligible.Select(c => c.ClientId).ShouldBe([qualified, below, furtherBelow]);
        result.Eligible.Select(c => c.LevelShortfall).ShouldBe([0, 1, 2]);
    }

    [Test]
    public async Task BindingLevelGap_IsHardExcluded_EvenWhenAuthorized()
    {
        // Arrange
        var below = Guid.NewGuid();
        var qualified = Guid.NewGuid();
        SetMembers(Member(below, "Anna"), Member(qualified, "Bea"));
        SetConflicts(LevelConflict(below, ScheduleValidationType.Error, levelDelta: 1));
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        // Act
        var result = await Find(overrideBlock: true);

        // Assert
        result.Eligible.Single().ClientId.ShouldBe(qualified);
        result.Excluded.Single().ClientId.ShouldBe(below);
        result.Excluded.Single().Reason.ShouldBe(QualificationValidationKeys.InsufficientLevel);
    }

    [Test]
    public async Task NonBindingLevelGap_OnCallStillRanksFirst()
    {
        // Arrange
        var onCallBelow = Guid.NewGuid();
        var qualified = Guid.NewGuid();
        SetMembers(Member(onCallBelow, "Anna"), Member(qualified, "Bea"));
        SetOnLeave(BreakCell(onCallBelow, OnCallAbsenceId));
        SetConflicts(LevelConflict(onCallBelow, ScheduleValidationType.Warning, levelDelta: 1));

        // Act
        var result = await Find();

        // Assert
        result.Eligible[0].ClientId.ShouldBe(onCallBelow);
        result.Eligible[0].LevelShortfall.ShouldBe(1);
    }

    [Test]
    public async Task TwoLevelGapsOfOnePerson_BothCount()
    {
        // Arrange: two mandatory qualifications below their level are two findings of the same kind on the same day.
        var below = Guid.NewGuid();
        SetMembers(Member(below, "Anna"));
        SetConflicts(
            LevelConflict(below, ScheduleValidationType.Warning, levelDelta: 1),
            LevelConflict(below, ScheduleValidationType.Warning, levelDelta: 2));

        // Act
        var result = await Find();

        // Assert
        var candidate = result.Eligible.ShouldHaveSingleItem();
        candidate.LevelShortfall.ShouldBe(3);
        candidate.SoftConflicts.Count.ShouldBe(2);
    }

    private static ScheduleValidationNotificationDto LevelConflict(Guid clientId, ScheduleValidationType type, int levelDelta)
        => new()
        {
            Type = type,
            ClientId = clientId,
            Date = Date,
            Comment = QualificationValidationKeys.InsufficientLevel,
            CommentParams = new Dictionary<string, string>
            {
                [QualificationValidationParamKeys.LevelDelta] = levelDelta.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };

    private static readonly Guid ScenarioToken = Guid.NewGuid();
    private const string HomeShiftCollision = ScheduleValidationKeys.Collision;

    private void SetPool(Client[] inGroup, Client[] borrowable)
    {
        _clientRepo.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(inGroup.Concat(borrowable).ToList());
        _groupClientService.GetRealClientIdsFromGroupAndSubgroups(GroupId, Arg.Any<CancellationToken>())
            .Returns(inGroup.Select(m => m.Id).ToList());
    }

    private Task<ReplacementSearchResult> FindInScenario()
        => _handler.Handle(
            new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, ScenarioToken, false), CancellationToken.None);

    private static EffectiveContractData ContractWithoutTuesday() => new() { WorkOnTuesday = false };

    [Test]
    public async Task TheMembersOfAScenario_AreNotReadForThePool_OnlyTheRealMemberships()
    {
        // Arrange
        var anna = Member(Guid.NewGuid(), "Anna");
        SetMembers(anna);

        // Act
        await Find();

        // Assert
        await _groupClientService.Received(1).GetRealClientIdsFromGroupAndSubgroups(GroupId, Arg.Any<CancellationToken>());
        await _groupClientService.DidNotReceive().GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>());
    }

    [Test]
    public async Task AnExcludedBorrowedPerson_CarriesTheBorrowedMark()
    {
        // Arrange
        var anna = Member(Guid.NewGuid(), "Anna");
        var sibling = Member(Guid.NewGuid(), "Sibling");
        SetPool([anna], [sibling]);
        SetOnLeave(BreakCell(sibling.Id, SickAbsenceId), BreakCell(anna.Id, SickAbsenceId));

        // Act
        var result = await Find();

        // Assert
        result.Excluded.Select(e => (e.ClientId, e.IsBorrowed)).ShouldBe([(anna.Id, false), (sibling.Id, true)], ignoreOrder: true);
    }

    [Test]
    public async Task TheCap_CutsTheBorrowedPeopleByIdAfterTheCallersVisibility()
    {
        // Arrange: five more borrowable people than the cap; the caller cannot see the five with the lowest ids.
        const int hiddenCount = 5;
        var anna = Member(Guid.NewGuid(), "Anna");
        var borrowable = Enumerable.Range(0, RecoveryBuildConstants.CrossGroupPoolCap + hiddenCount)
            .Select(index => Member(Guid.NewGuid(), $"Sibling {index}"))
            .OrderBy(c => c.Id)
            .ToArray();
        var hidden = borrowable.Take(hiddenCount).Select(c => c.Id).ToHashSet();
        SetPool([anna], borrowable);
        SetRestrictedCaller(
            [GroupId], borrowable.Select(c => c.Id).Append(anna.Id).Where(id => !hidden.Contains(id)).ToList());

        // Act
        var result = await Find();

        // Assert: all visible ones fit under the cap; a cut before the visibility would have dropped the last five.
        var borrowedIds = result.Eligible.Where(c => c.IsBorrowed).Select(c => c.ClientId).ToList();
        borrowedIds.ShouldBe(borrowable.Skip(hiddenCount).Select(c => c.Id), ignoreOrder: true);
        result.Eligible.Count(c => !c.IsBorrowed).ShouldBe(1);
    }

    [Test]
    public async Task MoreVisibleBorrowablePeopleThanTheCap_KeepsTheLowestIds()
    {
        // Arrange
        const int surplus = 3;
        var borrowable = Enumerable.Range(0, RecoveryBuildConstants.CrossGroupPoolCap + surplus)
            .Select(index => Member(Guid.NewGuid(), $"Sibling {index}"))
            .ToArray();
        SetPool([], borrowable);

        // Act
        var result = await Find();

        // Assert
        result.Eligible.Select(c => c.ClientId).ShouldBe(
            borrowable.Select(c => c.Id).OrderBy(id => id).Take(RecoveryBuildConstants.CrossGroupPoolCap), ignoreOrder: true);
    }

    [Test]
    public async Task InAScenario_ABorrowedPersonWithACollidingHomeShiftInTheRealPlan_IsExcluded()
    {
        // Arrange: the scenario holds no row of the sibling; the real plan reports the collision with the home shift.
        var anna = Member(Guid.NewGuid(), "Anna");
        var sibling = Member(Guid.NewGuid(), "Sibling");
        SetPool([anna], [sibling]);
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Is<Guid?>(token => token == null), Arg.Any<CancellationToken>())
            .Returns(call => new PreCommitCheckResult(call.Arg<IReadOnlyList<PlannedWorkRow>>()
                .Select(row => Conflict(row.ClientId, ScheduleValidationType.Error, HomeShiftCollision))
                .ToList()));

        // Act
        var result = await FindInScenario();

        // Assert: only the borrowed person is checked on the real plan, and that finding excludes them.
        result.Eligible.Select(c => c.ClientId).ShouldBe([anna.Id]);
        var excluded = result.Excluded.ShouldHaveSingleItem();
        excluded.ClientId.ShouldBe(sibling.Id);
        excluded.Reason.ShouldBe(HomeShiftCollision);
        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == sibling.Id),
            Arg.Is<Guid?>(token => token == null), Arg.Any<CancellationToken>());
        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 2), Arg.Is<Guid?>(token => token == ScenarioToken),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnTheRealPlan_TheCheckRunsOnce()
    {
        // Arrange
        SetPool([Member(Guid.NewGuid(), "Anna")], [Member(Guid.NewGuid(), "Sibling")]);

        // Act
        await Find();

        // Assert
        await _checker.Received(1).CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InAScenario_AWarningOfBothPlans_CountsOnce()
    {
        // Arrange: the scenario and the real plan report the same warning for the borrowed person.
        var sibling = Member(Guid.NewGuid(), "Sibling");
        SetPool([], [sibling]);
        SetConflicts(Conflict(sibling.Id, ScheduleValidationType.Warning, "schedule.error-list.overtime"));

        // Act
        var result = await FindInScenario();

        // Assert
        result.Eligible.ShouldHaveSingleItem().SoftConflicts.Count.ShouldBe(1);
    }

    [Test]
    public async Task InAScenario_ABorrowedPersonAbsentInTheRealPlan_IsExcluded_AndAMemberIsJudgedByTheScenario()
    {
        // Arrange: both have an absence in the real plan only. The scenario owns the day of its member.
        var anna = Member(Guid.NewGuid(), "Anna");
        var sibling = Member(Guid.NewGuid(), "Sibling");
        SetPool([anna], [sibling]);
        ScheduleEntriesStub.Serve(_scheduleEntries, criteria => criteria.AnalyseToken == ScenarioToken, []);
        ScheduleEntriesStub.Serve(
            _scheduleEntries, criteria => criteria.AnalyseToken == null,
            [BreakCell(sibling.Id, SickAbsenceId), BreakCell(anna.Id, SickAbsenceId)]);

        // Act
        var result = await FindInScenario();

        // Assert
        result.Eligible.Select(c => c.ClientId).ShouldBe([anna.Id]);
        result.Excluded.ShouldHaveSingleItem().ClientId.ShouldBe(sibling.Id);
        await _scheduleEntries.Received(1).GetScheduleEntriesAsync(
            Arg.Is<ScheduleEntriesCriteria>(criteria =>
                criteria.AnalyseToken == null && criteria.ClientIds != null && criteria.ClientIds.SequenceEqual(new[] { sibling.Id })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbsentAndUnavailablePeople_NeverReachTheConflictCheck()
    {
        // Arrange
        var absent = Member(Guid.NewGuid(), "Absent");
        var unavailable = Member(Guid.NewGuid(), "Unavailable");
        var free = Member(Guid.NewGuid(), "Free");
        SetMembers(absent, unavailable, free);
        SetOnLeave(BreakCell(absent.Id, SickAbsenceId));
        SetAvailability(Available(unavailable.Id, 10));

        // Act
        var result = await Find();

        // Assert
        result.Excluded.Count.ShouldBe(2);
        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == free.Id),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContractDayOff_IsMarkedAndRankedBehindTheWorkingMember_NotExcluded()
    {
        // Arrange: the slot lies on a Tuesday; Anna's contract has Tuesdays off, Bea works.
        var anna = Member(Guid.NewGuid(), "Anna");
        var bea = Member(Guid.NewGuid(), "Bea");
        SetMembers(anna, bea);
        _contracts[anna.Id] = ContractWithoutTuesday();
        _contracts[bea.Id] = new EffectiveContractData();

        // Act
        var result = await Find();

        // Assert
        result.Excluded.ShouldBeEmpty();
        result.Eligible.Select(c => (c.ClientId, c.IsContractDayOff)).ShouldBe([(bea.Id, false), (anna.Id, true)]);
    }

    [Test]
    public async Task APersonOnCall_IsNotMarkedWithTheContractDayOff()
    {
        // Arrange
        var anna = Member(Guid.NewGuid(), "Anna");
        SetMembers(anna);
        _contracts[anna.Id] = ContractWithoutTuesday();
        SetOnLeave(BreakCell(anna.Id, OnCallAbsenceId));

        // Act
        var result = await Find();

        // Assert
        var candidate = result.Eligible.ShouldHaveSingleItem();
        candidate.IsOnCall.ShouldBeTrue();
        candidate.IsContractDayOff.ShouldBeFalse();
    }

    [Test]
    public async Task APersonWithoutContractData_FollowsTheDefaultWeek()
    {
        // Arrange: no contract data at all; the default week works Monday to Friday.
        var anna = Member(Guid.NewGuid(), "Anna");
        SetMembers(anna);
        var saturday = new DateOnly(2026, 3, 14);

        // Act
        var onTuesday = await Find();
        var onSaturday = await _handler.Handle(
            new FindReplacementQuery(ShiftId, saturday, Start, End, GroupId, null, false), CancellationToken.None);

        // Assert
        onTuesday.Eligible.ShouldHaveSingleItem().IsContractDayOff.ShouldBeFalse();
        onSaturday.Eligible.ShouldHaveSingleItem().IsContractDayOff.ShouldBeTrue();
    }

    [Test]
    public async Task TheRank_PutsTheOwnGroupFirst_ThenOnCall_ThenTheContractDayOff()
    {
        // Arrange: every combination of borrowed and on call, plus a member with the day off by contract.
        var memberOnCall = Member(Guid.NewGuid(), "D member on call");
        var memberFree = Member(Guid.NewGuid(), "C member free");
        var memberDayOff = Member(Guid.NewGuid(), "A member day off");
        var borrowedOnCall = Member(Guid.NewGuid(), "B borrowed on call");
        var borrowedFree = Member(Guid.NewGuid(), "A borrowed free");
        SetPool([memberFree, memberDayOff, memberOnCall], [borrowedFree, borrowedOnCall]);
        _contracts[memberDayOff.Id] = ContractWithoutTuesday();
        SetOnLeave(BreakCell(memberOnCall.Id, OnCallAbsenceId), BreakCell(borrowedOnCall.Id, OnCallAbsenceId));

        // Act
        var result = await Find();

        // Assert
        result.Eligible.Select(c => c.ClientId).ShouldBe(
            [memberOnCall.Id, memberFree.Id, memberDayOff.Id, borrowedOnCall.Id, borrowedFree.Id]);
    }

    [Test]
    public async Task OnlyOnePerson_IsJudged_WhenTheQueryNamesOne()
    {
        // Arrange
        var anna = Member(Guid.NewGuid(), "Anna");
        var sibling = Member(Guid.NewGuid(), "Sibling");
        SetPool([anna], [sibling]);

        // Act
        var result = await _handler.Handle(
            new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null, false, OnlyClientId: sibling.Id),
            CancellationToken.None);

        // Assert
        var candidate = result.Eligible.ShouldHaveSingleItem();
        candidate.ClientId.ShouldBe(sibling.Id);
        candidate.IsBorrowed.ShouldBeTrue();
        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == sibling.Id),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InAScenario_TwoLevelGapsOfABorrowedPerson_BothCount_AndNeitherCountsTwice()
    {
        // Arrange: the scenario and the real plan both report the same two gaps for the borrowed person.
        var sibling = Member(Guid.NewGuid(), "Sibling");
        SetPool([], [sibling]);
        SetConflicts(
            LevelConflict(sibling.Id, ScheduleValidationType.Warning, levelDelta: 1),
            LevelConflict(sibling.Id, ScheduleValidationType.Warning, levelDelta: 2));

        // Act
        var result = await FindInScenario();

        // Assert
        var candidate = result.Eligible.ShouldHaveSingleItem();
        candidate.LevelShortfall.ShouldBe(3);
        candidate.SoftConflicts.Count.ShouldBe(2);
    }

    [Test]
    public async Task OnlyOnePerson_BeyondTheCap_IsNotJudged()
    {
        // Arrange: one borrowable person more than the cap; the query names the one the full list cuts off.
        var borrowable = Enumerable.Range(0, RecoveryBuildConstants.CrossGroupPoolCap + 1)
            .Select(index => Member(Guid.NewGuid(), $"Sibling {index}"))
            .OrderBy(c => c.Id)
            .ToArray();
        SetPool([], borrowable);

        // Act
        var result = await _handler.Handle(
            new FindReplacementQuery(ShiftId, Date, Start, End, GroupId, null, false, OnlyClientId: borrowable[^1].Id),
            CancellationToken.None);

        // Assert
        result.Eligible.ShouldBeEmpty();
        result.Excluded.ShouldBeEmpty();
    }

    [Test]
    public async Task SkillPath_NamesBorrowedPeopleAndContractDaysOff()
    {
        // Arrange
        var anna = Member(Guid.NewGuid(), "Anna");
        var sibling = Member(Guid.NewGuid(), "Sibling");
        var absentSibling = Member(Guid.NewGuid(), "Absent sibling");
        SetPool([anna], [sibling, absentSibling]);
        _contracts[anna.Id] = ContractWithoutTuesday();
        SetOnLeave(BreakCell(absentSibling.Id, SickAbsenceId));
        var skill = SkillDispatchingToRealHandler();

        // Act
        var result = await skill.ExecuteAsync(SkillContext(), SkillParams(GroupId));

        // Assert
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Candidates").EnumerateArray()
            .Select(c => (
                c.GetProperty("ClientId").GetGuid(),
                c.GetProperty("Borrowed").GetBoolean(),
                c.GetProperty("ContractDayOff").GetBoolean()))
            .ShouldBe([(anna.Id, false, true), (sibling.Id, true, false)]);
        data.GetProperty("Excluded").EnumerateArray().Single().GetProperty("Borrowed").GetBoolean().ShouldBeTrue();
        result.Message.ShouldContain("1 of the eligible are borrowed");
        result.Message.ShouldContain("1 have that weekday off by contract");
    }
}
