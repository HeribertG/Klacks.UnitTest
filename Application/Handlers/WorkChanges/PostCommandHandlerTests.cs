// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the WorkChange create handler's replacement guard: which conflicts block the write,
/// and where the supervisor override may lift a block and where it may not. A schedule collision alone
/// never blocks (owner decision 2026-08-22); only a non-overridable structural error (e.g. a missing
/// mandatory qualification) still refuses the write unconditionally.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.WorkChanges;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.WorkChanges;

[TestFixture]
public class PostCommandHandlerTests
{
    private static readonly TimeOnly ChangeStart = new(8, 0);
    private static readonly TimeOnly ChangeEnd = new(16, 0);
    private const decimal WholeShiftHours = 8m;
    private const decimal HalfShiftHours = 4m;
    private const decimal TwoHours = 2m;
    private const decimal MoreThanTheShiftHours = 12m;
    private static readonly TimeOnly NoTime = new(0, 0);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private static readonly TimeOnly Noon = new(12, 0);
    private static readonly TimeOnly TenOClock = new(10, 0);
    private static readonly TimeOnly FourAm = new(4, 0);
    private static readonly TimeOnly TwoAm = new(2, 0);
    private static readonly TimeOnly FiveAm = new(5, 0);

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
    private IReplacementRequestRecorder _recorder = null!;
    private IClientRepository _clientRepository = null!;
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
        _recorder = Substitute.For<IReplacementRequestRecorder>();
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Substitute"));

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
            TestScheduleRowAccess.Over(TestGroupWriteVisibility.AllClientsVisible()),
            new ScheduleMapper(),
            _periodHoursService,
            _notificationService,
            _completionService,
            _resultService,
            _httpContextAccessor,
            _dayLockService,
            _recorder,
            new ParentWorkLockGuard(new WorkLockLevelService()),
            new ReplacementWriteGuard(_clientRepository, _conflictChecker, _overrideAuthorizer),
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
    public async Task Handle_CollisionOnly_LetsTheReplacementThroughWithoutAnyOverride()
    {
        GivenConflicts(CollisionError());

        await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
        await _overrideAuthorizer.DidNotReceiveWithAnyArgs().IsAuthorizedAsync(Arg.Any<bool>());
    }

