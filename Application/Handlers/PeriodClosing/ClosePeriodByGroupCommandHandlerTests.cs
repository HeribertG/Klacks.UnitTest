// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClosePeriodByGroupCommandHandler: permission check, group-aware sealing, and audit log writing.
/// </summary>

using Shouldly;
using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Domain.Enums;
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

        result.ShouldBe(13);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal &&
                log.GroupId == null &&
                log.Reason == "Monthly close" &&
                log.AffectedCount == 13 &&
                log.PerformedBy == "admin-user"),
            Arg.Any<CancellationToken>());

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
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

        result.ShouldBe(7);

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal &&
                log.GroupId == groupId &&
                log.AffectedCount == 7),
            Arg.Any<CancellationToken>());
    }

}
