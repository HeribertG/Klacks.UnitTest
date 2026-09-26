// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReopenPeriodByGroupCommandHandler: reason validation, permission check, and audit log writing.
/// </summary>

using Shouldly;
using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces;
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
    private ISealedDayRepository _sealedDayRepository = null!;
    private IUserService _userService = null!;
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
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _userService = Substitute.For<IUserService>();
        _userService.GetDisplayName().Returns("Ada Lovelace");
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<ReopenPeriodByGroupCommandHandler>>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<PeriodReopenResult>>>())
            .Returns(ci => ci.ArgAt<Func<Task<PeriodReopenResult>>>(0)());

        _handler = new ReopenPeriodByGroupCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            _auditLogRepository,
            _sealedDayRepository,
            _userService,
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
    public async Task Handle_PassesAuthorisedRoleFlagToCanUnseal_WhenUserHasAuthorisedRole()
    {
        PeriodClosingTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _lockLevelService.CanUnseal(WorkLockLevel.Closed, false, true).Returns(true);
        _workRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>()).Returns(new PeriodUnsealCounts(0, 0, 5, 0));
        _breakRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>()).Returns(new PeriodUnsealCounts(0, 0, 2, 0));

        var command = new ReopenPeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Customer correction");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.AffectedCount.ShouldBe(7);

        _lockLevelService.Received(1).CanUnseal(WorkLockLevel.Closed, false, true);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.PerformedBy == "authorised-user"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnsealsAndWritesAuditLog_WhenAdminWithReason()
    {
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanUnseal(Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>()).Returns(new PeriodUnsealCounts(0, 0, 7, 0));
        _breakRepository.UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>()).Returns(new PeriodUnsealCounts(0, 0, 1, 0));

        var command = new ReopenPeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Customer correction");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.AffectedCount.ShouldBe(8);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Unseal &&
                log.Reason == "Customer correction" &&
                log.AffectedCount == 8),
            Arg.Any<CancellationToken>());

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<PeriodReopenResult>>>());
    }

    [Test]
    public async Task Handle_ReportsRestoredLevels_SummedOverWorksAndBreaks_ForGroupScope()
    {
        var groupId = Guid.NewGuid();
        PeriodClosingTestHelpers.GivenUserIsAdmin(_httpContextAccessor, "admin-user");
        _lockLevelService.CanUnseal(Arg.Any<WorkLockLevel>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(true);
        _workRepository.UnsealByPeriodAndGroup(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, WorkLockLevel.Closed, Arg.Any<CancellationToken>())
            .Returns(new PeriodUnsealCounts(RestoredConfirmed: 3, RestoredApproved: 2, RestoredNone: 4, WithoutRecordedLevel: 1));
        _breakRepository.UnsealByPeriodAndGroup(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, WorkLockLevel.Closed, Arg.Any<CancellationToken>())
            .Returns(new PeriodUnsealCounts(RestoredConfirmed: 1, RestoredApproved: 0, RestoredNone: 0, WithoutRecordedLevel: 2));
        _sealedDayRepository.SoftDeleteRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(31);

        var command = new ReopenPeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            groupId,
            "Customer correction");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Entries.ShouldBe(new PeriodUnsealCounts(4, 2, 4, 3));
        result.SealedDayCount.ShouldBe(31);
        result.AffectedCount.ShouldBe(13 + 31);
        await _workRepository.DidNotReceive().UnsealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>());
        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.AffectedCount == 44 && log.GroupId == groupId),
            Arg.Any<CancellationToken>());
    }
}
