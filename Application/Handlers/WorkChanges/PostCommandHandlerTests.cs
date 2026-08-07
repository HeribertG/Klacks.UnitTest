// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the WorkChange create handler's replacement guard: which conflicts block the write,
/// and where the supervisor override may lift a block and where it may not.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class PostCommandHandlerTests
{
    private static readonly TimeOnly ChangeStart = new(8, 0);
    private static readonly TimeOnly ChangeEnd = new(16, 0);

    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationService _notificationService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IWorkChangeResultService _resultService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IDayLockService _dayLockService = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private PostCommandHandler _handler = null!;

    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _parentClientId = Guid.NewGuid();
    private readonly Guid _replaceClientId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 5, 10);

    [SetUp]
    public void Setup()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _resultService = Substitute.For<IWorkChangeResultService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _dayLockService = Substitute.For<IDayLockService>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();

        _workRepository.GetNoTracking(_workId).Returns(ParentWork(null));
        _periodHoursService.GetPeriodBoundariesAsync(_date)
            .Returns((new DateOnly(2027, 5, 1), new DateOnly(2027, 5, 31)));
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        _handler = new PostCommandHandler(
            _workChangeRepository,
            _workRepository,
            new ScheduleMapper(),
            _periodHoursService,
            _notificationService,
            _completionService,
            _resultService,
            _httpContextAccessor,
            _dayLockService,
            _conflictChecker,
            _overrideAuthorizer,
            Substitute.For<ILogger<PostCommandHandler>>());
    }

    [Test]
    public async Task Handle_EscalatedErrorWithoutOverride_RefusesTheReplacement()
    {
        GivenConflicts(EscalatedError());

        Func<Task> act = async () => await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        (await Should.ThrowAsync<ConflictException>(act)).Message.ShouldContain("blocked");
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_EscalatedErrorWithAuthorizedOverride_LetsTheReplacementThrough()
    {
        GivenConflicts(EscalatedError());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        await _handler.Handle(ReplacementCommand(overrideBlock: true), CancellationToken.None);

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_StructuralErrorWithAuthorizedOverride_StillRefusesTheReplacement()
    {
        GivenConflicts(StructuralError());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        Func<Task> act = async () => await _handler.Handle(ReplacementCommand(overrideBlock: true), CancellationToken.None);

        (await Should.ThrowAsync<ConflictException>(act)).Message.ShouldContain("blocked");
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_OverrideRequestedButNotAuthorized_RefusesTheReplacement()
    {
        GivenConflicts(EscalatedError());
        _overrideAuthorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);

        Func<Task> act = async () => await _handler.Handle(ReplacementCommand(overrideBlock: true), CancellationToken.None);

        (await Should.ThrowAsync<ConflictException>(act)).Message.ShouldContain("blocked");
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_MixedConflicts_RefusesEvenWithAuthorizedOverride()
    {
        GivenConflicts(EscalatedError(), StructuralError());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        Func<Task> act = async () => await _handler.Handle(ReplacementCommand(overrideBlock: true), CancellationToken.None);

        await Should.ThrowAsync<ConflictException>(act);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_WarningsOnly_LetsTheReplacementThroughWithoutAnyOverride()
    {
        GivenConflicts(Warning());

        await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
        await _overrideAuthorizer.DidNotReceiveWithAnyArgs().IsAuthorizedAsync(Arg.Any<bool>());
    }

    [Test]
    public async Task Handle_PureCorrection_SkipsTheReplacementGuard()
    {
        GivenConflicts(StructuralError());

        await _handler.Handle(CorrectionCommand(), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ScenarioParentWork_SkipsTheReplacementGuard()
    {
        _workRepository.GetNoTracking(_workId).Returns(ParentWork(Guid.NewGuid()));
        GivenConflicts(StructuralError());

        await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_MissingParentWork_IsRefusedBeforeAnyGuardRuns()
    {
        var missingId = Guid.NewGuid();
        _workRepository.GetNoTracking(missingId).Returns((Work?)null);

        Func<Task> act = async () => await _handler.Handle(
            new PostCommand<WorkChangeResource>(new WorkChangeResource
            {
                WorkId = missingId,
                Type = WorkChangeType.ReplacementWithin,
                ReplaceClientId = _replaceClientId,
                StartTime = ChangeStart,
                EndTime = ChangeEnd,
            }),
            CancellationToken.None);

        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    private Work ParentWork(Guid? analyseToken) => new()
    {
        Id = _workId,
        ClientId = _parentClientId,
        ShiftId = _shiftId,
        CurrentDate = _date,
        StartTime = ChangeStart,
        EndTime = ChangeEnd,
        AnalyseToken = analyseToken,
    };

    private PostCommand<WorkChangeResource> ReplacementCommand(bool overrideBlock) =>
        new(new WorkChangeResource
        {
            WorkId = _workId,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = _replaceClientId,
            StartTime = ChangeStart,
            EndTime = ChangeEnd,
            OverrideBlock = overrideBlock,
        });

    private PostCommand<WorkChangeResource> CorrectionCommand() =>
        new(new WorkChangeResource
        {
            WorkId = _workId,
            Type = WorkChangeType.CorrectionEnd,
            StartTime = ChangeStart,
            EndTime = ChangeEnd,
        });

    private void GivenConflicts(params ScheduleValidationNotificationDto[] conflicts)
    {
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts));
    }

    private ScheduleValidationNotificationDto StructuralError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _replaceClientId,
        Date = _date,
        Comment = "schedule.error-list.collision",
    };

    private ScheduleValidationNotificationDto EscalatedError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _replaceClientId,
        Date = _date,
        Comment = "schedule.error-list.rest-violation",
        CommentParams = new Dictionary<string, string>
        {
            [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.MinRestHours,
        },
    };

    private ScheduleValidationNotificationDto Warning() => new()
    {
        Type = ScheduleValidationType.Warning,
        ClientId = _replaceClientId,
        Date = _date,
        Comment = "schedule.error-list.rest-violation",
    };
}
