// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule (2026-11-04) for the day override of a container: like the template, an item that reaches outside the
/// override window, by itself or by its travel, briefing or debriefing time, refuses the save (Post and Put) before anything
/// is written. Override 08:00-16:00.
/// </summary>

using Klacks.Api.Application.Commands.ContainerShiftOverrides;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ContainerShiftOverrides;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ContainerShiftOverrides;

[TestFixture]
public class ContainerShiftOverrideFitTests
{
    private static readonly DateOnly Day = new(2027, 3, 1);

    private readonly Guid _containerId = Guid.NewGuid();
    private IContainerShiftOverrideRepository _repository = null!;
    private IContainerLockRepository _lockRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ContainerShiftOverride _stored = null!;

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IContainerShiftOverrideRepository>();
        _stored = new ContainerShiftOverride { Id = Guid.NewGuid(), ContainerId = _containerId, Date = Day, FromTime = T(8), UntilTime = T(16) };
        _repository.GetWithTracking(_stored.Id, Arg.Any<CancellationToken>()).Returns(_stored);
        _repository.HasWorkForOverride(_containerId, Day, Arg.Any<CancellationToken>()).Returns(false);
        _lockRepository = Substitute.For<IContainerLockRepository>();
        _lockRepository.IsHeldBy(Arg.Any<string>(), _containerId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private ContainerShiftOverrideResource Override(params ContainerShiftOverrideItemResource[] items) => new()
    {
        ContainerId = _containerId, Date = Day, FromTime = T(8), UntilTime = T(16),
        ContainerShiftOverrideItems = items.ToList(),
    };

    private static ContainerShiftOverrideItemResource Item(TimeOnly start, TimeOnly end, TimeOnly? debriefing = null) => new()
    {
        ShiftId = Guid.NewGuid(), StartItem = start, EndItem = end, DebriefingTime = debriefing ?? TimeOnly.MinValue,
    };

    private static IEnumerable<TestCaseData> Overhangs()
    {
        yield return new TestCaseData(Item(T(15), T(17))).SetName("ItemEndsAfterUntil");
        yield return new TestCaseData(Item(T(7), T(9))).SetName("ItemStartsBeforeFrom");
        yield return new TestCaseData(Item(T(14), T(15, 45), T(0, 30))).SetName("ItemFitsButDebriefingReachesOutside");
    }

    private PostContainerShiftOverrideCommandHandler PostHandler() =>
        new(_repository, _lockRepository, _unitOfWork, new ScheduleMapper(), Substitute.For<IUserService>(),
            Substitute.For<IContainerVisibilityGuard>(), Substitute.For<ILogger<PostContainerShiftOverrideCommandHandler>>());

    private PutContainerShiftOverrideCommandHandler PutHandler() =>
        new(_repository, _lockRepository, _unitOfWork, new ScheduleMapper(), Substitute.For<IUserService>(),
            Substitute.For<IContainerVisibilityGuard>(), Substitute.For<ILogger<PutContainerShiftOverrideCommandHandler>>());

    [TestCaseSource(nameof(Overhangs))]
    public async Task PostHandler_RefusesOverhangingItem(ContainerShiftOverrideItemResource item)
    {
        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PostHandler().Handle(new PostContainerShiftOverrideCommand(_containerId, Override(item)), CancellationToken.None));

        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCaseSource(nameof(Overhangs))]
    public async Task PutHandler_RefusesOverhangingItem_WithoutTouchingTheStoredOverride(ContainerShiftOverrideItemResource item)
    {
        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PutHandler().Handle(new PutContainerShiftOverrideCommand(_containerId, _stored.Id, Override(item)), CancellationToken.None));

        _stored.ContainerShiftOverrideItems.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task PostHandler_StoresAnItemThatFitsWithItsDebriefing()
    {
        await PostHandler().Handle(
            new PostContainerShiftOverrideCommand(_containerId, Override(Item(T(14), T(15, 30), T(0, 30)))), CancellationToken.None);

        await _repository.ReceivedWithAnyArgs(1).Add(default!);
    }
}
