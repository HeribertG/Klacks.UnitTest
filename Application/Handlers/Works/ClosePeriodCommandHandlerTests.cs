// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClosePeriodCommandHandler: role-based permission flags passed to the lock-level service,
/// and the acknowledgement gate that has to refuse from inside the transaction.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ClosePeriodCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private IPeriodValidationLoader _validationLoader = null!;
    private IComplianceEscalationService _escalationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ClosePeriodCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
        _validationLoader = Substitute.For<IPeriodValidationLoader>();
        _validationLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>());
        _escalationService = Substitute.For<IComplianceEscalationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.ArgAt<Func<Task<int>>>(0)());

        _handler = new ClosePeriodCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            _eventDispatcher,
            _validationLoader,
            _escalationService,
            _unitOfWork,
            Substitute.For<ILogger<ClosePeriodCommandHandler>>());
    }

    [Test]
    public async Task Handle_PassesAuthorisedRoleFlagToCanSeal_WhenUserHasAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, false, true).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, "authorised-user", Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, "authorised-user", Arg.Any<CancellationToken>()).Returns(3);

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(13);
        _lockLevelService.Received(1).CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, false, true);
    }

    [Test]
    public async Task Handle_DispatchesPeriodClosedEvent_WithNullGroupAndZeroSealedDays()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), WorkLockLevel.Closed, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        await _handler.Handle(command, CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is PeriodClosedEvent &&
                ((PeriodClosedEvent)e).GroupId == null &&
                ((PeriodClosedEvent)e).WorkCount == 10 &&
                ((PeriodClosedEvent)e).BreakCount == 3 &&
                ((PeriodClosedEvent)e).SealedDayCount == 0 &&
                ((PeriodClosedEvent)e).SealedBy == "authorised-user"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenPermissionDenied()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(false);

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_SealsInsideATransaction()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        await _handler.Handle(command, CancellationToken.None);

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
    }

    [Test]
    public async Task Handle_UnacknowledgedErrors_RefusesToSealAndReportsTheCount()
    {
        GivenIssues(Issue(ScheduleValidationType.Error), Issue(ScheduleValidationType.Error));

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var ex = await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(command, CancellationToken.None));

        ex.Message.ShouldContain("2 unresolved error");
        await _workRepository.DidNotReceiveWithAnyArgs().SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnacknowledgedErrors_RefusesFromInsideTheTransaction()
    {
        // The acknowledgement check loads the period's findings, and that load reconciles the
        // materialised K12 state before reading it. A refusal raised outside the transaction would
        // therefore persist reconcile writes for a close that never happened.
        var refusalReachedTheTransaction = false;
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(async ci =>
            {
                try
                {
                    return await ci.ArgAt<Func<Task<int>>>(0)();
                }
                catch
                {
                    refusalReachedTheTransaction = true;
                    throw;
                }
            });
        GivenIssues(Issue(ScheduleValidationType.Error));

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(command, CancellationToken.None));

        refusalReachedTheTransaction.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_AcknowledgedErrors_SealsAnyway()
    {
        GivenIssues(Issue(ScheduleValidationType.Error));

        var command = new ClosePeriodCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), AcknowledgeViolations: true);

        await _handler.Handle(command, CancellationToken.None);

        await _workRepository.ReceivedWithAnyArgs(1).SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_OnlyWarnings_SealsWithoutAcknowledgement()
    {
        GivenIssues(Issue(ScheduleValidationType.Warning), Issue(ScheduleValidationType.Info));

        var command = new ClosePeriodCommand(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        await _handler.Handle(command, CancellationToken.None);

        await _workRepository.ReceivedWithAnyArgs(1).SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static PeriodIssueDto Issue(ScheduleValidationType severity) => new()
    {
        Date = new DateOnly(2026, 1, 15),
        ClientId = Guid.NewGuid(),
        ClientName = "Probe",
        Severity = severity,
        Code = "ScheduleValidation",
        MessageKey = "schedule.error-list.rest-violation",
        MessageParams = new Dictionary<string, string>(),
    };

    private void GivenIssues(params PeriodIssueDto[] issues)
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(true);
        _validationLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(issues.ToList());
    }
}
