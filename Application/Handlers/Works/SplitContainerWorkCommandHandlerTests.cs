// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container split is one command in one transaction: every check runs before anything is staged, the split is staged
/// once, and a failing recalculation rolls it back (the transaction throws) so no half-split container is left behind.
/// After the transaction the first part is completed against the container before the split and the second part as new,
/// each in its own pay period (IScheduleCompletionService.CompleteWorkWriteAsync).
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class SplitContainerWorkCommandHandlerTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);

    private IWorkRepository _workRepository = null!;
    private IShiftRepository _shiftRepository = null!;
    private IContainerLockRepository _lockRepository = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private IContainerWorkChildrenReadRepository _childrenReadRepository = null!;
    private IContainerSplitWriter _splitWriter = null!;
    private IContainerWorkChildrenManager _childrenManager = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SplitContainerWorkCommandHandler _handler = null!;
    private IPeriodHoursService _periodHours = null!;
    private IScheduleCompletionService _completion = null!;
    private IHttpContextAccessor _http = null!;
    private IClientPlanningPeriodGuard _planningGuard = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private Work _container = null!;
    private Work _early = null!;
    private Work _late = null!;
    private readonly Guid _target = Guid.NewGuid();

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [SetUp]
    public void SetUp()
    {
        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = T(8), EndTime = T(16),
        };
        _early = new Work { Id = Guid.NewGuid(), ClientId = _container.ClientId, ParentWorkId = _container.Id, StartTime = T(8), EndTime = T(11), CurrentDate = Day };
        _late = new Work { Id = Guid.NewGuid(), ClientId = _container.ClientId, ParentWorkId = _container.Id, StartTime = T(13), EndTime = T(15), CurrentDate = Day };

        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.GetNoTracking(_container.Id).Returns(_container);
        _workRepository.Get(_container.Id).Returns(_container);
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(_container.ShiftId).Returns(new Shift { Id = _container.ShiftId, ShiftType = ShiftType.IsContainer });
        _shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift>());
        _lockRepository = Substitute.For<IContainerLockRepository>();
        _lockRepository.IsHeldBy(Arg.Any<string>(), _container.Id, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _cascadeService.GetLiveChildrenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerChildren([_early, _late], []));
        _childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        _childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkChange>());
        _splitWriter = Substitute.For<IContainerSplitWriter>();
        _splitWriter.StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Work { Id = Guid.NewGuid(), ClientId = _target, CurrentDate = Day, StartTime = T(12), EndTime = T(16) });
        _childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        _childrenManager.RecalculateForNewOwnerAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>()).Returns(true);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<(Work, Work)>>>())
            .Returns(ci => ci.Arg<Func<Task<(Work, Work)>>>()());
        _periodHours = Substitute.For<IPeriodHoursService>();
        _periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));
        _completion = Substitute.For<IScheduleCompletionService>();
        _http = Substitute.For<IHttpContextAccessor>();
        _http.HttpContext.Returns(new DefaultHttpContext());
        _planningGuard = Substitute.For<IClientPlanningPeriodGuard>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();

        _handler = CreateHandler(TestGroupWriteVisibility.AllClientsVisible());
    }

    private SplitContainerWorkCommandHandler CreateHandler(IClientVisibilityGuard clientVisibility) =>
        new(
            _workRepository, _shiftRepository, TestScheduleRowAccess.Over(clientVisibility),
            _lockRepository, Substitute.For<IUserService>(), _cascadeService, _childrenReadRepository, new ParentWorkLockGuard(new WorkLockLevelService()),
            _http, Substitute.For<IDayLockService>(), _planningGuard, Substitute.For<IWorkWriteGuard>(),
            _splitWriter, _childrenManager, _unitOfWork, _periodHours, _completion,
            _notificationFacade, new ScheduleMapper(),
            Substitute.For<ILogger<SplitContainerWorkCommandHandler>>());

    private Task<SplitContainerWorkResponse> Split(int hour) =>
        _handler.Handle(new SplitContainerWorkCommand(_container.Id, T(hour), _target), CancellationToken.None);

    [Test]
    public async Task Split_StagesOnePlanWithTheLaterTask_InsideOneTransaction()
    {
        var response = await Split(12);

        await _splitWriter.Received(1).StageAsync(
            Arg.Is<ContainerSplitPlan>(p => p.ContainerId == _container.Id && p.SplitTime == T(12) && p.TargetClientId == _target
                && p.SecondPartDate == Day && p.MovedChildIds.SetEquals(new[] { _late.Id })),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<(Work, Work)>>>());
        response.SecondPart.ShouldNotBeNull();
    }

    [Test]
    public async Task ASplitWithinOnePeriod_CompletesTheFirstPartAgainstTheContainer_ThenTheSecondPartAsNew_AndTheHandlerSendsNoPeriodHoursPush()
    {
        await Split(12);

        Received.InOrder(() =>
        {
            _completion.CompleteWorkWriteAsync(_container, _container, Day, Day);
            _completion.CompleteWorkWriteAsync(
                Arg.Is<Work>(w => w.ClientId == _target && w.CurrentDate == Day), Arg.Is<Work?>(previous => previous == null), Day, Day);
        });
        _completion.ReceivedCalls().Count().ShouldBe(2);
        await _notificationFacade.DidNotReceiveWithAnyArgs().NotifyPeriodHoursUpdatedAsync(
            default, default, default, default!, default!, default);
    }

    [Test]
    public async Task ASealedContainer_IsRefusedBeforeAnythingIsStaged()
    {
        _container.LockLevel = WorkLockLevel.Confirmed;

        await Should.ThrowAsync<ContainerSplitSealedRefusedException>(() => Split(12));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AMissingOrDeletedTarget_IsRefusedBeforeAnythingIsStaged()
    {
        var token = new CancellationTokenSource().Token;
        _planningGuard.EnsurePlannablePersonsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == _target), token)
            .Returns(_ => throw new KeyNotFoundException("missing target"));

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new SplitContainerWorkCommand(_container.Id, T(12), _target), token));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ANightSplitIntoANewPeriod_CompletesEachPartInItsOwnPeriod_ForTheSamePerson()
    {
        var nextDay = Day.AddDays(1);
        var firstPeriod = (Day.AddDays(-9), Day);
        var secondPeriod = (nextDay, nextDay.AddDays(29));
        _container.StartTime = T(20);
        _container.EndTime = T(4);
        _early.StartTime = T(20);
        _early.EndTime = T(23);
        _late.StartTime = T(1);
        _late.EndTime = T(3);
        _periodHours.GetPeriodBoundariesAsync(Day).Returns(firstPeriod);
        _periodHours.GetPeriodBoundariesAsync(nextDay).Returns(secondPeriod);
        _splitWriter.StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Work { Id = Guid.NewGuid(), ClientId = _container.ClientId, CurrentDate = nextDay, StartTime = T(0), EndTime = T(4) });

        await _handler.Handle(new SplitContainerWorkCommand(_container.Id, T(0), _container.ClientId), CancellationToken.None);

        Received.InOrder(() =>
        {
            _completion.CompleteWorkWriteAsync(_container, _container, firstPeriod.Item1, firstPeriod.Item2);
            _completion.CompleteWorkWriteAsync(
                Arg.Is<Work>(w => w.ClientId == _container.ClientId && w.CurrentDate == nextDay),
                Arg.Is<Work?>(previous => previous == null), secondPeriod.Item1, secondPeriod.Item2);
        });
        _completion.ReceivedCalls().Count().ShouldBe(2);
    }

    [Test]
    public async Task ANightSplitIntoANewPeriod_ForAnotherPerson_CompletesEachPersonInItsOwnPeriod()
    {
        var nextDay = Day.AddDays(1);
        var firstPeriod = (Day.AddDays(-9), Day);
        var secondPeriod = (nextDay, nextDay.AddDays(29));
        _container.StartTime = T(20);
        _container.EndTime = T(4);
        _early.StartTime = T(20);
        _early.EndTime = T(23);
        _late.StartTime = T(1);
        _late.EndTime = T(3);
        _periodHours.GetPeriodBoundariesAsync(Day).Returns(firstPeriod);
        _periodHours.GetPeriodBoundariesAsync(nextDay).Returns(secondPeriod);
        _splitWriter.StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Work { Id = Guid.NewGuid(), ClientId = _target, CurrentDate = nextDay, StartTime = T(0), EndTime = T(4) });

        await _handler.Handle(new SplitContainerWorkCommand(_container.Id, T(0), _target), CancellationToken.None);

        Received.InOrder(() =>
        {
            _completion.CompleteWorkWriteAsync(_container, _container, firstPeriod.Item1, firstPeriod.Item2);
            _completion.CompleteWorkWriteAsync(
                Arg.Is<Work>(w => w.ClientId == _target && w.CurrentDate == nextDay),
                Arg.Is<Work?>(previous => previous == null), secondPeriod.Item1, secondPeriod.Item2);
        });
        _completion.ReceivedCalls().Count().ShouldBe(2);
    }

    [Test]
    public async Task FailingRecalculation_ThrowsInsideTheTransaction_AndNothingIsCompletedAfterTheStructuralSave()
    {
        _childrenManager.RecalculateForNewOwnerAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<InvalidRequestException>(() => Split(12));

        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task SplitThroughATask_IsRefusedBeforeAnythingIsStaged()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => Split(14));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SplitThroughTheTravelTimeBeforeTheLaterTask_IsRefused()
    {
        _childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkChange>
            {
                new() { Id = Guid.NewGuid(), WorkId = _late.Id, Type = WorkChangeType.CorrectionStart, StartTime = T(11, 30), EndTime = T(13) },
            });

        await Should.ThrowAsync<InvalidRequestException>(() => Split(12));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutTheContainerLock_NothingIsStaged()
    {
        _lockRepository.IsHeldBy(Arg.Any<string>(), _container.Id, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await Should.ThrowAsync<ContainerLockedException>(() => Split(12));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AChildOfAContainer_CannotBeSplit()
    {
        _container.ParentWorkId = Guid.NewGuid();

        await Should.ThrowAsync<KeyNotFoundException>(() => Split(12));
    }

    [Test]
    public async Task AReplacementOutsideTheCallersGroups_MakesTheContainerNotFound_AndNothingIsStaged()
    {
        var hiddenReplacement = Guid.NewGuid();
        _childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkChange>
            {
                new() { Id = Guid.NewGuid(), WorkId = _late.Id, Type = WorkChangeType.ReplacementStart, StartTime = T(13), EndTime = T(13), ReplaceClientId = hiddenReplacement },
            });
        _handler = CreateHandler(TestGroupWriteVisibility.ClientsHidden(hiddenReplacement));

        await Should.ThrowAsync<KeyNotFoundException>(() => Split(12));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFutureOwnerOutsideTheCallersGroups_MakesTheContainerNotFound()
    {
        var hiddenCustomer = Guid.NewGuid();
        _late.ShiftId = Guid.NewGuid();
        _shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { new() { Id = _late.ShiftId, ClientId = hiddenCustomer } });
        _handler = CreateHandler(TestGroupWriteVisibility.ClientsHidden(hiddenCustomer));

        await Should.ThrowAsync<KeyNotFoundException>(() => Split(12));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASplitTimeWithSeconds_IsRefusedBeforeAnythingIsStaged()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new SplitContainerWorkCommand(_container.Id, new TimeOnly(12, 0, 30), _target), CancellationToken.None));

        await _splitWriter.DidNotReceive().StageAsync(Arg.Any<ContainerSplitPlan>(), Arg.Any<CancellationToken>());
    }
}
