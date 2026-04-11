// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClosePeriodByGroupCommandHandler: permission check, group-aware sealing, and audit log writing.
/// </summary>

using FluentAssertions;
using Klacks.Api.Application.Commands.PeriodClosing;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

[TestFixture]
public class ClosePeriodByGroupCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
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
        _logger = Substitute.For<ILogger<ClosePeriodByGroupCommandHandler>>();

        _handler = new ClosePeriodByGroupCommandHandler(
            _workRepository,
            _breakRepository,
            _lockLevelService,
            _httpContextAccessor,
            _auditLogRepository,
            _logger);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenUserIsNotAdmin()
    {
        SetupNonAdminUser();
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), false, Arg.Any<bool>()).Returns(false);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*permission*");

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CallsSealByPeriod_WhenGroupIdIsNull_AndAdmin()
    {
        SetupAdminUser("admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), true, Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _breakRepository.SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            null,
            "Monthly close");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().Be(13);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal &&
                log.GroupId == null &&
                log.Reason == "Monthly close" &&
                log.AffectedCount == 13 &&
                log.PerformedBy == "admin-user"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CallsSealByPeriodAndGroup_WhenGroupIdIsProvided()
    {
        var groupId = Guid.NewGuid();
        SetupAdminUser("admin-user");
        _lockLevelService.CanSeal(Arg.Any<WorkLockLevel>(), Arg.Any<WorkLockLevel>(), true, Arg.Any<bool>()).Returns(true);
        _workRepository.SealByPeriodAndGroup(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(5);
        _breakRepository.SealByPeriodAndGroup(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), groupId, Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(2);

        var command = new ClosePeriodByGroupCommand(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            groupId,
            null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().Be(7);

        await _workRepository.DidNotReceive().SealByPeriod(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.Seal &&
                log.GroupId == groupId &&
                log.AffectedCount == 7),
            Arg.Any<CancellationToken>());
    }

    private void SetupAdminUser(string userName)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, Roles.Admin),
                new Claim(ClaimNames.IsAuthorised, "true"),
                new Claim(ClaimTypes.NameIdentifier, userName)
            ], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }

    private void SetupNonAdminUser()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.User)], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }
}
