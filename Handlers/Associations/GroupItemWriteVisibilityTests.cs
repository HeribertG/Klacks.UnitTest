// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The group-link endpoints (api/backend/GroupItems: POST, DELETE {id}, POST bulk, DELETE remove) are open to
/// supervisors. Until 2026-10-01 none of them checked group visibility, so a group-restricted supervisor could
/// link a foreign client into their own group — which made that client visible to them — or edit the links of
/// a hidden group. A hidden group or client must now be answered like a missing one, before anything is written.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Commands.Associations;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Handlers.Associations;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using GroupItemDeleteHandler = Klacks.Api.Application.Handlers.GroupItems.DeleteCommandHandler;
using GroupItemPostHandler = Klacks.Api.Application.Handlers.GroupItems.PostCommandHandler;

namespace Klacks.UnitTest.Handlers.Associations;

[TestFixture]
public class GroupItemWriteVisibilityTests
{
    private IGroupItemRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _visibleGroupId;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _visibleGroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();
    }

    private IGroupVisibilityGuard GroupsVisibleExcept(Guid hiddenGroupId)
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Guid>() != hiddenGroupId));
        guard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(!ci.Arg<IReadOnlyCollection<Guid>>().Contains(hiddenGroupId)));
        return guard;
    }

    private GroupItemPostHandler PostHandler(IGroupVisibilityGuard groups, IClientVisibilityGuard clients) =>
        new(_repository, new GroupMapper(), _unitOfWork, groups, clients, NullLogger<GroupItemPostHandler>.Instance);

    private static GroupItemResource Link(Guid groupId, Guid? clientId) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = groupId,
        ClientId = clientId,
        ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private async Task NothingWasWrittenAsync()
    {
        await _repository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_IntoHiddenGroup_IsAnsweredLikeAMissingGroup()
    {
        var handler = PostHandler(GroupsVisibleExcept(_hiddenGroupId), TestGroupWriteVisibility.AllClientsVisible());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PostCommand<GroupItemResource>(Link(_hiddenGroupId, Guid.NewGuid())), CancellationToken.None));

        ex.Message.ShouldBe($"Group with ID {_hiddenGroupId} not found");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Post_HiddenClientIntoOwnGroup_IsAnsweredLikeAMissingClient()
    {
        var hiddenClientId = Guid.NewGuid();
        var handler = PostHandler(GroupsVisibleExcept(_hiddenGroupId), TestGroupWriteVisibility.ClientsHidden(hiddenClientId));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PostCommand<GroupItemResource>(Link(_visibleGroupId, hiddenClientId)), CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {hiddenClientId} not found");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Post_VisibleClientIntoOwnGroup_IsWritten()
    {
        var handler = PostHandler(GroupsVisibleExcept(_hiddenGroupId), TestGroupWriteVisibility.AllClientsVisible());

        await handler.Handle(new PostCommand<GroupItemResource>(Link(_visibleGroupId, Guid.NewGuid())), CancellationToken.None);

        await _repository.Received(1).Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Delete_LinkOfHiddenGroup_IsAnsweredLikeAMissingLink()
    {
        var linkId = Guid.NewGuid();
        _repository.Get(linkId).Returns(new GroupItem { Id = linkId, GroupId = _hiddenGroupId });
        var handler = new GroupItemDeleteHandler(
            _repository, new GroupMapper(), _unitOfWork, GroupsVisibleExcept(_hiddenGroupId),
            NullLogger<GroupItemDeleteHandler>.Instance);

        var result = await handler.Handle(new DeleteCommand<GroupItemResource>(linkId), CancellationToken.None);

        result.ShouldBeNull();
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task RemoveByClientAndGroup_HiddenGroup_IsAnsweredLikeAMissingLink()
    {
        var handler = new RemoveGroupItemByClientAndGroupCommandHandler(
            _repository, _unitOfWork, GroupsVisibleExcept(_hiddenGroupId));

        var found = await handler.Handle(
            new RemoveGroupItemByClientAndGroupCommand { ClientId = Guid.NewGuid(), GroupId = _hiddenGroupId },
            CancellationToken.None);

        found.ShouldBeFalse();
        await _repository.DidNotReceive().GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>());
        _repository.DidNotReceive().Remove(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task BulkAdd_OneHiddenGroup_RefusesTheWholeBatch()
    {
        var handler = new BulkAddGroupItemsCommandHandler(
            _repository, _unitOfWork, GroupsVisibleExcept(_hiddenGroupId), TestGroupWriteVisibility.AllClientsVisible(),
            NullLogger<BulkAddGroupItemsCommandHandler>.Instance);
        var command = new BulkAddGroupItemsCommand(new BulkGroupItemRequest
        {
            Items = [Link(_visibleGroupId, Guid.NewGuid()), Link(_hiddenGroupId, Guid.NewGuid())]
        });

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(command, CancellationToken.None));

        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task BulkAdd_OneHiddenClient_RefusesTheWholeBatch()
    {
        var hiddenClientId = Guid.NewGuid();
        var handler = new BulkAddGroupItemsCommandHandler(
            _repository, _unitOfWork, GroupsVisibleExcept(_hiddenGroupId), TestGroupWriteVisibility.ClientsHidden(hiddenClientId),
            NullLogger<BulkAddGroupItemsCommandHandler>.Instance);
        var command = new BulkAddGroupItemsCommand(new BulkGroupItemRequest
        {
            Items = [Link(_visibleGroupId, Guid.NewGuid()), Link(_visibleGroupId, hiddenClientId)]
        });

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(command, CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {hiddenClientId} not found");
        await NothingWasWrittenAsync();
    }
}
