// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Work handlers that delete or move container children read and check the children's lock levels before they stage
/// anything: a refusal leaves the change tracker without a single added, modified or deleted entry, so it cannot be caught
/// further up and leave the container saved without its children. The cascade service is the real one over an in-memory
/// context; the rest of each handler is substituted.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class ContainerChildLocksBeforeStagingTests
{
    private const string UserName = "test-user";
    private static readonly DateOnly Day = new(2027, 4, 12);

    private DataBaseContext _context = null!;
    private IContainerWorkCascadeService _cascade = null!;
    private IWorkRepository _workRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private Work _container = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _cascade = new ContainerWorkCascadeService(_context, NullLogger<ContainerWorkCascadeService>.Instance);
        _workRepository = Substitute.For<IWorkRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<Func<Work, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Work>>(0).ToList());
        WorksTestHelpers.GivenUserIsRegularUser(_httpContextAccessor, UserName);

        _container = new Work { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day };
        _workRepository.Get(_container.Id).Returns(_container);
        _workRepository.GetNoTracking(_container.Id).Returns(_container);
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { _container });
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Delete_WithAnApprovedChildWork_IsRefusedForARegularUser_BeforeAnythingIsStaged()
    {
        await GivenChildWork(WorkLockLevel.Approved);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewDeleteHandler().Handle(
            new DeleteWorkCommand(_container.Id, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedWorkMessage);
        await _workRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        AssertNothingStaged();
    }

    [Test]
    public async Task Delete_WithAConfirmedChildBreak_IsRefusedEvenForAnAuthorisedUser_BeforeAnythingIsStaged()
    {
        WorksTestHelpers.GivenUserIsAuthorised(_httpContextAccessor, UserName);
        await GivenChildBreak(WorkLockLevel.Confirmed);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewDeleteHandler().Handle(
            new DeleteWorkCommand(_container.Id, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe(BreakLockRule.SealedBreakDeleteMessage);
        AssertNothingStaged();
    }

    [Test]
    public async Task BulkDelete_WithAnApprovedChildWork_IsRefusedForARegularUser_BeforeAnythingIsStaged()
    {
        await GivenChildWork(WorkLockLevel.Approved);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewBulkDeleteHandler().Handle(
            new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = [_container.Id] }), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedWorkMessage);
        _workRepository.DidNotReceive().Remove(Arg.Any<Work>());
        AssertNothingStaged();
    }

    [Test]
    public async Task BulkDelete_WithAConfirmedChildBreak_IsRefusedForARegularUser_BeforeAnythingIsStaged()
    {
        await GivenChildBreak(WorkLockLevel.Confirmed);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewBulkDeleteHandler().Handle(
            new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = [_container.Id] }), CancellationToken.None));

        ex.Message.ShouldBe(BreakLockRule.SealedBreakDeleteMessage);
        _workRepository.DidNotReceive().Remove(Arg.Any<Work>());
        AssertNothingStaged();
    }

    [Test]
    public async Task Put_ThatChangesTheDay_IsRefusedWhenAChildWorkIsClosed_BeforeAnythingIsStaged()
    {
        WorksTestHelpers.GivenUserIsAdmin(_httpContextAccessor, UserName);
        await GivenChildWork(WorkLockLevel.Closed);
        var resource = new WorkResource
        {
            Id = _container.Id, ClientId = _container.ClientId, ShiftId = _container.ShiftId, CurrentDate = Day.AddDays(1),
        };

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewPutHandler().Handle(
            new PutCommand<WorkResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.ClosedWorkMessage);
        await _workRepository.DidNotReceive().Put(Arg.Any<Work>());
        AssertNothingStaged();
    }

    [Test]
    public async Task Put_ThatKeepsDayAndClient_DoesNotLookAtTheChildren()
    {
        await GivenChildWork(WorkLockLevel.Closed);
        var resource = new WorkResource
        {
            Id = _container.Id, ClientId = _container.ClientId, ShiftId = _container.ShiftId, CurrentDate = Day,
        };

        var result = await NewPutHandler().Handle(new PutCommand<WorkResource>(resource), CancellationToken.None);

        result.ShouldBeNull();
        await _workRepository.Received(1).Put(Arg.Any<Work>());
    }

    [Test]
    public async Task Reassign_WithAnApprovedChildWork_IsRefusedForARegularUser_BeforeAnythingIsStaged()
    {
        await GivenChildWork(WorkLockLevel.Approved);

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewReassignHandler().Handle(
            new ReassignWorkClientCommand(_container.Id, Guid.NewGuid()), CancellationToken.None));

        ex.Message.ShouldBe(ParentWorkLockGuard.SealedWorkMessage);
        await _workRepository.DidNotReceive().Get(Arg.Any<Guid>());
        AssertNothingStaged();
    }

    private async Task GivenChildWork(WorkLockLevel level)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(), ParentWorkId = _container.Id, ClientId = _container.ClientId, ShiftId = _container.ShiftId,
            CurrentDate = Day, LockLevel = level,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task GivenChildBreak(WorkLockLevel level)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(), ParentWorkId = _container.Id, ClientId = _container.ClientId, CurrentDate = Day, LockLevel = level,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private void AssertNothingStaged()
    {
        _context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ShouldBeEmpty();
    }

    private static ParentWorkLockGuard NewGuard() => new(new WorkLockLevelService());

    private DeleteCommandHandler NewDeleteHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleEntriesService>(),
            Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(), _cascade,
            Substitute.For<ISelectedGroupContextResolver>(), Substitute.For<IDayLockService>(), Substitute.For<IUnitOfWork>(),
            Substitute.For<IOvertimeCascadeService>(), _httpContextAccessor, NewGuard(),
            Substitute.For<ILogger<DeleteCommandHandler>>());

    private BulkDeleteWorksCommandHandler NewBulkDeleteHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            Substitute.For<IPeriodHoursService>(), Substitute.For<IScheduleCompletionService>(),
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IOvertimeCascadeService>(), _cascade,
            Substitute.For<IDayLockService>(), _httpContextAccessor, NewGuard(),
            Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

    private PutCommandHandler NewPutHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            NewPeriodHours(), Substitute.For<IScheduleEntriesService>(), Substitute.For<IScheduleCompletionService>(),
            Substitute.For<IWorkNotificationFacade>(), _cascade, Substitute.For<ISelectedGroupContextResolver>(),
            Substitute.For<IDayLockService>(), Substitute.For<IUnitOfWork>(), Substitute.For<IOvertimeCascadeService>(),
            TestWorkWriteGuard.Over(NewConflictChecker()), _httpContextAccessor, NewGuard(),
            Substitute.For<IClientPlanningPeriodGuard>(), ShiftVisibilityGuards.AllowAll(),
            Substitute.For<ILogger<PutCommandHandler>>());

    private ReassignWorkClientCommandHandler NewReassignHandler()
        => new(
            _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            NewPeriodHours(), Substitute.For<IScheduleEntriesService>(), Substitute.For<IScheduleCompletionService>(),
            Substitute.For<IWorkNotificationFacade>(), _cascade, Substitute.For<ISelectedGroupContextResolver>(),
            Substitute.For<IDayLockService>(), Substitute.For<IUnitOfWork>(), Substitute.For<IOvertimeCascadeService>(),
            TestWorkWriteGuard.Over(NewConflictChecker()), _httpContextAccessor, NewGuard(),
            Substitute.For<IClientPlanningPeriodGuard>(), Substitute.For<ILogger<ReassignWorkClientCommandHandler>>());

    private static IPeriodHoursService NewPeriodHours()
    {
        var periodHours = Substitute.For<IPeriodHoursService>();
        periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));
        return periodHours;
    }

    private static IPreCommitConflictChecker NewConflictChecker()
    {
        var checker = Substitute.For<IPreCommitConflictChecker>();
        checker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        return checker;
    }
}
