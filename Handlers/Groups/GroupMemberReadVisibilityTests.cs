// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Group member lists are person data (owner decision 2026-11-07): group rows stay readable for every caller, but a
/// group-restricted caller gets no member items for groups outside their visibility scope - in the group list, the
/// single group, the members endpoint and a single group item (the latter answered like a missing item). Unrestricted
/// callers (admin) keep every member list.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Groups;
using Microsoft.Extensions.Logging.Abstractions;
using GroupGetHandler = Klacks.Api.Application.Handlers.Groups.GetQueryHandler;
using GroupItemGetHandler = Klacks.Api.Application.Handlers.GroupItems.GetQueryHandler;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class GroupMemberReadVisibilityTests
{
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private GroupMapper _mapper = null!;
    private Guid _visibleGroup;
    private Guid _hiddenGroup;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _mapper = new GroupMapper();
        _visibleGroup = Guid.NewGuid();
        _hiddenGroup = Guid.NewGuid();
    }

    private IGroupVisibilityGuard Supervisor()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.GetScopeAsync(Arg.Any<CancellationToken>())
            .Returns(GroupVisibilityScope.Restricted([_visibleGroup], [_visibleGroup]));
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Guid>() == _visibleGroup));
        return guard;
    }

    private static IGroupVisibilityGuard Admin()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.GetScopeAsync(Arg.Any<CancellationToken>()).Returns(GroupVisibilityScope.Unrestricted());
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    private static Group StoredGroup(Guid id, int memberCount) => new()
    {
        Id = id,
        Name = "Group",
        GroupItems = Enumerable.Range(0, memberCount)
            .Select(_ => new GroupItem
            {
                Id = Guid.NewGuid(),
                GroupId = id,
                ClientId = Guid.NewGuid(),
                Client = new Client { Id = Guid.NewGuid(), FirstName = "Anna", Name = "Muster" }
            })
            .ToList()
    };

    private async Task<TruncatedGroupResource> ListAsync(IGroupVisibilityGuard guard)
    {
        var filter = new GroupFilter();
        _groupRepository.Truncated(filter).Returns(new TruncatedGroup
        {
            Groups = [StoredGroup(_visibleGroup, 2), StoredGroup(_hiddenGroup, 3)],
            MaxItems = 2
        });
        var handler = new GetTruncatedListQueryHandler(
            _groupRepository, _mapper, guard, NullLogger<GetTruncatedListQueryHandler>.Instance);

        return await handler.Handle(new GetTruncatedListQuery(filter), CancellationToken.None);
    }

    [Test]
    public async Task List_RestrictedCaller_KeepsMembersOfVisibleGroupAndOmitsThoseOfHiddenGroup()
    {
        var result = await ListAsync(Supervisor());

        result.Groups.Count.ShouldBe(2);
        var visible = result.Groups.Single(g => g.Id == _visibleGroup);
        var hidden = result.Groups.Single(g => g.Id == _hiddenGroup);
        visible.GroupItems.Count.ShouldBe(2);
        visible.GroupItems.ShouldAllBe(item => item.Client != null);
        hidden.Name.ShouldBe("Group");
        hidden.GroupItems.ShouldBeEmpty();
        hidden.ClientsCount.ShouldBe(0);
        result.MaxItems.ShouldBe(2);
    }

    [Test]
    public async Task List_RestrictedCallerWithoutVisibilityRows_OmitsEveryMemberList()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.GetScopeAsync(Arg.Any<CancellationToken>()).Returns(GroupVisibilityScope.Restricted([], []));

        var result = await ListAsync(guard);

        result.Groups.Count.ShouldBe(2);
        result.Groups.ShouldAllBe(group => group.GroupItems.Count == 0);
    }

    [Test]
    public async Task List_Admin_KeepsAllMembers()
    {
        var result = await ListAsync(Admin());

        result.Groups.Single(g => g.Id == _visibleGroup).GroupItems.Count.ShouldBe(2);
        result.Groups.Single(g => g.Id == _hiddenGroup).GroupItems.Count.ShouldBe(3);
    }

    private GroupGetHandler GroupHandler(IGroupVisibilityGuard guard) =>
        new(_groupRepository, _mapper, guard, NullLogger<GroupGetHandler>.Instance);

    [Test]
    public async Task GetGroup_HiddenGroup_ReturnsTheGroupWithoutMembers()
    {
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, 3));

        var result = await GroupHandler(Supervisor())
            .Handle(new GetQuery<GroupResource>(_hiddenGroup), CancellationToken.None);

        result.Id.ShouldBe(_hiddenGroup);
        result.GroupItems.ShouldBeEmpty();
    }

    [Test]
    public async Task GetGroup_VisibleGroup_KeepsMembers()
    {
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, 2));

        var result = await GroupHandler(Supervisor())
            .Handle(new GetQuery<GroupResource>(_visibleGroup), CancellationToken.None);

        result.GroupItems.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetGroup_Admin_KeepsMembersOfAnyGroup()
    {
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, 3));

        var result = await GroupHandler(Admin())
            .Handle(new GetQuery<GroupResource>(_hiddenGroup), CancellationToken.None);

        result.GroupItems.Count.ShouldBe(3);
    }

    private GetGroupMembersQueryHandler MembersHandler(IGroupVisibilityGuard guard) =>
        new(_groupRepository, _mapper, guard);

    [Test]
    public async Task Members_HiddenGroup_IsEmpty()
    {
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, 3));

        var result = await MembersHandler(Supervisor())
            .Handle(new GetGroupMembersQuery(_hiddenGroup), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task Members_VisibleGroup_ReturnsMembers()
    {
        _groupRepository.Get(_visibleGroup).Returns(StoredGroup(_visibleGroup, 2));

        var result = await MembersHandler(Supervisor())
            .Handle(new GetGroupMembersQuery(_visibleGroup), CancellationToken.None);

        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task Members_Admin_ReturnsMembersOfAnyGroup()
    {
        _groupRepository.Get(_hiddenGroup).Returns(StoredGroup(_hiddenGroup, 3));

        var result = await MembersHandler(Admin())
            .Handle(new GetGroupMembersQuery(_hiddenGroup), CancellationToken.None);

        result.Count.ShouldBe(3);
    }

    [Test]
    public async Task Members_MissingGroup_IsNotFound()
    {
        var missing = Guid.NewGuid();
        _groupRepository.Get(missing).Returns((Group?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => MembersHandler(Supervisor()).Handle(new GetGroupMembersQuery(missing), CancellationToken.None));
    }

    private GroupItemGetHandler ItemHandler(IGroupVisibilityGuard guard) =>
        new(_groupItemRepository, _mapper, guard, NullLogger<GroupItemGetHandler>.Instance);

    [Test]
    public async Task GroupItem_OfHiddenGroup_IsAnsweredLikeAMissingItem()
    {
        var hiddenItem = StoredGroup(_hiddenGroup, 1).GroupItems.Single();
        _groupItemRepository.Get(hiddenItem.Id).Returns(hiddenItem);
        var missingId = Guid.NewGuid();
        _groupItemRepository.Get(missingId).Returns((GroupItem?)null);
        var handler = ItemHandler(Supervisor());

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<GroupItemResource>(hiddenItem.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<GroupItemResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe(missing.Message.Replace(missingId.ToString(), hiddenItem.Id.ToString()));
    }

    [Test]
    public async Task GroupItem_OfVisibleGroup_IsReturned()
    {
        var item = StoredGroup(_visibleGroup, 1).GroupItems.Single();
        _groupItemRepository.Get(item.Id).Returns(item);

        var result = await ItemHandler(Supervisor())
            .Handle(new GetQuery<GroupItemResource>(item.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        result.ClientId.ShouldBe(item.ClientId);
    }

    [Test]
    public async Task GroupItem_Admin_IsReturnedForAnyGroup()
    {
        var item = StoredGroup(_hiddenGroup, 1).GroupItems.Single();
        _groupItemRepository.Get(item.Id).Returns(item);

        var result = await ItemHandler(Admin())
            .Handle(new GetQuery<GroupItemResource>(item.Id), CancellationToken.None);

        result.ShouldNotBeNull();
    }
}
