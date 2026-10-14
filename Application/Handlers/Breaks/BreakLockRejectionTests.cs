// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the refusals of ConfirmBreakCommandHandler and UnconfirmBreakCommandHandler against the real
/// lock-level matrix: an entry without a lock is a state conflict (InvalidRequestException), a missing role is a rights
/// problem (ForbiddenException), a second confirmation is refused, and every refusal is an IUserFacingRejection, so a
/// skill that runs the command reports it as a readable error instead of an execution error.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Breaks;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Breaks;

[TestFixture]
public class BreakLockRejectionTests
{
    private IBreakRepository _breakRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IBreakUserContextProvider _userContextProvider = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private UnconfirmBreakCommandHandler _unconfirmHandler = null!;
    private ConfirmBreakCommandHandler _confirmHandler = null!;

    [SetUp]
    public void Setup()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userContextProvider = Substitute.For<IBreakUserContextProvider>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        var visibility = TestScheduleRowAccess.Over(TestGroupWriteVisibility.AllClientsVisible());
        var userService = Substitute.For<IUserService>();

        _unconfirmHandler = new UnconfirmBreakCommandHandler(
            _breakRepository,
            visibility,
            _unitOfWork,
            new WorkLockLevelService(),
            new ScheduleMapper(),
            _userContextProvider,
            Substitute.For<ILogger<UnconfirmBreakCommandHandler>>());

        _confirmHandler = new ConfirmBreakCommandHandler(
            _breakRepository,
            visibility,
            _unitOfWork,
            new WorkLockLevelService(),
            new ScheduleMapper(),
            _userContextProvider,
            _auditLogRepository,
            userService,
            Substitute.For<ILogger<ConfirmBreakCommandHandler>>());
    }

    private Break GivenBreak(WorkLockLevel level)
    {
        var entry = new Break { Id = Guid.NewGuid(), LockLevel = level };
        _breakRepository.Get(entry.Id).Returns(entry);
        return entry;
    }

    private void GivenUser(bool isAdmin, bool isAuthorised)
    {
        _userContextProvider.GetUserContext().Returns(new BreakUserContext(isAdmin, isAuthorised, "tester"));
    }

    [Test]
    public async Task Unconfirm_ThrowsInvalidRequest_WhenTheEntryCarriesNoLock()
    {
        var entry = GivenBreak(WorkLockLevel.None);
        GivenUser(true, false);

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _unconfirmHandler.Handle(new UnconfirmBreakCommand(entry.Id), CancellationToken.None));

        ex.ShouldBeAssignableTo<IUserFacingRejection>();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Unconfirm_ThrowsForbidden_WhenRegularUserUnsealsApprovedBreak()
    {
        var entry = GivenBreak(WorkLockLevel.Approved);
        GivenUser(false, false);

        var ex = await Should.ThrowAsync<ForbiddenException>(
            () => _unconfirmHandler.Handle(new UnconfirmBreakCommand(entry.Id), CancellationToken.None));

        ex.ShouldBeAssignableTo<IUserFacingRejection>();
        entry.LockLevel.ShouldBe(WorkLockLevel.Approved);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Unconfirm_ThrowsForbidden_WhenSupervisorUnsealsClosedBreak()
    {
        var entry = GivenBreak(WorkLockLevel.Closed);
        GivenUser(false, true);

        await Should.ThrowAsync<ForbiddenException>(
            () => _unconfirmHandler.Handle(new UnconfirmBreakCommand(entry.Id), CancellationToken.None));

        entry.LockLevel.ShouldBe(WorkLockLevel.Closed);
    }

    [Test]
    public async Task Unconfirm_DropsConfirmedBreak_ForRegularUser()
    {
        var entry = GivenBreak(WorkLockLevel.Confirmed);
        GivenUser(false, false);

        var result = await _unconfirmHandler.Handle(new UnconfirmBreakCommand(entry.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        entry.LockLevel.ShouldBe(WorkLockLevel.None);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Confirm_ThrowsInvalidRequest_WhenTheBreakIsAlreadyConfirmed()
    {
        var entry = GivenBreak(WorkLockLevel.Confirmed);
        GivenUser(false, false);

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _confirmHandler.Handle(new ConfirmBreakCommand(entry.Id), CancellationToken.None));

        ex.ShouldBeAssignableTo<IUserFacingRejection>();
        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
