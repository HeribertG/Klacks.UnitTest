// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The bulk delete skill deletes only what the user confirmed, and a confirmed preview names the container children
/// (child Works and Breaks) that go along. The skill compares its fresh plan with the confirmation, but a child can still
/// appear between that plan and the handler cascade. When the command carries ExpectedChildren, the handler therefore
/// reads the live children of the Works it is about to delete once, before it stages the first delete, and refuses with an
/// InvalidRequestException when they are not exactly the expected Works and Breaks: nothing is staged, nothing is
/// committed. Without ExpectedChildren (the multi-delete of the schedule) the handler behaves as before and never reads them.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class BulkDeleteWorksExpectedChildrenTests
{
    private static readonly DateOnly Day = new(2027, 4, 12);

    private IWorkRepository _workRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IContainerWorkCascadeService _cascadeService = null!;
    private IScheduleCompletionService _completionService = null!;
    private IOvertimeCascadeService _overtimeCascadeService = null!;
    private IPeriodHoursService _periodHoursService = null!;
    private IWorkNotificationFacade _notificationFacade = null!;
    private Guid _hiddenClientId;
    private Work _first = null!;
    private Work _second = null!;
    private Guid _childWork;
    private Guid _childBreak;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _cascadeService = Substitute.For<IContainerWorkCascadeService>();
        _completionService = Substitute.For<IScheduleCompletionService>();
        _overtimeCascadeService = Substitute.For<IOvertimeCascadeService>();
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _notificationFacade = Substitute.For<IWorkNotificationFacade>();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<Func<Work, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Work>>(0)
                .Where(w => ci.ArgAt<Func<Work, Guid>>(1)(w) != _hiddenClientId)
                .ToList());
        _periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Day, Day));

        _first = NewWork(Guid.NewGuid());
        _second = NewWork(Guid.NewGuid());
        _childWork = Guid.NewGuid();
        _childBreak = Guid.NewGuid();
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { _first, _second });
    }

    private static Work NewWork(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = Guid.NewGuid(),
        CurrentDate = Day,
        LockLevel = WorkLockLevel.None
    };

    private BulkDeleteWorksCommandHandler NewHandler() => new(
        _workRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(), _periodHoursService,
        _completionService, _notificationFacade, _overtimeCascadeService, _cascadeService,
        Substitute.For<IDayLockService>(), Substitute.For<IHttpContextAccessor>(),
        new ParentWorkLockGuard(new WorkLockLevelService()), Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

    private void GivenLiveChildren(ContainerChildIds children) =>
        _cascadeService.GetChildIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(children);

    private BulkDeleteWorksCommand Command(ContainerChildIds? expected) =>
        new(new BulkDeleteWorksRequest { WorkIds = [_first.Id, _second.Id] }, expected);

    private async Task ShouldRefuseAndStageNothing(ContainerChildIds expected)
    {
        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => NewHandler().Handle(Command(expected), CancellationToken.None));

        ex.Message.ShouldContain("Nothing was deleted");
        await _cascadeService.DidNotReceive().DeleteChildrenAsync(Arg.Any<Guid>());
        _workRepository.DidNotReceive().Remove(Arg.Any<Work>());
        await _completionService.DidNotReceiveWithAnyArgs().SaveBulkAndTrackAsync(default!);
        await _overtimeCascadeService.DidNotReceiveWithAnyArgs().ReprocessSuccessorsAsync(default!);
    }

    [Test]
    public async Task AChildWorkThatAppearedAfterTheConfirmation_RefusesBeforeAnythingIsStaged()
    {
        GivenLiveChildren(new ContainerChildIds([_childWork, Guid.NewGuid()], [_childBreak]));

        await ShouldRefuseAndStageNothing(new ContainerChildIds([_childWork], [_childBreak]));
    }

    [Test]
    public async Task AChildBreakThatAppearedAfterTheConfirmation_RefusesBeforeAnythingIsStaged()
    {
        GivenLiveChildren(new ContainerChildIds([_childWork], [_childBreak, Guid.NewGuid()]));

        await ShouldRefuseAndStageNothing(new ContainerChildIds([_childWork], [_childBreak]));
    }

    [Test]
    public async Task AChildThatVanishedAfterTheConfirmation_RefusesBeforeAnythingIsStaged()
    {
        GivenLiveChildren(new ContainerChildIds([_childWork], []));

        await ShouldRefuseAndStageNothing(new ContainerChildIds([_childWork], [_childBreak]));
    }

    [Test]
    public async Task ASwappedChildBreak_RefusesBeforeAnythingIsStaged()
    {
        GivenLiveChildren(new ContainerChildIds([_childWork], [Guid.NewGuid()]));

        await ShouldRefuseAndStageNothing(new ContainerChildIds([_childWork], [_childBreak]));
    }

    [Test]
    public async Task ChildrenAppearingWhereNoneWereExpected_RefuseBeforeAnythingIsStaged()
    {
        GivenLiveChildren(new ContainerChildIds([], [_childBreak]));

        await ShouldRefuseAndStageNothing(ContainerChildIds.None);
    }

    [Test]
    public async Task TheRefusalNamesTheShownAndTheCurrentNumberOfEntries()
    {
        GivenLiveChildren(new ContainerChildIds([_childWork, Guid.NewGuid()], [_childBreak]));

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => NewHandler().Handle(
            Command(new ContainerChildIds([_childWork], [_childBreak])), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ConfirmationBindingMessages.ContainerChildrenChanged, 2, 3));
    }

    [Test]
    public async Task ExactlyTheExpectedChildren_DeleteTheWorksAndTheirChildren()
    {
        GivenLiveChildren(new ContainerChildIds([_childWork], [_childBreak]));

        var response = await NewHandler().Handle(
            Command(new ContainerChildIds([_childWork], [_childBreak])), CancellationToken.None);

        response.DeletedIds.ShouldBe(new[] { _first.Id, _second.Id }, ignoreOrder: true);
        await _cascadeService.Received(1).DeleteChildrenAsync(_first.Id);
        await _cascadeService.Received(1).DeleteChildrenAsync(_second.Id);
        await _completionService.Received(1).SaveBulkAndTrackAsync(Arg.Any<List<(Guid, DateOnly, Guid?)>>());
    }

    [Test]
    public async Task NoChildrenOnBothSides_DeleteTheWorks()
    {
        GivenLiveChildren(ContainerChildIds.None);

        var response = await NewHandler().Handle(Command(ContainerChildIds.None), CancellationToken.None);

        response.SuccessCount.ShouldBe(2);
    }

    [Test]
    public async Task TheLiveChildrenAreReadOnceForTheWorksThatAreDeleted_NotForTheHiddenOnes()
    {
        var hidden = NewWork(_hiddenClientId);
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new List<Work> { _first, hidden });
        GivenLiveChildren(ContainerChildIds.None);

        await NewHandler().Handle(
            new BulkDeleteWorksCommand(
                new BulkDeleteWorksRequest { WorkIds = [_first.Id, hidden.Id] }, ContainerChildIds.None),
            CancellationToken.None);

        await _cascadeService.Received(1).GetChildIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(_first.Id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutExpectedChildren_TheChildrenAreNeverRead_AndTheWorksAreDeleted()
    {
        var response = await NewHandler().Handle(Command(null), CancellationToken.None);

        response.SuccessCount.ShouldBe(2);
        await _cascadeService.DidNotReceiveWithAnyArgs().GetChildIdsAsync(default!, default);
        await _cascadeService.Received(1).DeleteChildrenAsync(_first.Id);
        await _cascadeService.Received(1).DeleteChildrenAsync(_second.Id);
    }

    [Test]
    public void TheCommandDefaultsToNoExpectedChildren()
    {
        new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = [_first.Id] }).ExpectedChildren.ShouldBeNull();
    }
}
