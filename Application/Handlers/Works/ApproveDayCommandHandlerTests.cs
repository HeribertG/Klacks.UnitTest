// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ApproveDayCommandHandler: authorised-role permission resolution against the real
/// lock-level matrix, and the day-level audit record that used to be missing entirely.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ApproveDayCommandHandlerTests
{
    private const string ApproverId = "8f14e45f-ceea-467a-9a3a-1f0f2d9a1c77";

    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IPeriodAuditLogRepository _auditLogRepository = null!;
    private IUserService _userService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ApproveDayCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _auditLogRepository = Substitute.For<IPeriodAuditLogRepository>();
        _userService = Substitute.For<IUserService>();
        _userService.GetDisplayName().Returns("Ada Lovelace");
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.ArgAt<Func<Task<int>>>(0)());

        _handler = new ApproveDayCommandHandler(
            _workRepository,
            _breakRepository,
            new WorkLockLevelService(),
            _httpContextAccessor,
            _auditLogRepository,
            _userService,
            _unitOfWork,
            Substitute.For<ILogger<ApproveDayCommandHandler>>());
    }

    [Test]
    public async Task Handle_ApprovesDay_WhenUserHasAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");
        _workRepository.SealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), WorkLockLevel.Approved, "authorised-user", Arg.Any<CancellationToken>()).Returns(4);
        _breakRepository.SealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), WorkLockLevel.Approved, "authorised-user", Arg.Any<CancellationToken>()).Returns(2);

        var command = new ApproveDayCommand(new DateOnly(2026, 1, 15), Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBe(6);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequest_WhenUserHasNeitherAdminNorAuthorisedRole()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, "regular-user");

        var command = new ApproveDayCommand(new DateOnly(2026, 1, 15), Guid.NewGuid());

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("permission");

        await _workRepository.DidNotReceive().SealByDayAndGroup(Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<WorkLockLevel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditLogRepository.DidNotReceive().AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WritesAuditRecord_WithActorIdNameSnapshotAndTypedReference()
    {
        var groupId = Guid.NewGuid();
        var date = new DateOnly(2026, 1, 15);
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, ApproverId);
        _workRepository.SealByDayAndGroup(date, groupId, WorkLockLevel.Approved, ApproverId, Arg.Any<CancellationToken>()).Returns(4);
        _breakRepository.SealByDayAndGroup(date, groupId, WorkLockLevel.Approved, ApproverId, Arg.Any<CancellationToken>()).Returns(2);

        await _handler.Handle(new ApproveDayCommand(date, groupId), CancellationToken.None);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log =>
                log.Action == PeriodAuditAction.ApproveDay &&
                log.StartDate == date &&
                log.EndDate == date &&
                log.GroupId == groupId &&
                log.AffectedCount == 6 &&
                log.PerformedBy == ApproverId &&
                log.PerformedByName == "Ada Lovelace" &&
                log.ApprovedByUserId == Guid.Parse(ApproverId)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WritesTheAuditRecordInsideTheTransaction()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, ApproverId);

        await _handler.Handle(new ApproveDayCommand(new DateOnly(2026, 1, 15), Guid.NewGuid()), CancellationToken.None);

        // Without the transaction the audit row would never be saved: the two seal calls bypass the
        // change tracker, so nothing else in this handler would ever trigger a SaveChanges.
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
        await _auditLogRepository.Received(1).AddAsync(Arg.Any<PeriodAuditLog>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_LeavesTheTypedReferenceEmpty_WhenTheClaimIsNotAGuid()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, "authorised-user");

        await _handler.Handle(new ApproveDayCommand(new DateOnly(2026, 1, 15), Guid.NewGuid()), CancellationToken.None);

        await _auditLogRepository.Received(1).AddAsync(
            Arg.Is<PeriodAuditLog>(log => log.ApprovedByUserId == null && log.PerformedBy == "authorised-user"),
            Arg.Any<CancellationToken>());
    }
}
