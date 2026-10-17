// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// Deleting a group removes the group_item rows of every group in the deleted subtree (client memberships, shift
// assignments and scenario memberships), so the members become group-less and the shifts ungrouped. Items of groups
// outside the subtree stay untouched. Runs the real GroupTreeService on the InMemory adapter.

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.Adapters;
using Klacks.Api.Infrastructure.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupTreeServiceDeleteMembershipsTests
{
    private DataBaseContext _context = null!;
    private GroupTreeService _treeService = null!;

    private readonly Guid _rootId = Guid.NewGuid();
    private readonly Guid _doomedId = Guid.NewGuid();
    private readonly Guid _doomedChildId = Guid.NewGuid();
    private readonly Guid _siblingId = Guid.NewGuid();
    private readonly Guid _otherRootId = Guid.NewGuid();

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _treeService = new GroupTreeService(
            _context, Substitute.For<ILogger<GroupTreeService>>(), new GroupTreeInMemoryAdapter(_context));

        _context.Group.AddRange(
            NewGroup(_rootId, null, null, 1, 8),
            NewGroup(_doomedId, _rootId, _rootId, 2, 5),
            NewGroup(_doomedChildId, _doomedId, _rootId, 3, 4),
            NewGroup(_siblingId, _rootId, _rootId, 6, 7),
            NewGroup(_otherRootId, null, null, 9, 10));
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task DeleteNode_SoftDeletesClientAndShiftItemsOfTheWholeSubtree()
    {
        var clientInDoomed = await AddItemAsync(_doomedId, clientId: Guid.NewGuid());
        var clientInChild = await AddItemAsync(_doomedChildId, clientId: Guid.NewGuid());
        var shiftInChild = await AddItemAsync(_doomedChildId, shiftId: Guid.NewGuid());
        var scenarioInDoomed = await AddItemAsync(_doomedId, clientId: Guid.NewGuid(), analyseToken: Guid.NewGuid());
        var clientInSibling = await AddItemAsync(_siblingId, clientId: Guid.NewGuid());
        var shiftInRoot = await AddItemAsync(_rootId, shiftId: Guid.NewGuid());

        await _treeService.DeleteNodeAsync(_doomedId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        foreach (var id in new[] { clientInDoomed, clientInChild, shiftInChild, scenarioInDoomed })
        {
            var item = await LoadItemAsync(id);
            item.IsDeleted.ShouldBeTrue();
            item.DeletedTime.ShouldNotBeNull();
            item.CurrentUserDeleted.ShouldNotBeNullOrWhiteSpace();
        }

        (await LoadItemAsync(clientInSibling)).IsDeleted.ShouldBeFalse();
        (await LoadItemAsync(shiftInRoot)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteNode_OfARoot_SoftDeletesTheRootTreeAndItsItems_OtherTreeUntouched()
    {
        var clientInRoot = await AddItemAsync(_rootId, clientId: Guid.NewGuid());
        var shiftInSibling = await AddItemAsync(_siblingId, shiftId: Guid.NewGuid());
        var clientInOtherRoot = await AddItemAsync(_otherRootId, clientId: Guid.NewGuid());

        await _treeService.DeleteNodeAsync(_rootId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var deletedGroupIds = await _context.Group.IgnoreQueryFilters()
            .Where(g => g.IsDeleted)
            .Select(g => g.Id)
            .ToListAsync();
        deletedGroupIds.ShouldBe(new[] { _rootId, _doomedId, _doomedChildId, _siblingId }, ignoreOrder: true);

        (await LoadItemAsync(clientInRoot)).IsDeleted.ShouldBeTrue();
        (await LoadItemAsync(shiftInSibling)).IsDeleted.ShouldBeTrue();
        (await LoadItemAsync(clientInOtherRoot)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteNode_LeavesTheMemberGroupless_ButKeepsItsOtherMemberships()
    {
        var clientId = Guid.NewGuid();
        await AddItemAsync(_doomedChildId, clientId: clientId);
        await AddItemAsync(_siblingId, clientId: clientId);
        var grouplessAfterDelete = Guid.NewGuid();
        await AddItemAsync(_doomedId, clientId: grouplessAfterDelete);

        await _treeService.DeleteNodeAsync(_doomedId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _context.GroupItem.Where(gi => gi.ClientId == clientId).Select(gi => gi.GroupId).ToListAsync())
            .ShouldBe(new[] { _siblingId });
        (await _context.GroupItem.AnyAsync(gi => gi.ClientId == grouplessAfterDelete)).ShouldBeFalse();
    }

    private async Task<Guid> AddItemAsync(Guid groupId, Guid? clientId = null, Guid? shiftId = null, Guid? analyseToken = null)
    {
        var item = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId,
            ShiftId = shiftId,
            AnalyseToken = analyseToken
        };
        _context.GroupItem.Add(item);
        await _context.SaveChangesAsync();
        return item.Id;
    }

    private Task<GroupItem> LoadItemAsync(Guid id) =>
        _context.GroupItem.IgnoreQueryFilters().AsNoTracking().SingleAsync(gi => gi.Id == id);

    private static Group NewGroup(Guid id, Guid? parent, Guid? root, int lft, int rgt) => new()
    {
        Id = id,
        Name = $"Group {lft}-{rgt}",
        Description = "Group tree delete test",
        ValidFrom = DateTime.UtcNow.AddYears(-1),
        Parent = parent,
        Root = root,
        Lft = lft,
        Rgt = rgt
    };
}
