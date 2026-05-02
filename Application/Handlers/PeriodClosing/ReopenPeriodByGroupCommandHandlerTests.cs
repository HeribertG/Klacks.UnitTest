// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReopenPeriodByGroupCommandHandler: reason validation, permission check, and audit log writing.
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
public class ReopenPeriodByGroupCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<ReopenPeriodByGroupCommandHandler> _logger = null!;
    private ReopenPeriodByGroupCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<ReopenPeriodByGroupCommandHandler>>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.ArgAt<Func<Task<int>>>(0)());

        _handler = new ReopenPeriodByGroupCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            _auditLogRepository,
            _unitOfWork,
            _logger);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenReasonMissing()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanUnseal(Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);

        var command = new ReopenPeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "   ");

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("reason");
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenUserIsNotAdmin()
    {
        PeriodClosingTestHelpers.GivenUserIsNotAdmin(_httpContextAccessor);
        _lockLevelService.CanUnseal(Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(false);

        var command = new ReopenPeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Customer correction");

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");
    }

    [Test]
    public async Task Handle_UnsealsAndWritesAuditLog_WhenAdminWithReason()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanUnseal(Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>()).Returns(7);
        _breakRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>()).Returns(1);

        var command = new ReopenPeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Customer correction");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(8);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Unseal &&
                log.Reason == "Customer correction" &&
                log.AffectedCount == 8),
            Arg.Any<CancellationToken>());

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
    }

}
