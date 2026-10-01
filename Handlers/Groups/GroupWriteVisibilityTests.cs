// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Group writes must respect the caller's group visibility: a hidden target group, a hidden parent or a
/// hidden new parent is answered exactly like a missing one, adding a client the caller cannot see is
/// answered like a missing client, root groups are reserved for unrestricted callers, and no refused write
/// ever reaches the (self-committing) repository. Removing members of a visible group stays allowed.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Associations;
using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Associations;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Geo;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using GroupDeleteHandler = Klacks.Api.Application.Handlers.Groups.DeleteCommandHandler;
using GroupPostHandler = Klacks.Api.Application.Handlers.Groups.PostCommandHandler;
using GroupPutHandler = Klacks.Api.Application.Handlers.Groups.PutCommandHandler;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class GroupWriteVisibilityTests
{
    private static readonly DateTime ValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IGroupRepository _groupRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private GroupMapper _mapper = null!;
    private Guid _visibleRoot;
    private Guid _visibleGroup;
    private Guid _hiddenGroup;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<GroupResource?>>>())
            .Returns(ci => ci.Arg<Func<Task<GroupResource?>>>()());
        _mapper = new GroupMapper();
        _visibleRoot = Guid.NewGuid();
        _visibleGroup = Guid.NewGuid();
        _hiddenGroup = Guid.NewGuid();
    }

    private IGroupVisibilityGuard RestrictedTo(params Guid[] visibleGroupIds)
    {
        var visible = visibleGroupIds.ToHashSet();
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(visible.Contains(ci.Arg<Guid>())));
        guard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Guid>>().All(visible.Contains)));
        return guard;
    }

    private IGroupVisibilityGuard Supervisor() => RestrictedTo(_visibleRoot, _visibleGroup);

    private Group StoredGroup(Guid id, Guid? parent, params Guid[] memberClientIds) => new()
    {
        Id = id,
        Name = "Stored",
        Parent = parent,
        ValidFrom = ValidFrom,
        GroupItems = memberClientIds
            .Select(clientId => new GroupItem { Id = Guid.NewGuid(), GroupId = id, ClientId = clientId })
            .ToList()
    };

    private static GroupResource ResourceOf(Guid id, Guid? parent, params Guid[] memberClientIds) => new()
    {
        Id = id,
        Name = "Incoming",
        Parent = parent,
        ValidFrom = ValidFrom,
        GroupItems = memberClientIds
            .Select(clientId => new GroupItemResource { Id = Guid.NewGuid(), GroupId = id, ClientId = clientId })
            .ToList()
    };

    private GroupPostHandler PostHandler(IGroupVisibilityGuard groupGuard, IClientVisibilityGuard clientGuard) =>
        new(_groupRepository, _mapper, Substitute.For<IGroupGeocodingQueue>(), groupGuard, clientGuard, _unitOfWork,
            NullLogger<GroupPostHandler>.Instance);

    private GroupPutHandler PutHandler(IGroupVisibilityGuard groupGuard, IClientVisibilityGuard clientGuard) =>
        new(_groupRepository, _mapper, groupGuard, clientGuard, _unitOfWork, NullLogger<GroupPutHandler>.Instance);

    private async Task NoGroupWasWrittenAsync()
    {
        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        await _groupRepository.DidNotReceive().Put(Arg.Any<Group>());
        await _groupRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _groupRepository.DidNotReceive().MoveNode(Arg.Any<Guid>(), Arg.Any<Guid>());
        await _groupRepository.DidNotReceive()
            .SetCoordinatesAsync(Arg.Any<Guid>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_UnderHiddenParent_IsAnsweredLikeAMissingParent()
    {
        var handler = PostHandler(Supervisor(), TestGroupWriteVisibility.AllClientsVisible());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PostCommand<GroupResource>(ResourceOf(Guid.Empty, _hiddenGroup)), CancellationToken.None));

        ex.Message.ShouldBe($"Parent group with ID {_hiddenGroup} not found");
        await NoGroupWasWrittenAsync();
    }

    // An empty root makes nobody visible, so creating one is not a visibility write and stays open to a
    // restricted supervisor (Supervisors may create groups since 2026-09-21).
    [Test]
    public async Task Post_RootGroupByRestrictedCaller_IsWritten()
    {
        var handler = PostHandler(Supervisor(), TestGroupWriteVisibility.AllClientsVisible());

        await handler.Handle(new PostCommand<GroupResource>(ResourceOf(Guid.Empty, null)), CancellationToken.None);

        await _groupRepository.Received(1).Add(Arg.Any<Group>());
    }

    [Test]
    public async Task Post_WithHiddenClientAsMember_IsAnsweredLikeAMissingClient()
    {
        var hiddenClient = Guid.NewGuid();
        var handler = PostHandler(Supervisor(), TestGroupWriteVisibility.ClientsHidden(hiddenClient));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(
                new PostCommand<GroupResource>(ResourceOf(Guid.Empty, _visibleGroup, Guid.NewGuid(), hiddenClient)),
                CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {hiddenClient} not found");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Post_UnderVisibleParentWithVisibleMembers_IsWritten()
    {
        var handler = PostHandler(Supervisor(), TestGroupWriteVisibility.AllClientsVisible());

        var result = await handler.Handle(
            new PostCommand<GroupResource>(ResourceOf(Guid.Empty, _visibleGroup, Guid.NewGuid())), CancellationToken.None);

        result.ShouldNotBeNull();
        await _groupRepository.Received(1).Add(Arg.Any<Group>());
    }

    [Test]
    public async Task Put_HiddenGroup_IsAnsweredExactlyLikeAMissingGroup()
    {
        var missing = Guid.NewGuid();
        _groupRepository.Get(missing).Returns((Group?)null);
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, _visibleRoot));
        var restricted = PutHandler(Supervisor(), TestGroupWriteVisibility.AllClientsVisible());
        var unrestricted = PutHandler(
            TestGroupWriteVisibility.UnrestrictedGroups(), TestGroupWriteVisibility.AllClientsVisible());

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => restricted.Handle(new PutCommand<GroupResource>(ResourceOf(_hiddenGroup, _visibleRoot)), CancellationToken.None));
        var absent = await Should.ThrowAsync<KeyNotFoundException>(
            () => unrestricted.Handle(new PutCommand<GroupResource>(ResourceOf(missing, null)), CancellationToken.None));

        hidden.Message.ShouldBe($"Group with ID {_hiddenGroup} not found.");
        absent.Message.ShouldBe($"Group with ID {missing} not found.");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Put_MovingUnderHiddenParent_IsAnsweredLikeAMissingNewParent()
    {
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, _visibleRoot));
        var handler = PutHandler(Supervisor(), TestGroupWriteVisibility.AllClientsVisible());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PutCommand<GroupResource>(ResourceOf(_visibleGroup, _hiddenGroup)), CancellationToken.None));

        ex.Message.ShouldBe($"New parent group with ID {_hiddenGroup} not found");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Put_DetachingToRootByRestrictedCaller_IsWritten()
    {
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, _visibleRoot));
        _groupRepository.Put(Arg.Any<Group>()).Returns(ci => ci.Arg<Group>());
        var handler = PutHandler(Supervisor(), TestGroupWriteVisibility.AllClientsVisible());

        await handler.Handle(new PutCommand<GroupResource>(ResourceOf(_visibleGroup, null)), CancellationToken.None);

        await _groupRepository.Received(1).Put(Arg.Any<Group>());
    }

    [Test]
    public async Task Put_AddingHiddenClient_IsAnsweredLikeAMissingClient()
    {
        var member = Guid.NewGuid();
        var hiddenClient = Guid.NewGuid();
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, _visibleRoot, member));
        var handler = PutHandler(Supervisor(), TestGroupWriteVisibility.ClientsHidden(hiddenClient));

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(
                new PutCommand<GroupResource>(ResourceOf(_visibleGroup, _visibleRoot, member, hiddenClient)),
                CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {hiddenClient} not found");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Put_RemovingMembersAndKeepingStoredOnes_IsWritten()
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, _visibleRoot, kept, removed));
        _groupRepository.Put(Arg.Any<Group>()).Returns(ci => ci.Arg<Group>());
        var clientGuard = TestGroupWriteVisibility.ClientsHidden(kept, removed);
        var handler = PutHandler(Supervisor(), clientGuard);

        var result = await handler.Handle(
            new PutCommand<GroupResource>(ResourceOf(_visibleGroup, _visibleRoot, kept)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _groupRepository.Received(1).Put(Arg.Any<Group>());
    }

    [Test]
    public async Task Put_MovingUnderVisibleParent_IsWritten()
    {
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, _visibleRoot));
        _groupRepository.Put(Arg.Any<Group>()).Returns(ci => ci.Arg<Group>());
        var otherVisible = Guid.NewGuid();
        var handler = PutHandler(
            RestrictedTo(_visibleRoot, _visibleGroup, otherVisible), TestGroupWriteVisibility.AllClientsVisible());

        await handler.Handle(new PutCommand<GroupResource>(ResourceOf(_visibleGroup, otherVisible)), CancellationToken.None);

        await _groupRepository.Received(1).Put(Arg.Any<Group>());
    }

    [Test]
    public async Task Delete_HiddenGroup_IsAnsweredExactlyLikeAMissingGroup()
    {
        var missing = Guid.NewGuid();
        _groupRepository.Get(missing).Returns((Group?)null);
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, null));
        var restricted = new GroupDeleteHandler(
            _groupRepository, _mapper, Supervisor(), _unitOfWork, NullLogger<GroupDeleteHandler>.Instance);
        var unrestricted = new GroupDeleteHandler(
            _groupRepository, _mapper, TestGroupWriteVisibility.UnrestrictedGroups(), _unitOfWork,
            NullLogger<GroupDeleteHandler>.Instance);

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => restricted.Handle(new DeleteCommand<GroupResource>(_hiddenGroup), CancellationToken.None));
        var absent = await Should.ThrowAsync<KeyNotFoundException>(
            () => unrestricted.Handle(new DeleteCommand<GroupResource>(missing), CancellationToken.None));

        hidden.Message.ShouldBe($"Group with ID {_hiddenGroup} not found.");
        absent.Message.ShouldBe($"Group with ID {missing} not found.");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task DeleteSubtree_HiddenGroup_IsAnsweredLikeAMissingGroup()
    {
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, null));
        var handler = new DeleteGroupSubtreeCommandHandler(
            _groupRepository, Supervisor(), _unitOfWork, NullLogger<DeleteGroupSubtreeCommandHandler>.Instance);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteGroupSubtreeCommand(_hiddenGroup), CancellationToken.None));

        ex.Message.ShouldBe($"Group with ID {_hiddenGroup} not found.");
        await _groupRepository.DidNotReceive().GetChildren(Arg.Any<Guid>());
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Move_HiddenNode_IsAnsweredLikeAMissingNode()
    {
        var handler = new MoveGroupNodeCommandHandler(
            _groupRepository, _mapper, Supervisor(), _unitOfWork, NullLogger<MoveGroupNodeCommandHandler>.Instance);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new MoveGroupNodeCommand(_hiddenGroup, _visibleGroup), CancellationToken.None));

        ex.Message.ShouldBe($"Group to be moved with ID {_hiddenGroup} not found");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Move_OntoHiddenParent_IsAnsweredLikeAMissingNewParent()
    {
        var handler = new MoveGroupNodeCommandHandler(
            _groupRepository, _mapper, Supervisor(), _unitOfWork, NullLogger<MoveGroupNodeCommandHandler>.Instance);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new MoveGroupNodeCommand(_visibleGroup, _hiddenGroup), CancellationToken.None));

        ex.Message.ShouldBe($"New parent group with ID {_hiddenGroup} not found");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task Move_BetweenVisibleGroups_IsWritten()
    {
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, _visibleRoot));
        var handler = new MoveGroupNodeCommandHandler(
            _groupRepository, _mapper, Supervisor(), _unitOfWork, NullLogger<MoveGroupNodeCommandHandler>.Instance);

        await handler.Handle(new MoveGroupNodeCommand(_visibleGroup, _visibleRoot), CancellationToken.None);

        await _groupRepository.Received(1).MoveNode(_visibleGroup, _visibleRoot);
    }

    [Test]
    public async Task SetLocation_HiddenGroup_IsAnsweredLikeAMissingGroup()
    {
        var handler = new SetGroupLocationCommandHandler(
            _groupRepository, Supervisor(), NullLogger<SetGroupLocationCommandHandler>.Instance);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new SetGroupLocationCommand(_hiddenGroup, 46.9, 7.4), CancellationToken.None));

        ex.Message.ShouldBe($"Group with ID {_hiddenGroup} not found");
        await NoGroupWasWrittenAsync();
    }

    [Test]
    public async Task AddSelectedClients_HiddenGroup_IsAnsweredLikeAMissingGroup()
    {
        var clientRepository = Substitute.For<IClientRepository>();
        var groupItemRepository = Substitute.For<IGroupItemRepository>();
        var handler = new AddSelectedClientsToGroupCommandHandler(
            clientRepository, groupItemRepository, _unitOfWork, Substitute.For<ICompanyClock>(),
            Supervisor(), TestGroupWriteVisibility.AllClientsVisible());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(
                new AddSelectedClientsToGroupCommand(_hiddenGroup, "Hidden", [Guid.NewGuid()], null, true, "tester"),
                CancellationToken.None));

        ex.Message.ShouldBe($"Group with ID {_hiddenGroup} not found");
        await clientRepository.DidNotReceive().GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
        await groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task AddSelectedClients_HiddenClient_IsCountedAsNotFoundAndNotAdded()
    {
        var visibleClient = Guid.NewGuid();
        var hiddenClient = Guid.NewGuid();
        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                new() { Id = visibleClient, FirstName = "Max", Name = "Visible", Type = EntityTypeEnum.Employee },
                new() { Id = hiddenClient, FirstName = "Eva", Name = "Hidden", Type = EntityTypeEnum.Employee }
            });
        var groupItemRepository = Substitute.For<IGroupItemRepository>();
        groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        var handler = new AddSelectedClientsToGroupCommandHandler(
            clientRepository, groupItemRepository, _unitOfWork, Substitute.For<ICompanyClock>(),
            Supervisor(), TestGroupWriteVisibility.ClientsHidden(hiddenClient));

        var result = await handler.Handle(
            new AddSelectedClientsToGroupCommand(_visibleGroup, "Visible", [visibleClient, hiddenClient], null, false, "tester"),
            CancellationToken.None);

        result.NotFoundCount.ShouldBe(1);
        result.EligibleCount.ShouldBe(1);
        result.Clients.Select(c => c.Id).ShouldBe([visibleClient]);
    }

    [Test]
    public async Task FillByCriteria_HiddenGroup_IsAnsweredLikeAMissingGroup()
    {
        var searchRepository = Substitute.For<IClientSearchRepository>();
        var groupItemRepository = Substitute.For<IGroupItemRepository>();
        var handler = new FillGroupByCriteriaCommandHandler(
            searchRepository, groupItemRepository, _unitOfWork, Substitute.For<ICompanyClock>(), Supervisor());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(
                new FillGroupByCriteriaCommand(
                    _hiddenGroup, "Hidden", null, null, null, null, null, null, null, null, true, "tester"),
                CancellationToken.None));

        ex.Message.ShouldBe($"Group with ID {_hiddenGroup} not found");
        await groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task GroupUngroupedByCityName_RestrictedCaller_IsForbiddenBeforeAnythingIsRead()
    {
        var clientRepository = Substitute.For<IClientRepository>();
        var groupItemRepository = Substitute.For<IGroupItemRepository>();
        var handler = new GroupUngroupedByCityNameCommandHandler(
            clientRepository, _groupRepository, groupItemRepository, _unitOfWork, Substitute.For<ICompanyClock>(),
            Supervisor());

        await Should.ThrowAsync<ForbiddenException>(
            () => handler.Handle(new GroupUngroupedByCityNameCommand(null, null, true, "tester"), CancellationToken.None));

        await clientRepository.DidNotReceive()
            .GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>());
        await groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task PartitionClientsByAddress_RestrictedCaller_IsForbiddenBeforeAnythingIsRead()
    {
        var clientRepository = Substitute.For<IClientRepository>();
        var groupItemRepository = Substitute.For<IGroupItemRepository>();
        var handler = new PartitionClientsByAddressCommandHandler(
            clientRepository, _groupRepository, groupItemRepository, _unitOfWork, Substitute.For<ICompanyClock>(),
            Substitute.For<ICountryRegionProvider>(), Substitute.For<ICountryResolver>(),
            Substitute.For<IStateRepository>(), Substitute.For<ISettingsReader>(),
            Substitute.For<IGroupVisibilityPreservationService>(), Supervisor());

        await Should.ThrowAsync<ForbiddenException>(
            () => handler.Handle(
                new PartitionClientsByAddressCommand(
                    GroupPartitionLevelEnum.State, [EntityTypeEnum.Employee], null, null, false, null, true, "tester", 0),
                CancellationToken.None));

        await clientRepository.DidNotReceive()
            .GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>());
        await NoGroupWasWrittenAsync();
    }
}