    [Test]
    public async Task Handle_NonOverridableErrorWithAuthorizedOverride_StillRefusesTheReplacement()
    {
        GivenConflicts(HardBlockingError());
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
    public async Task Handle_EscalatedErrorPlusCollisionWithAuthorizedOverride_LetsTheReplacementThrough()
    {
        GivenConflicts(EscalatedError(), CollisionError());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        await _handler.Handle(ReplacementCommand(overrideBlock: true), CancellationToken.None);

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_MixedHardAndEscalatedConflicts_RefusesEvenWithAuthorizedOverride()
    {
        GivenConflicts(EscalatedError(), HardBlockingError());
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
    public async Task Handle_ReplacementStart_ChecksOnlyTheHoursHandedOverAtTheStartOfTheWork()
    {
        await _handler.Handle(UiCommand(WorkChangeType.ReplacementStart, HalfShiftHours), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date, ChangeStart, Noon))),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReplacementEnd_ChecksOnlyTheHoursHandedOverAtTheEndOfTheWork()
    {
        await _handler.Handle(UiCommand(WorkChangeType.ReplacementEnd, HalfShiftHours), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date, Noon, ChangeEnd))),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReplacementLongerThanTheWork_ChecksTheWholeWorkOnly()
    {
        await _handler.Handle(UiCommand(WorkChangeType.ReplacementStart, MoreThanTheShiftHours), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date, ChangeStart, ChangeEnd))),
            null, Arg.Any<CancellationToken>());
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    public async Task Handle_ReplacementThatHandsNothingOver_SkipsTheCheck(WorkChangeType type)
    {
        await _handler.Handle(UiCommand(type, 0m), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ReplacementWithin_ChecksItsOwnWindow()
    {
        await _handler.Handle(UiCommand(WorkChangeType.ReplacementWithin, TwoHours, TenOClock, Noon), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date, TenOClock, Noon))),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReplacementWithinWithoutChangeTime_StillChecksItsOwnWindow()
    {
        await _handler.Handle(UiCommand(WorkChangeType.ReplacementWithin, 0m, TenOClock, Noon), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date, TenOClock, Noon))),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReplacementWithinWithEqualBounds_SkipsTheCheck()
    {
        await _handler.Handle(UiCommand(WorkChangeType.ReplacementWithin, WholeShiftHours, TenOClock, TenOClock), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_NightShiftReplacementEnd_ChecksTheMorningOfTheNextDay()
    {
        _workRepository.GetNoTracking(_workId).Returns(NightParentWork());

        await _handler.Handle(UiCommand(WorkChangeType.ReplacementEnd, TwoHours), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date.AddDays(1), FourAm, NightEnd))),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NightShiftReplacementStart_ChecksTheEveningUntilMidnight()
    {
        _workRepository.GetNoTracking(_workId).Returns(NightParentWork());

        await _handler.Handle(UiCommand(WorkChangeType.ReplacementStart, TwoHours), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date, NightStart, NoTime))),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NightShiftReplacementWithinAfterMidnight_ChecksTheNextDay()
    {
        _workRepository.GetNoTracking(_workId).Returns(NightParentWork());

        await _handler.Handle(UiCommand(WorkChangeType.ReplacementWithin, TwoHours, TwoAm, FiveAm), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => IsOnly(rows, Row(_date.AddDays(1), TwoAm, FiveAm))),
            null, Arg.Any<CancellationToken>());
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task Handle_ReplacementWithoutAPerson_IsRefusedWithAClearMessage(WorkChangeType type)
    {
        var command = new PostCommand<WorkChangeResource>(new WorkChangeResource
        {
            WorkId = _workId,
            Type = type,
            ReplaceClientId = null,
            ChangeTime = HalfShiftHours,
            StartTime = ChangeStart,
            EndTime = ChangeEnd,
        });

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonRequired);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
        await _recorder.DidNotReceiveWithAnyArgs().RecordManualReplacementAsync(
            Arg.Any<Work>(), Arg.Any<WorkChange>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReplacementWithACustomer_IsRefused()
    {
        _clientRepository.GetTypeAndDisplayNameAsync(_replaceClientId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Customer, "Customer"));

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonNotAnEmployee);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ReplacementWithADeletedOrUnknownPerson_IsRefused()
    {
        _clientRepository.GetTypeAndDisplayNameAsync(_replaceClientId, Arg.Any<CancellationToken>())
            .Returns((ClientTypeAndDisplayName?)null);

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonNotFound);
        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ReplacementWithAnExternalEmployee_IsAccepted()
    {
        _clientRepository.GetTypeAndDisplayNameAsync(_replaceClientId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.ExternEmp, "External"));

        await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ScenarioReplacementWithoutAPerson_IsRefusedToo()
    {
        _workRepository.GetNoTracking(_workId).Returns(ParentWork(Guid.NewGuid()));
        var command = new PostCommand<WorkChangeResource>(new WorkChangeResource
        {
            WorkId = _workId,
            Type = WorkChangeType.ReplacementStart,
            ChangeTime = HalfShiftHours,
        });

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(command, CancellationToken.None));

        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_CorrectionWithoutAPerson_StaysAllowed()
    {
        await _handler.Handle(CorrectionCommand(), CancellationToken.None);

        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_PureCorrection_SkipsTheReplacementGuard()
    {
        GivenConflicts(CollisionError());

        await _handler.Handle(CorrectionCommand(), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ScenarioParentWork_SkipsTheReplacementGuard()
    {
        _workRepository.GetNoTracking(_workId).Returns(ParentWork(Guid.NewGuid()));
        GivenConflicts(CollisionError());

        await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _workChangeRepository.Received(1).Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_ScenarioReplacement_HandsTheChangeWithTheScenarioTokenToTheRequestBook()
    {
        var token = Guid.NewGuid();
        _workRepository.GetNoTracking(_workId).Returns(ParentWork(token));

        await _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None);

        await _recorder.Received(1).RecordManualReplacementAsync(
            Arg.Is<Work>(w => w.Id == _workId),
            Arg.Is<WorkChange>(c => c.AnalyseToken == token && c.ReplaceClientId == _replaceClientId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RefusedReplacement_RecordsNothingInTheRequestBook()
    {
        GivenConflicts(EscalatedError());

        await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None));

        await _recorder.DidNotReceiveWithAnyArgs().RecordManualReplacementAsync(
            Arg.Any<Work>(), Arg.Any<WorkChange>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReplacementWithAnEmployeeWhoseDayIsSealed_IsRefused()
    {
        _dayLockService.EnsureWorkChangeNotLockedAsync(
                Arg.Is<Work>(w => w.Id == _workId), Arg.Is<WorkChange>(c => c.ReplaceClientId == _replaceClientId), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidRequestException("Day 2027-05-10 is sealed and cannot be modified.")));

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(ReplacementCommand(overrideBlock: false), CancellationToken.None));

        await _workChangeRepository.DidNotReceiveWithAnyArgs().Add(Arg.Any<WorkChange>());
    }

    [Test]
    public async Task Handle_PureCorrection_ChecksOnlyTheDayOfTheWorksEmployee()
    {
        await _handler.Handle(CorrectionCommand(), CancellationToken.None);

        await _dayLockService.Received(1).EnsureNotLockedAsync(
            Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _dayLockService.Received(1).EnsureNotLockedAsync(_date, _parentClientId, null, Arg.Any<CancellationToken>());
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

        await Should.ThrowAsync<KeyNotFoundException>(act);
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

    private Work NightParentWork() => new()
    {
        Id = _workId,
        ClientId = _parentClientId,
        ShiftId = _shiftId,
        CurrentDate = _date,
        StartTime = NightStart,
        EndTime = NightEnd,
    };

    private PostCommand<WorkChangeResource> UiCommand(WorkChangeType type, decimal changeTime, TimeOnly? start = null, TimeOnly? end = null) =>
        new(new WorkChangeResource
        {
            WorkId = _workId,
            Type = type,
            ReplaceClientId = _replaceClientId,
            ChangeTime = changeTime,
            StartTime = start ?? NoTime,
            EndTime = end ?? NoTime,
        });

    private PlannedWorkRow Row(DateOnly date, TimeOnly start, TimeOnly end) => new(_replaceClientId, date, start, end, _shiftId);

    private static bool IsOnly(IReadOnlyList<PlannedWorkRow> rows, PlannedWorkRow expected) => rows.Count == 1 && rows[0] == expected;

    private PostCommand<WorkChangeResource> ReplacementCommand(bool overrideBlock) =>
        new(new WorkChangeResource
        {
            WorkId = _workId,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = _replaceClientId,
            StartTime = ChangeStart,
            EndTime = ChangeEnd,
            ChangeTime = WholeShiftHours,
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

    private ScheduleValidationNotificationDto CollisionError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _replaceClientId,
        Date = _date,
        Comment = "schedule.error-list.collision",
    };

    private ScheduleValidationNotificationDto HardBlockingError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _replaceClientId,
        Date = _date,
        Comment = "schedule.error-list.missing-mandatory-qualification",
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
