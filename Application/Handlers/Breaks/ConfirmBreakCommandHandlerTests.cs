// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ConfirmBreakCommandHandler. This handler is the one of the three confirmation
/// paths that reads its identity from IBreakUserContextProvider instead of the HTTP context, so the
/// audit record is pinned separately here.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Handlers.Breaks;
using Klacks.Api.Application.Mappers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Breaks;

[TestFixture]
public class ConfirmBreakCommandHandlerTests
{
    private const string ConfirmerId = "3b0d1f52-2c8a-4f1e-9a44-6c1b7e0f9d31";

    private IBreakRepository _breakRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkLockLevelService _lockLevelService = null!;
    private IBreakUserContextProvider _userContextProvider = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUserService _userService = null!;
    private ConfirmBreakCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _lockLevelService = Substitute.For<IWorkLockLevelService>();
        _userContextProvider = Substitute.For<IBreakUserContextProvider>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _userService = Substitute.For<IUserService>();

        _userContextProvider.GetUserContext().Returns(new BreakUserContext(false, true, ConfirmerId));
        _userService.GetDisplayName().Returns("Ada Lovelace");

        _handler = new ConfirmBreakCommandHandler(
            _breakRepository,
            _unitOfWork,
            _lockLevelService,
            new ScheduleMapper(),
            _userContextProvider,
            _auditLogRepository,
            _userService,
            Substitute.For<ILogger<ConfirmBreakCommandHandler>>());
    }

    [Test]
    public async Task Handle_ThrowsKeyNotFound_WhenBreakDoesNotExist()
    {
        var breakId = Guid.NewGuid();
        _breakRepository.Get(breakId).Returns((Break?)null);

        Func<Task> act = async () => await _handler.Handle(new ConfirmBreakCommand(breakId), CancellationToken.None);

        await Should.ThrowAsync<KeyNotFoundException>(act);

        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_WritesAuditRecord_FromTheBreakUserContext()
    {
        var date = new DateOnly(2026, 3, 9);
        var entry = new Break { Id = Guid.NewGuid(), CurrentDate = date };
        _breakRepository.Get(entry.Id).Returns(entry);

        var result = await _handler.Handle(new ConfirmBreakCommand(entry.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        _lockLevelService.Received(1).Seal(entry, WorkLockLevel.Confirmed, ConfirmerId, false, true);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.ConfirmBreak &&
                log.StartDate == date &&
                log.EndDate == date &&
                log.AffectedCount == 1 &&
                log.PerformedBy == ConfirmerId &&
                log.PerformedByName == "Ada Lovelace" &&
                log.ApprovedByUserId == Guid.Parse(ConfirmerId)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_SavesAfterStagingTheAuditRecord()
    {
        var entry = new Break { Id = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 3, 9) };
        _breakRepository.Get(entry.Id).Returns(entry);

        await _handler.Handle(new ConfirmBreakCommand(entry.Id), CancellationToken.None);

        // AddAsync only stages the row in the change tracker; without the following save it would
        // never reach the database.
        Received.InOrder(() =>
        {
            _auditLogRepository.AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
        });
    }
}
