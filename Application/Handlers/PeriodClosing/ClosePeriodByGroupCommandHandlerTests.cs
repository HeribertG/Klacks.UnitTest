// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClosePeriodByGroupCommandHandler: permission check, group-aware sealing, and audit log writing.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Shouldly;
using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

[TestFixture]
public class ClosePeriodByGroupCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private IPeriodValidationLoader _validationLoader = null!;
    private IComplianceEscalationService _escalationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<ClosePeriodByGroupCommandHandler> _logger = null!;
    private ClosePeriodByGroupCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
        _validationLoader = Substitute.For<IPeriodValidationLoader>();
        _validationLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>());
        _escalationService = Substitute.For<IComplianceEscalationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<ClosePeriodByGroupCommandHandler>>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.ArgAt<Func<Task<int>>>(0)());

        _handler = new ClosePeriodByGroupCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            _auditLogRepository,
            _sealedDayRepository,
            _eventDispatcher,
            _validationLoader,
            _escalationService,
            _unitOfWork,
            _logger);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenUserIsNotAdmin()
    {
        PeriodClosingTestHelpers.GivenUserIsNotAdmin(_httpContextAccessor);
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(false);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CallsSealByPeriod_WhenGroupIdIsNull_AndAdmin()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        var result = await _handler.Handle(command, CancellationToken.None);

        // 10 work + 3 break + 31 SealedDay rows (Jan 2026 has 31 days)
        result.ShouldBe(44);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal &&
                log.GroupId == null &&
                log.Reason == "Monthly close" &&
                log.AffectedCount == 44 &&
                log.PerformedBy == "admin-user"),
            Arg.Any<CancellationToken>());

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
    }

    [Test]
    public async Task Handle_PassesAuthorisedRoleFlagToCanSeal_WhenUserHasAuthorisedRole()
    {
        PeriodClosingTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, false, true).Returns(true);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        var result = await _handler.Handle(command, CancellationToken.None);

        // 0 work + 0 break + 31 SealedDay rows (Jan 2026 has 31 days)
        result.ShouldBe(31);

        _lockLevelService.Received(1).CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, false, true);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.PerformedBy == "authorised-user"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CallsSealByPeriodAndGroup_WhenGroupIdIsProvided()
    {
        var groupId = Guid.NewGuid();
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriodAndGroup(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(5);
        _breakRepository.SealByPeriodAndGroup(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(2);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            groupId,
            null);

        var result = await _handler.Handle(command, CancellationToken.None);

        // 5 work + 2 break + 31 SealedDay rows (Jan 2026 has 31 days)
        result.ShouldBe(38);

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal &&
                log.GroupId == groupId &&
                log.AffectedCount == 38),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DispatchesPeriodClosedEvent_AfterCommit_WithSealCounts()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        await _handler.Handle(command, CancellationToken.None);

        // 10 work + 3 break + 31 sealed days (Jan 2026 has 31 days)
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is PeriodClosedEvent &&
                ((PeriodClosedEvent)e).StartDate == new DateOnly(2026, 1, 1) &&
                ((PeriodClosedEvent)e).EndDate == new DateOnly(2026, 1, 31) &&
                ((PeriodClosedEvent)e).GroupId == null &&
                ((PeriodClosedEvent)e).WorkCount == 10 &&
                ((PeriodClosedEvent)e).BreakCount == 3 &&
                ((PeriodClosedEvent)e).SealedDayCount == 31 &&
                ((PeriodClosedEvent)e).SealedBy == "admin-user"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ReturnsSealResult_WhenPostCommitHookThrows()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);
        _eventDispatcher
            .DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hook failure")));

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        var result = await _handler.Handle(command, CancellationToken.None);

        // A failing post-commit hook must never affect the already-committed seal or the returned result
        result.ShouldBe(44);
        await _eventDispatcher.Received(1).DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DoesNotAccumulateCounts_WhenTransactionIsRetried()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);

        // Simulate EF Core's retry-on-failure execution strategy re-running the operation after a transient error
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(async ci =>
            {
                var operation = ci.ArgAt<Func<Task<int>>>(0);
                await operation();
                return await operation();
            });

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        var result = await _handler.Handle(command, CancellationToken.None);

        // Counts must reflect a single attempt (10 + 3 + 31 = 44), not accumulate across the retry (would be 75)
        result.ShouldBe(44);
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e => e is PeriodClosedEvent && ((PeriodClosedEvent)e).SealedDayCount == 31),
            Arg.Any<CancellationToken>());
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
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(true);
        _validationLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(issues.ToList());
    }

    [Test]
    public async Task Handle_UnacknowledgedErrors_RefusesToSealAndReportsTheCount()
    {
        GivenIssues(Issue(ScheduleValidationType.Error), Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close");

        var ex = await Should.ThrowAsync<PeriodValidationConflictException>(
            () => _handler.Handle(command, CancellationToken.None));

        ex.Message.ShouldContain("2 unresolved error");
        ex.CurrentErrorCount.ShouldBe(2);
        ex.ShouldBeAssignableTo<ConflictException>();
        await _workRepository.DidNotReceiveWithAnyArgs().SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AcknowledgedErrors_SealsAnyway()
    {
        GivenIssues(Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close", AcknowledgeViolations: true);

        await _handler.Handle(command, CancellationToken.None);

        await _workRepository.ReceivedWithAnyArgs(1).SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AcknowledgedWithoutCount_SealsWithoutRevalidating()
    {
        GivenIssues(Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close", AcknowledgeViolations: true);

        await _handler.Handle(command, CancellationToken.None);

        await _validationLoader.DidNotReceiveWithAnyArgs().LoadAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
            Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CountWithoutTheAcknowledgeFlag_DoesNotAcknowledgeAnything()
    {
        GivenIssues(Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close",
            AcknowledgedErrorCount: 5);

        var ex = await Should.ThrowAsync<PeriodValidationConflictException>(
            () => _handler.Handle(command, CancellationToken.None));

        ex.CurrentErrorCount.ShouldBe(1);
        await _workRepository.DidNotReceiveWithAnyArgs().SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AcknowledgedCountStillMatches_Seals()
    {
        GivenIssues(Issue(ScheduleValidationType.Error), Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close",
            AcknowledgeViolations: true, AcknowledgedErrorCount: 2);

        await _handler.Handle(command, CancellationToken.None);

        await _workRepository.ReceivedWithAnyArgs(1).SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_FewerErrorsThanAcknowledged_Seals()
    {
        GivenIssues(Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close",
            AcknowledgeViolations: true, AcknowledgedErrorCount: 2);

        await _handler.Handle(command, CancellationToken.None);

        await _workRepository.ReceivedWithAnyArgs(1).SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_MoreErrorsThanAcknowledged_RefusesAgainWithTheCurrentCount()
    {
        GivenIssues(
            Issue(ScheduleValidationType.Error),
            Issue(ScheduleValidationType.Error),
            Issue(ScheduleValidationType.Error));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close",
            AcknowledgeViolations: true, AcknowledgedErrorCount: 2);

        var ex = await Should.ThrowAsync<PeriodValidationConflictException>(
            () => _handler.Handle(command, CancellationToken.None));

        ex.CurrentErrorCount.ShouldBe(3);
        await _workRepository.DidNotReceiveWithAnyArgs().SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditLogRepository.DidNotReceiveWithAnyArgs().AddAsync(
            Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_OnlyWarnings_SealsWithoutAcknowledgement()
    {
        GivenIssues(Issue(ScheduleValidationType.Warning), Issue(ScheduleValidationType.Info));
        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), null, "Monthly close");

        await _handler.Handle(command, CancellationToken.None);

        await _workRepository.ReceivedWithAnyArgs(1).SealByPeriod(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
