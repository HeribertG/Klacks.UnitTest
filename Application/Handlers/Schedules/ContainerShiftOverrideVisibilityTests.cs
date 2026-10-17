// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container override endpoints answer a hidden container exactly like a missing one (KeyNotFoundException with the
/// shared missing-shift text) before anything else runs; an override of another container is answered exactly like a
/// missing override (PUT: NotFoundException with the same text, DELETE: false) before the work check and without a
/// write; new hidden task shifts are refused while unchanged ones stay saveable. The container edit lock keeps its
/// whitelist of resource types (400 otherwise) and refuses a container lock on a hidden container like a missing one,
/// before stale locks are swept or the holder is revealed; a ContainerWork lock (keyed by a work id) is not
/// container-checked but needs an existing work whose client, scenario and shift the caller may see, and is otherwise
/// answered with the same 404 for a missing and a hidden work, equally before anything is swept or revealed.
/// </summary>

using Klacks.Api.Application.Commands.ContainerShiftOverrides;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ContainerShiftOverrides;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerShiftOverrides;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ContainerShiftOverrideVisibilityTests
{
    private static readonly DateOnly Day = new(2027, 3, 1);

    private IContainerShiftOverrideRepository _repository = null!;
    private IContainerLockRepository _lockRepository = null!;
    private IUserService _userService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftVisibilityReader _reader = null!;
    private IContainerWorkChildrenReadRepository _workReader = null!;
    private IScheduleRowAccessGuard _rowAccessGuard = null!;
    private Guid _containerId;
    private Guid _hiddenShiftId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IContainerShiftOverrideRepository>();
        _lockRepository = Substitute.For<IContainerLockRepository>();
        _userService = Substitute.For<IUserService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _reader = Substitute.For<IShiftVisibilityReader>();
        _workReader = Substitute.For<IContainerWorkChildrenReadRepository>();
        _rowAccessGuard = Substitute.For<IScheduleRowAccessGuard>();
        _rowAccessGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _containerId = Guid.NewGuid();
        _hiddenShiftId = Guid.NewGuid();
        _lockRepository.IsHeldBy(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _reader.GetContainerItemShiftIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
    }

    private ContainerVisibilityGuard Guard(IShiftVisibilityGuard shiftGuard) =>
        new(shiftGuard, _reader, Substitute.For<ILogger<ContainerVisibilityGuard>>());

    private string HiddenContainerText() => string.Format(ShiftVisibilityMessages.ShiftNotFound, _containerId);

    [Test]
    public async Task GetForDate_HiddenContainer_IsAnsweredLikeAMissingOne_NothingRead()
    {
        var handler = new GetContainerShiftOverrideQueryHandler(_repository, new ScheduleMapper(), Guard(ShiftVisibilityGuards.Hiding(_containerId)));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetContainerShiftOverrideQuery(_containerId, Day), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _repository.DidNotReceiveWithAnyArgs().GetByContainerAndDateWithItems(default, default, default);
    }

    [Test]
    public async Task GetForRange_HiddenContainer_IsAnsweredLikeAMissingOne_NothingRead()
    {
        var handler = new GetContainerShiftOverridesForRangeQueryHandler(
            _repository, new ScheduleMapper(), Guard(ShiftVisibilityGuards.Hiding(_containerId)));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetContainerShiftOverridesForRangeQuery(_containerId, Day, Day), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _repository.DidNotReceiveWithAnyArgs().GetByContainerAndDateRange(default, default, default, default);
    }

    [Test]
    public async Task Post_HiddenContainer_IsRefusedBeforeTheLockCheck_NothingWritten()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PostHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(new PostContainerShiftOverrideCommand(_containerId, Resource(Guid.NewGuid())), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _lockRepository.DidNotReceiveWithAnyArgs().IsHeldBy(default!, default, default, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Post_NewHiddenTaskShift_IsAnsweredLikeAMissingShift_NothingWritten()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PostHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .Handle(new PostContainerShiftOverrideCommand(_containerId, Resource(_hiddenShiftId)), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_HiddenTaskShiftCopiedFromTheContainersTemplate_IsSaved()
    {
        _reader.GetContainerItemShiftIdsAsync(_containerId, Arg.Any<CancellationToken>()).Returns([_hiddenShiftId]);

        await PostHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .Handle(new PostContainerShiftOverrideCommand(_containerId, Resource(_hiddenShiftId)), CancellationToken.None);

        await _repository.Received(1).Add(Arg.Any<ContainerShiftOverride>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_OverrideOfAnotherContainer_IsAnsweredLikeAMissingOverride_BeforeTheWorkCheck()
    {
        var foreign = StoredOverride(Guid.NewGuid());
        var missingId = Guid.NewGuid();
        _repository.GetWithTracking(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);

        var wrong = await Should.ThrowAsync<NotFoundException>(() => PutHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new PutContainerShiftOverrideCommand(_containerId, foreign.Id, Resource(Guid.NewGuid())), CancellationToken.None));
        var missing = await Should.ThrowAsync<NotFoundException>(() => PutHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new PutContainerShiftOverrideCommand(_containerId, missingId, Resource(Guid.NewGuid())), CancellationToken.None));

        wrong.Message.ShouldBe(string.Format(ContainerTemplateMessages.OverrideNotFound, foreign.Id));
        missing.Message.ShouldBe(string.Format(ContainerTemplateMessages.OverrideNotFound, missingId));
        await _repository.DidNotReceiveWithAnyArgs().HasWorkForOverride(default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_NewHiddenTaskShift_IsAnsweredLikeAMissingShift_NothingWritten()
    {
        var stored = StoredOverride(_containerId);
        _repository.GetWithTracking(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PutHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .Handle(new PutContainerShiftOverrideCommand(_containerId, stored.Id, Resource(_hiddenShiftId)), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        stored.ContainerShiftOverrideItems.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_HiddenContainer_IsAnsweredLikeAMissingOne_NothingDeleted()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => DeleteHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(new DeleteContainerShiftOverrideCommand(_containerId, Guid.NewGuid()), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _repository.DidNotReceiveWithAnyArgs().Delete(default!);
    }

    [Test]
    public async Task Delete_OverrideOfAnotherContainer_IsAnsweredLikeAMissingOverride_NothingDeleted()
    {
        var foreign = StoredOverride(Guid.NewGuid());
        _repository.GetWithTracking(foreign.Id, Arg.Any<CancellationToken>()).Returns(foreign);

        var result = await DeleteHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new DeleteContainerShiftOverrideCommand(_containerId, foreign.Id), CancellationToken.None);

        result.ShouldBeFalse();
        await _repository.DidNotReceiveWithAnyArgs().HasWorkForOverride(default, default, default);
        await _repository.DidNotReceiveWithAnyArgs().Delete(default!);
    }

    [Test]
    public async Task Delete_OverrideOfTheContainer_IsDeleted()
    {
        var stored = StoredOverride(_containerId);
        _repository.GetWithTracking(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await DeleteHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new DeleteContainerShiftOverrideCommand(_containerId, stored.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).Delete(stored);
    }

    [Test]
    public async Task Lock_UnknownResourceType_IsRejectedWith400_NothingTouched()
    {
        var handler = LockHandler(ShiftVisibilityGuards.AllowAll());

        await Should.ThrowAsync<BadRequestException>(
            () => handler.Handle(new AcquireContainerLockCommand("Anything", _containerId, "i"), CancellationToken.None));

        await _lockRepository.DidNotReceiveWithAnyArgs().DeleteStale(default!, default, default, default);
        await _lockRepository.DidNotReceiveWithAnyArgs().GetByResource(default!, default, default);
    }

    [TestCase(ContainerLockResourceTypes.ContainerTemplate)]
    [TestCase(ContainerLockResourceTypes.ContainerShiftOverride)]
    public async Task Lock_OnAHiddenContainer_IsAnsweredLikeAMissingOne_BeforeTheHolderIsRevealed(string resourceType)
    {
        var handler = LockHandler(ShiftVisibilityGuards.Hiding(_containerId));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new AcquireContainerLockCommand(resourceType, _containerId, "i"), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _lockRepository.DidNotReceiveWithAnyArgs().DeleteStale(default!, default, default, default);
        await _lockRepository.DidNotReceiveWithAnyArgs().GetByResource(default!, default, default);
        await _lockRepository.DidNotReceiveWithAnyArgs().Add(default!, default);
    }

    [Test]
    public async Task Lock_ContainerWork_OnAVisibleWork_IsAcquired_WithoutContainerCheck()
    {
        var work = StoredWork();
        var shiftGuard = ShiftVisibilityGuards.AllowAll();
        _lockRepository.Add(Arg.Any<ContainerLock>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<ContainerLock>());

        var result = await LockHandler(shiftGuard)
            .Handle(new AcquireContainerLockCommand(ContainerLockResourceTypes.ContainerWork, work.Id, "i"), CancellationToken.None);

        result.Acquired.ShouldBeTrue();
        await shiftGuard.DidNotReceiveWithAnyArgs().ExistsAndVisibleAsync(default, default);
        await shiftGuard.Received(1).IsVisibleAsync(work.ShiftId, Arg.Any<CancellationToken>());
        await _rowAccessGuard.Received(1).IsVisibleAsync(work.ClientId, work.AnalyseToken, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Lock_ContainerWork_MissingWork_IsAnsweredWith404_BeforeTheHolderIsRevealed()
    {
        var work = StoredWork();
        _workReader.GetParentWorkNoTracking(work.Id, Arg.Any<CancellationToken>()).Returns((Work?)null);

        await ShouldRefuseWorkLockAsync(work, ShiftVisibilityGuards.AllowAll());
    }

    [Test]
    public async Task Lock_ContainerWork_HiddenClientOrScenario_IsAnsweredLikeAMissingWork()
    {
        var work = StoredWork();
        _rowAccessGuard.IsVisibleAsync(work.ClientId, work.AnalyseToken, Arg.Any<CancellationToken>()).Returns(false);

        await ShouldRefuseWorkLockAsync(work, ShiftVisibilityGuards.AllowAll());
    }

    [Test]
    public async Task Lock_ContainerWork_HiddenShift_IsAnsweredLikeAMissingWork()
    {
        var work = StoredWork();

        await ShouldRefuseWorkLockAsync(work, ShiftVisibilityGuards.Hiding(work.ShiftId));
    }

    private async Task ShouldRefuseWorkLockAsync(Work work, IShiftVisibilityGuard shiftGuard)
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => LockHandler(shiftGuard)
            .Handle(new AcquireContainerLockCommand(ContainerLockResourceTypes.ContainerWork, work.Id, "i"), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ContainerLockMessages.WorkNotFound, work.Id));
        await _lockRepository.DidNotReceiveWithAnyArgs().DeleteStale(default!, default, default, default);
        await _lockRepository.DidNotReceiveWithAnyArgs().GetByResource(default!, default, default);
        await _lockRepository.DidNotReceiveWithAnyArgs().Add(default!, default);
    }

    private Work StoredWork()
    {
        var work = new Work { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), AnalyseToken = Guid.NewGuid() };
        _workReader.GetParentWorkNoTracking(work.Id, Arg.Any<CancellationToken>()).Returns(work);
        return work;
    }

    [Test]
    public async Task Lock_OnAVisibleContainer_IsAcquired()
    {
        _lockRepository.Add(Arg.Any<ContainerLock>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<ContainerLock>());

        var result = await LockHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new AcquireContainerLockCommand(ContainerLockResourceTypes.ContainerTemplate, _containerId, "i"), CancellationToken.None);

        result.Acquired.ShouldBeTrue();
        result.ResourceId.ShouldBe(_containerId);
    }

    private PostContainerShiftOverrideCommandHandler PostHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _lockRepository, _unitOfWork, new ScheduleMapper(), _userService, Guard(shiftGuard),
            Substitute.For<ILogger<PostContainerShiftOverrideCommandHandler>>());

    private PutContainerShiftOverrideCommandHandler PutHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _lockRepository, _unitOfWork, new ScheduleMapper(), _userService, Guard(shiftGuard),
            Substitute.For<ILogger<PutContainerShiftOverrideCommandHandler>>());

    private DeleteContainerShiftOverrideCommandHandler DeleteHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _unitOfWork, Guard(shiftGuard), Substitute.For<ILogger<DeleteContainerShiftOverrideCommandHandler>>());

    private AcquireContainerLockCommandHandler LockHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_lockRepository, _userService, _unitOfWork, Guard(shiftGuard), _workReader, _rowAccessGuard, shiftGuard,
            Substitute.For<ILogger<AcquireContainerLockCommandHandler>>());

    private ContainerShiftOverrideResource Resource(Guid shiftId) => new()
    {
        ContainerId = _containerId,
        Date = Day,
        FromTime = new TimeOnly(6, 0),
        UntilTime = new TimeOnly(22, 0),
        ContainerShiftOverrideItems = [new ContainerShiftOverrideItemResource { ShiftId = shiftId }]
    };

    private static ContainerShiftOverride StoredOverride(Guid containerId) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = containerId,
        Date = Day,
        FromTime = new TimeOnly(6, 0),
        UntilTime = new TimeOnly(22, 0)
    };
}
