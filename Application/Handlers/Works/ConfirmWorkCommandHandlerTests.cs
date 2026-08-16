// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ConfirmWorkCommandHandler: role-based permission flags passed to the lock-level
/// service, and the audit record that the confirmation level was missing.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Mappers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ConfirmWorkCommandHandlerTests
{
    private IWorkRepository _workRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUserService _userService = null!;
    private ConfirmWorkCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _userService = Substitute.For<IUserService>();
        _userService.GetDisplayName().Returns("Ada Lovelace");

        _handler = new ConfirmWorkCommandHandler(
            _workRepository,
            _unitOfWork,
            _lockLevelService,
            new ScheduleMapper(),
            _httpContextAccessor,
            _cascadeService,
            _auditLogRepository,
            _userService,
            Substitute.For<ILogger<ConfirmWorkCommandHandler>>());
    }

    [Test]
    public async Task Handle_PassesAuthorisedRoleFlagToSeal_WhenUserHasAuthorisedRole()
    {
        var work = new Work { Id = Guid.NewGuid() };
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        var result = await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        _lockLevelService.Received(1).Seal(work, WorkLockLevel.Confirmed, "authorised-user", false, true);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ThrowsKeyNotFound_WhenWorkDoesNotExist()
    {
        var workId = Guid.NewGuid();
        _workRepository.Get(workId).Returns((Work?)null);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        Func<Task> act = async () => await _handler.Handle(new ConfirmWorkCommand(workId), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);

        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WritesAuditRecord_ForTheConfirmedDay()
    {
        var date = new DateOnly(2026, 2, 3);
        var work = new Work { Id = Guid.NewGuid(), CurrentDate = date };
        _workRepository.Get(work.Id).Returns(work);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        await _handler.Handle(new ConfirmWorkCommand(work.Id), CancellationToken.None);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.ConfirmWork &&
                log.StartDate == date &&
                log.EndDate == date &&
                log.AffectedCount == 1 &&
                log.PerformedBy == "authorised-user" &&
                log.PerformedByName == "Ada Lovelace"),
            Arg.Any<CancellationToken>());
    }
}
