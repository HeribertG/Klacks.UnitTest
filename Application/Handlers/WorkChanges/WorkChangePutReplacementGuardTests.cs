// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The WorkChanges PutCommandHandler with the real replacement write guard: an update names a valid person for a
/// replacement, a hidden replacement person is answered as before (nothing about the person is checked), an update
/// that keeps the person and the time is not blocked again, and a replacement can still be cut back to a correction.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.Application.Handlers.Works;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class WorkChangePutReplacementGuardTests
{
    private const string UserName = "test-user";
    private const decimal HalfShiftHours = 4m;
    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);
    private static readonly TimeOnly WorkStart = new(8, 0);
    private static readonly TimeOnly WorkEnd = new(16, 0);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Noon = new(12, 0);

    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IClientRepository _clientRepository = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private Work _work = null!;
    private Guid _personId;

    [SetUp]
    public void SetUp()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientRepository = Substitute.For<IClientRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _personId = Guid.NewGuid();
        _work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            StartTime = WorkStart,
            EndTime = WorkEnd,
        };

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clientRepository.GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Substitute"));
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _workChangeRepository.Put(Arg.Any<WorkChange>()).Returns(call => call.ArgAt<WorkChange>(0));
        _workRepository.GetNoTracking(_work.Id).Returns(_work);
        _workRepository.Get(_work.Id).Returns(_work);
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));

        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task Put_ReplacementWithoutAPerson_IsRefused_AndWritesNothing(WorkChangeType type)
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        var resource = Resource(stored.Id, type, null);

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => NewHandler().Handle(new PutCommand<WorkChangeResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonRequired);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Put_ReplacementWithACustomer_IsRefused()
    {
        var customer = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(customer, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Customer, "Customer"));
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            new PutCommand<WorkChangeResource>(Resource(stored.Id, WorkChangeType.ReplacementWithin, customer)),
            CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonNotAnEmployee);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Put_WithAHiddenReplacementPerson_IsAnsweredAsBefore_WithoutLookingAtThePerson()
    {
        var hidden = Guid.NewGuid();
        _clientVisibilityGuard.AreAllVisibleAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hidden)), Arg.Any<CancellationToken>())
            .Returns(false);
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);

        var result = await NewHandler().Handle(
            new PutCommand<WorkChangeResource>(Resource(stored.Id, WorkChangeType.ReplacementWithin, hidden)),
            CancellationToken.None);

        result.ShouldBeNull();
        await _clientRepository.DidNotReceiveWithAnyArgs().GetTypeAndDisplayNameAsync(default, default);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Put_ChangingThePerson_IsCheckedForConflicts_LikeACreate()
    {
        var newPerson = Guid.NewGuid();
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([EscalatedError(newPerson)]));

        var exception = await Should.ThrowAsync<ConflictException>(() => NewHandler().Handle(
            new PutCommand<WorkChangeResource>(Resource(stored.Id, WorkChangeType.ReplacementWithin, newPerson)),
            CancellationToken.None));

        exception.Message.ShouldContain("blocked");
        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == newPerson),
            null, Arg.Any<CancellationToken>());
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Put_WideningTheWindow_IsCheckedForTheAddedTimeOnly_AndRefusedOnAConflict()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([EscalatedError(_personId)]));
        var resource = Resource(stored.Id, WorkChangeType.ReplacementWithin, _personId);
        resource.EndTime = WorkEnd;

        await Should.ThrowAsync<ConflictException>(
            () => NewHandler().Handle(new PutCommand<WorkChangeResource>(resource), CancellationToken.None));

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].StartTime == Noon && rows[0].EndTime == WorkEnd),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 0),
            null, Arg.Any<CancellationToken>());
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Put(default!);
    }

    [Test]
    public async Task Put_ChangingOnlyTheDescription_IsNotBlockedByAConflictOfTheUnchangedReplacement()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([EscalatedError(_personId)]));
        var resource = Resource(stored.Id, WorkChangeType.ReplacementWithin, _personId);
        resource.Description = "only the text changed";

        var result = await NewHandler().Handle(new PutCommand<WorkChangeResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Put(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Put_CuttingAReplacementBackToACorrection_StaysAllowed()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        var resource = Resource(stored.Id, WorkChangeType.CorrectionEnd, null);

        var result = await NewHandler().Handle(new PutCommand<WorkChangeResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _workChangeRepository.Received(1).Put(Arg.Any<WorkChange>());
    }

    private WorkChange Stored(WorkChangeType type, Guid? personId)
    {
        var stored = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = _work.Id,
            Type = type,
            ReplaceClientId = personId,
            StartTime = Ten,
            EndTime = Noon,
            ChangeTime = HalfShiftHours,
        };
        _workChangeRepository.GetNoTracking(stored.Id).Returns(stored);
        return stored;
    }

    private WorkChangeResource Resource(Guid id, WorkChangeType type, Guid? personId) => new()
    {
        Id = id,
        WorkId = _work.Id,
        Type = type,
        ReplaceClientId = personId,
        StartTime = Ten,
        EndTime = Noon,
        ChangeTime = HalfShiftHours,
    };

    private PutCommandHandler NewHandler()
        => new(
            _workChangeRepository, _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            _periodHoursService, Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkChangeResultService>(),
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IDayLockService>(),
            Substitute.For<IReplacementRequestRecorder>(), _httpContextAccessor,
            new ParentWorkLockGuard(new WorkLockLevelService()),
            new ReplacementWriteGuard(_clientRepository, _conflictChecker, _overrideAuthorizer),
            Substitute.For<ILogger<PutCommandHandler>>());

    private static ScheduleValidationNotificationDto EscalatedError(Guid clientId) => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = clientId,
        Date = Day,
        Comment = "schedule.error-list.rest-violation",
        CommentParams = new Dictionary<string, string>
        {
            [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.MinRestHours,
        },
    };
}
