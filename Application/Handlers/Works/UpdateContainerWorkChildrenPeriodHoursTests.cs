// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Saving the children of a container changes the container's own hours, its sub-breaks and what its sub-works and work
/// changes count, so after the commit and the overtime cascade the period hours of the container's real pay period are
/// recalculated, stored and pushed once for the owner, every sub-work person and every replacement person, stored and
/// saved. Before 2026-11-07 the save recalculated no period hours at all.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class UpdateContainerWorkChildrenPeriodHoursTests
{
    private const string ConnectionId = "connection-1";

    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);

    private IContainerWorkChildrenReadRepository _childrenReadRepository = null!;
    private IContainerWorkChildrenManager _childrenManager = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private Work _container = null!;
    private Work _customerChild = null!;
    private Guid _storedSubstituteId;
    private Guid _savedSubstituteId;

    [SetUp]
    public void SetUp()
    {
        _childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        _childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _notificationFacade.GetConnectionId().Returns(ConnectionId);
        _periodHoursService.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));

        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), AnalyseToken = Guid.NewGuid(),
        };
        _customerChild = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), ParentWorkId = _container.Id,
        };
        _storedSubstituteId = Guid.NewGuid();
        _savedSubstituteId = Guid.NewGuid();
        var storedChange = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = _customerChild.Id, Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = _storedSubstituteId,
        };

        _childrenReadRepository.GetParentWorkNoTracking(_container.Id, Arg.Any<CancellationToken>()).Returns(_container);
        _childrenReadRepository.GetChildWorksWithShiftClient(_container.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Work> { _customerChild });
        _childrenReadRepository.GetChildBreaksWithAbsence(_container.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Break>());
        _childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkChange> { storedChange });
        _childrenManager.UpdateChildrenAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TimeOnly?>(), Arg.Any<TimeOnly?>(),
                Arg.Any<List<Work>>(), Arg.Any<List<Break>>(), Arg.Any<List<WorkChange>>(), Arg.Any<CancellationToken>())
            .Returns(_container);
    }

    [Test]
    public async Task Save_RecalculatesOwnerSubWorkPersonsAndSubstitutes_InTheContainersPayPeriod_AfterTheCascade()
    {
        var resource = new UpdateContainerWorkChildrenResource();
        resource.SubWorks.Add(new WorkResource
        {
            Id = _customerChild.Id, ClientId = _customerChild.ClientId, ShiftId = _customerChild.ShiftId,
            CurrentDate = Day, StartTime = _customerChild.StartTime, EndTime = _customerChild.EndTime,
        });
        resource.SubWorkChanges.Add(new WorkChangeResource
        {
            WorkId = _customerChild.Id, Type = WorkChangeType.ReplacementWithin, ReplaceClientId = _savedSubstituteId,
        });

        await NewHandler().Handle(new UpdateContainerWorkChildrenCommand(_container.Id, resource), CancellationToken.None);

        Received.InOrder(() =>
        {
            _overtimeCascadeService.ReprocessSuccessorsAsync(Arg.Any<IReadOnlyCollection<Work>>());
            _periodHoursService.RecalculateAndNotifyAsync(
                _container.ClientId, PeriodStart, PeriodEnd, _container.AnalyseToken, ConnectionId);
        });
        foreach (var clientId in new[] { _customerChild.ClientId, _storedSubstituteId, _savedSubstituteId })
        {
            await _periodHoursService.Received(1).RecalculateAndNotifyAsync(
                clientId, PeriodStart, PeriodEnd, _container.AnalyseToken, ConnectionId);
        }

        _periodHoursService.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IPeriodHoursService.RecalculateAndNotifyAsync))
            .ShouldBe(4);
    }

    [Test]
    public async Task Save_OnADayWhoseWeekReachesTheNextPayPeriod_AlsoStoresThatPeriod_WithoutAPush()
    {
        var lateDay = new DateOnly(2027, 5, 28);
        var juneStart = new DateOnly(2027, 6, 1);
        var juneEnd = new DateOnly(2027, 6, 30);
        _container.CurrentDate = lateDay;
        _periodHoursService.GetPeriodBoundariesAsync(lateDay).Returns((PeriodStart, PeriodEnd));
        _periodHoursService.GetPeriodBoundariesAsync(juneStart).Returns((juneStart, juneEnd));

        await NewHandler().Handle(
            new UpdateContainerWorkChildrenCommand(_container.Id, new UpdateContainerWorkChildrenResource()),
            CancellationToken.None);

        await _periodHoursService.Received(1).RecalculateAndNotifyAsync(
            _container.ClientId, PeriodStart, PeriodEnd, _container.AnalyseToken, ConnectionId);
        await _periodHoursService.Received(1).RecalculatePeriodHoursAsync(
            _container.ClientId, juneStart, juneEnd, _container.AnalyseToken);
        await _periodHoursService.DidNotReceive().RecalculateAndNotifyAsync(
            Arg.Any<Guid>(), juneStart, juneEnd, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    private UpdateContainerWorkChildrenCommandHandler NewHandler()
    {
        var lockRepository = Substitute.For<IContainerLockRepository>();
        lockRepository.IsHeldBy(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        visibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        visibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        return new UpdateContainerWorkChildrenCommandHandler(
            _childrenReadRepository, TestScheduleRowAccess.Over(visibilityGuard), new ScheduleMapper(),
            Substitute.For<IUnitOfWork>(), _notificationFacade, lockRepository, Substitute.For<IUserService>(),
            _childrenManager, _overtimeCascadeService, ShiftVisibilityGuards.AllowAll(), Substitute.For<IWorkWriteGuard>(),
            Substitute.For<IReplacementWriteGuard>(), Substitute.For<IContainerWorkChangeGuard>(),
            Substitute.For<IContainerChildrenWriteGuard>(), _periodHoursService,
            Substitute.For<ILogger<UpdateContainerWorkChildrenCommandHandler>>());
    }
}
