// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container template endpoints (GET/POST/PUT/DELETE templates, available tasks) answer a hidden container exactly
/// like a missing one (KeyNotFoundException with the shared missing-shift text) before the lock is checked and before
/// anything is read or written. Writes refuse new hidden task shifts but keep unchanged ones; PUT refuses an item id
/// of another template like a missing item, before the first write; available tasks only offers visible task shifts.
/// Unrestricted callers (AllowAll) are unchanged.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ContainerTemplates;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ContainerTemplateVisibilityTests
{
    private const int Monday = 1;

    private IContainerTemplateRepository _repository = null!;
    private IContainerLockRepository _lockRepository = null!;
    private IUserService _userService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftVisibilityReader _reader = null!;
    private IContainerAvailableTasksService _availableTasks = null!;
    private Guid _containerId;
    private Guid _hiddenShiftId;
    private Guid _visibleShiftId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IContainerTemplateRepository>();
        _lockRepository = Substitute.For<IContainerLockRepository>();
        _userService = Substitute.For<IUserService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _reader = Substitute.For<IShiftVisibilityReader>();
        _availableTasks = Substitute.For<IContainerAvailableTasksService>();
        _containerId = Guid.NewGuid();
        _hiddenShiftId = Guid.NewGuid();
        _visibleShiftId = Guid.NewGuid();
        _lockRepository.IsHeldBy(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _reader.GetContainerItemShiftIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _repository.GetTemplatesForContainer(Arg.Any<Guid>()).Returns(new List<ContainerTemplate>());
        _repository.GetTemplatesForContainerWithTracking(Arg.Any<Guid>()).Returns(new List<ContainerTemplate>());
        _repository.PutWithItems(Arg.Any<Guid>(), Arg.Any<List<ContainerTemplateItem>>()).Returns(new ContainerTemplateUpdateResult());
    }

    private ContainerVisibilityGuard Guard(IShiftVisibilityGuard shiftGuard) =>
        new(shiftGuard, _reader, Substitute.For<ILogger<ContainerVisibilityGuard>>());

    private GetContainerTemplatesQueryHandler GetHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, new ScheduleMapper(), Guard(shiftGuard), Substitute.For<ILogger<GetContainerTemplatesQueryHandler>>());

    private PostContainerTemplatesCommandHandler PostHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _unitOfWork, new ScheduleMapper(), _lockRepository, _userService, Guard(shiftGuard),
            Substitute.For<ILogger<PostContainerTemplatesCommandHandler>>());

    private PutContainerTemplatesCommandHandler PutHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _unitOfWork, new ScheduleMapper(), _lockRepository, _userService, Guard(shiftGuard),
            Substitute.For<ILogger<PutContainerTemplatesCommandHandler>>());

    private DeleteContainerTemplatesCommandHandler DeleteHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_repository, _unitOfWork, new ScheduleMapper(), _lockRepository, _userService, Guard(shiftGuard),
            Substitute.For<ILogger<DeleteContainerTemplatesCommandHandler>>());

    private GetAvailableTasksQueryHandler AvailableTasksHandler(IShiftVisibilityGuard shiftGuard) =>
        new(_availableTasks, new ScheduleMapper(), Guard(shiftGuard), shiftGuard);

    private string HiddenContainerText() => string.Format(ShiftVisibilityMessages.ShiftNotFound, _containerId);

    [Test]
    public async Task Get_HiddenContainer_IsAnsweredLikeAMissingOne_NothingRead()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => GetHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(new GetContainerTemplatesQuery(_containerId), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _repository.DidNotReceiveWithAnyArgs().GetTemplatesForContainer(default);
    }

    [Test]
    public async Task Get_VisibleContainerWithoutTemplates_ReturnsEmptyList()
    {
        var result = await GetHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new GetContainerTemplatesQuery(_containerId), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task Post_HiddenContainer_IsRefusedBeforeTheLockCheck_NothingWritten()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PostHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(new PostContainerTemplatesCommand(_containerId, [Template(Item(_visibleShiftId))]), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _lockRepository.DidNotReceiveWithAnyArgs().IsHeldBy(default!, default, default, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_NewHiddenTaskShift_IsAnsweredLikeAMissingShift_NothingWritten()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PostHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .Handle(new PostContainerTemplatesCommand(_containerId, [Template(Item(_visibleShiftId), Item(_hiddenShiftId))]),
                CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_Unrestricted_HiddenTaskShift_IsWritten()
    {
        await PostHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new PostContainerTemplatesCommand(_containerId, [Template(Item(_hiddenShiftId))]), CancellationToken.None);

        await _repository.Received(1).Add(Arg.Any<ContainerTemplate>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_HiddenContainer_IsRefusedBeforeTheLockCheck_NothingWritten()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PutHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(new PutContainerTemplatesCommand(_containerId, [Template(Item(_visibleShiftId))]), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _lockRepository.DidNotReceiveWithAnyArgs().IsHeldBy(default!, default, default, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().GetTemplatesForContainerWithTracking(default);
    }

    [Test]
    public async Task Put_NewHiddenTaskShift_IsRefusedBeforeTheFirstWrite()
    {
        var stored = StoredTemplate(Monday, _visibleShiftId);
        var secondDay = StoredTemplate(Monday + 1, _visibleShiftId);
        _repository.GetTemplatesForContainerWithTracking(_containerId).Returns([stored, secondDay]);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PutHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .Handle(new PutContainerTemplatesCommand(_containerId,
                [ResourceOf(stored), ResourceOf(secondDay, Item(_hiddenShiftId))]), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShiftId));
        await _repository.DidNotReceiveWithAnyArgs().PutWithItems(default, default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_UnchangedHiddenTaskShiftOfAMixedContainer_IsSaved()
    {
        var stored = StoredTemplate(Monday, _hiddenShiftId, _visibleShiftId);
        _repository.GetTemplatesForContainerWithTracking(_containerId).Returns([stored]);
        _reader.GetContainerItemShiftIdsAsync(_containerId, Arg.Any<CancellationToken>()).Returns([_hiddenShiftId, _visibleShiftId]);

        await PutHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId))
            .Handle(new PutContainerTemplatesCommand(_containerId, [ResourceOf(stored)]), CancellationToken.None);

        await _repository.Received(1).PutWithItems(stored.Id, Arg.Is<List<ContainerTemplateItem>>(items => items.Count == 2));
    }

    [Test]
    public async Task Put_ItemIdOfAnotherTemplate_IsAnsweredLikeAMissingItem_BeforeTheFirstWrite()
    {
        var stored = StoredTemplate(Monday, _visibleShiftId);
        _repository.GetTemplatesForContainerWithTracking(_containerId).Returns([stored]);
        var foreignItemId = Guid.NewGuid();
        var resource = ResourceOf(stored);
        resource.ContainerTemplateItems.Add(new ContainerTemplateItemResource { Id = foreignItemId, ShiftId = _visibleShiftId });

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PutHandler(ShiftVisibilityGuards.AllowAll())
            .Handle(new PutContainerTemplatesCommand(_containerId, [resource]), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ContainerTemplateMessages.ItemNotFound, foreignItemId));
        await _repository.DidNotReceiveWithAnyArgs().PutWithItems(default, default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_HiddenContainer_IsRefusedBeforeTheLockCheck_NothingDeleted()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => DeleteHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(new DeleteContainerTemplatesCommand(_containerId), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _lockRepository.DidNotReceiveWithAnyArgs().IsHeldBy(default!, default, default, default!, default);
        _repository.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [Test]
    public async Task AvailableTasks_HiddenContainer_IsAnsweredLikeAMissingOne()
    {
        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => AvailableTasksHandler(ShiftVisibilityGuards.Hiding(_containerId))
            .Handle(TasksQuery(), CancellationToken.None));

        ex.Message.ShouldBe(HiddenContainerText());
        await _availableTasks.DidNotReceiveWithAnyArgs().GetAvailableTasksAsync(default, default, default, default);
    }

    [Test]
    public async Task AvailableTasks_OffersOnlyVisibleTaskShifts()
    {
        GivenAvailableTasks(_visibleShiftId, _hiddenShiftId);

        var result = await AvailableTasksHandler(ShiftVisibilityGuards.Hiding(_hiddenShiftId)).Handle(TasksQuery(), CancellationToken.None);

        result.Select(r => r.Id).ShouldBe([_visibleShiftId]);
    }

    [Test]
    public async Task AvailableTasks_Unrestricted_OffersAllTaskShifts()
    {
        GivenAvailableTasks(_visibleShiftId, _hiddenShiftId);

        var result = await AvailableTasksHandler(ShiftVisibilityGuards.AllowAll()).Handle(TasksQuery(), CancellationToken.None);

        result.Select(r => r.Id).ShouldBe([_visibleShiftId, _hiddenShiftId]);
    }

    private GetAvailableTasksQuery TasksQuery() => new(_containerId, Monday, new TimeOnly(6, 0), new TimeOnly(22, 0));

    private void GivenAvailableTasks(params Guid[] shiftIds) =>
        _availableTasks.GetAvailableTasksAsync(
                _containerId, Monday, Arg.Any<TimeOnly>(), Arg.Any<TimeOnly>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(shiftIds.Select(id => new Shift { Id = id, ShiftType = ShiftType.IsTask }).ToList());

    private ContainerTemplateResource Template(params ContainerTemplateItemResource[] items) => new()
    {
        ContainerId = _containerId,
        Weekday = Monday,
        FromTime = new TimeOnly(6, 0),
        UntilTime = new TimeOnly(22, 0),
        ContainerTemplateItems = items.ToList()
    };

    private static ContainerTemplateItemResource Item(Guid shiftId) => new() { ShiftId = shiftId };

    private ContainerTemplate StoredTemplate(int weekday, params Guid[] shiftIds)
    {
        var templateId = Guid.NewGuid();
        return new ContainerTemplate
        {
            Id = templateId,
            ContainerId = _containerId,
            Weekday = weekday,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerTemplateItems = shiftIds
                .Select(id => new ContainerTemplateItem { Id = Guid.NewGuid(), ContainerTemplateId = templateId, ShiftId = id })
                .ToList()
        };
    }

    private static ContainerTemplateResource ResourceOf(ContainerTemplate stored, params ContainerTemplateItemResource[] extra) => new()
    {
        Id = stored.Id,
        ContainerId = stored.ContainerId,
        Weekday = stored.Weekday,
        FromTime = stored.FromTime,
        UntilTime = stored.UntilTime,
        ContainerTemplateItems = stored.ContainerTemplateItems
            .Select(i => new ContainerTemplateItemResource { Id = i.Id, ContainerTemplateId = stored.Id, ShiftId = i.ShiftId })
            .Concat(extra)
            .ToList()
    };
}
