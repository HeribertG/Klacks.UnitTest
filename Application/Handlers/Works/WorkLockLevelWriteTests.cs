// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// WP1 (2026-10-08): Work PUT had no lock-level check at all and Work DELETE let an admin remove a Closed Work
/// (dropping its expenses and WorkChanges from a closed period). Both now apply the shared Work lock rule to the
/// stored Work: Closed refuses everyone, Approved needs Admin or Authorised, Confirmed is open, scenario Works are
/// not checked. The day-lock substitute throws a sentinel, so "the guard let it through" is visible as the
/// sentinel instead of the guard's own message.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class WorkLockLevelWriteTests
{
    private const string UserName = "test-user";
    private const string PassedGuardSentinel = "passed-the-lock-guard";
    private static readonly DateOnly Day = new(2027, 4, 12);

    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IDayLockService _dayLockService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _dayLockService = Substitute.For<IDayLockService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<Func<Work, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Work>>(0).ToList());
        _dayLockService.EnsureNotLockedAsync(default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidRequestException(PassedGuardSentinel)));

        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);
    }

    [Test]
    public async Task Put_OfAClosedWork_IsRefusedEvenForAnAdmin()
    {
        var stored = GivenStoredWork(WorkLockLevel.Closed, null);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedWorkMessage);
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
    }

    [Test]
    public async Task Put_OfAnApprovedWork_AsRegularUser_IsRefused()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, UserName);
        var stored = GivenStoredWork(WorkLockLevel.Approved, null);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored)), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedWorkMessage);
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
    }

    [Test]
    public async Task Put_OfAnApprovedWork_AsAuthorised_PassesTheLockGuard()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, UserName);
        var stored = GivenStoredWork(WorkLockLevel.Approved, null);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored)), CancellationToken.None));

        ex.Message.ShouldBe(PassedGuardSentinel);
    }

    [Test]
    public async Task Put_OfAConfirmedWork_AsRegularUser_PassesTheLockGuard()
    {
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, UserName);
        var stored = GivenStoredWork(WorkLockLevel.Confirmed, null);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored)), CancellationToken.None));

        ex.Message.ShouldBe(PassedGuardSentinel);
    }

    [Test]
    public async Task Put_OfAClosedScenarioWork_IsNotCheckedByTheLockGuard()
    {
        var stored = GivenStoredWork(WorkLockLevel.Closed, Guid.NewGuid());

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(NewResource(stored)), CancellationToken.None));

        ex.Message.ShouldBe(PassedGuardSentinel);
    }

    [Test]
    public async Task Delete_OfAClosedWork_IsRefusedEvenForAnAdmin_NothingDeleted()
    {
        var stored = NewWork(WorkLockLevel.Closed, null);
        _workRepository.Get(stored.Id).Returns(stored);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewDeleteHandler().Handle(
            new DeleteWorkCommand(stored.Id, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedWorkMessage);
        await _workRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_OfAConfirmedWork_AsAdmin_PassesTheLockGuard()
    {
        var stored = NewWork(WorkLockLevel.Confirmed, null);
        _workRepository.Get(stored.Id).Returns(stored);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewDeleteHandler().Handle(
            new DeleteWorkCommand(stored.Id, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe(PassedGuardSentinel);
    }

    [Test]
    public async Task BulkDelete_WithOneClosedWork_IsRefusedAsAWholeEvenForAnAdmin_NothingRemoved()
    {
        var open = NewWork(WorkLockLevel.None, null);
        var closed = NewWork(WorkLockLevel.Closed, null);
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { open, closed });

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewBulkDeleteHandler().Handle(
            new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = [open.Id, closed.Id] }),
            CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedWorkMessage);
        _workRepository.DidNotReceive().Remove(Arg.Any<Work>());
    }

    private Work GivenStoredWork(WorkLockLevel level, Guid? analyseToken)
    {
        var stored = NewWork(level, analyseToken);
        _workRepository.GetNoTracking(stored.Id).Returns(stored);
        return stored;
    }

    private static ParentWorkLockGuard NewGuard() => new(new WorkLockLevelService());

    private PutCommandHandler NewPutHandler()
        => new(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
            Substitute.For<IScheduleEntriesService>(), Substitute.For<IScheduleCompletionService>(),
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IContainerWorkCascadeService>(),
            Substitute.For<ISelectedGroupContextResolver>(), _dayLockService, _unitOfWork,
            Substitute.For<IOvertimeCascadeService>(), Substitute.For<IPreCommitConflictChecker>(),
            _httpContextAccessor, NewGuard(), Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
            Substitute.For<IScheduleEntriesService>(), Substitute.For<IScheduleCompletionService>(),
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IContainerWorkCascadeService>(),
            Substitute.For<ISelectedGroupContextResolver>(), _dayLockService, _unitOfWork,
            Substitute.For<IOvertimeCascadeService>(), _httpContextAccessor, NewGuard(),
            Substitute.For<ILogger<DeleteCommandHandler>>());

    private BulkDeleteWorksCommandHandler NewBulkDeleteHandler()
        => new(
            _workRepository, _clientVisibilityGuard, new ScheduleMapper(), Substitute.For<IPeriodHoursService>(),
            Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(),
            Substitute.For<IOvertimeCascadeService>(), Substitute.For<IDayLockService>(), _httpContextAccessor,
            NewGuard(), Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

    private static Work NewWork(WorkLockLevel level, Guid? analyseToken)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            LockLevel = level,
            AnalyseToken = analyseToken
        };

    private static WorkResource NewResource(Work stored)
        => new() { Id = stored.Id, ClientId = stored.ClientId, ShiftId = stored.ShiftId, CurrentDate = Day };
}
