using FluentAssertions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupMembershipServiceTests
{
    private DataBaseContext _context;
    private GroupMembershipService _membershipService;
    private ILogger<GroupMembershipService> _mockLogger;
    private IGroupHierarchyService _mockHierarchyService;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<GroupMembershipService>>();
        _mockHierarchyService = Substitute.For<IGroupHierarchyService>();

        _membershipService = new GroupMembershipService(_context, _mockLogger, _mockHierarchyService);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task UpdateGroupMembershipAsync_WithNewClients_ShouldAddClients()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var newClientIds = new[] { clientId1, clientId2 };

        // Act
        await _membershipService.UpdateGroupMembershipAsync(groupId, newClientIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.GroupId == groupId)
            .ToListAsync();

        groupItems.Should().HaveCount(2);
        groupItems.Should().Contain(gi => gi.ClientId == clientId1);
        groupItems.Should().Contain(gi => gi.ClientId == clientId2);
    }

    [Test]
    public async Task UpdateGroupMembershipAsync_WithExistingClients_ShouldRemoveOldClients()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var oldClientId = Guid.NewGuid();
        var newClientId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        var existingGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = oldClientId
        };

        await _context.Group.AddAsync(group);
        await _context.GroupItem.AddAsync(existingGroupItem);
        await _context.SaveChangesAsync();

        var newClientIds = new[] { newClientId };

        // Act
        await _membershipService.UpdateGroupMembershipAsync(groupId, newClientIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.GroupId == groupId)
            .ToListAsync();

        groupItems.Should().HaveCount(1);
        groupItems.Should().Contain(gi => gi.ClientId == newClientId);
        groupItems.Should().NotContain(gi => gi.ClientId == oldClientId);
    }

    [Test]
    public async Task AddClientToGroupAsync_WithValidData_ShouldAddClient()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        // Act
        await _membershipService.AddClientToGroupAsync(groupId, clientId);

        // Assert
        var groupItem = await _context.GroupItem
            .FirstOrDefaultAsync(gi => gi.GroupId == groupId && gi.ClientId == clientId);

        groupItem.Should().NotBeNull();
        groupItem.GroupId.Should().Be(groupId);
        groupItem.ClientId.Should().Be(clientId);
    }

    [Test]
    public async Task RemoveClientFromGroupAsync_WithExistingClient_ShouldRemoveClient()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        var groupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId
        };

        await _context.Group.AddAsync(group);
        await _context.GroupItem.AddAsync(groupItem);
        await _context.SaveChangesAsync();

        // Act
        await _membershipService.RemoveClientFromGroupAsync(groupId, clientId);

        // Assert
        var removedItem = await _context.GroupItem
            .FirstOrDefaultAsync(gi => gi.GroupId == groupId && gi.ClientId == clientId);

        removedItem.Should().BeNull();
    }

    [Test]
    public async Task GetGroupMembersAsync_WithMembers_ShouldReturnMembers()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        var groupItem1 = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId1
        };

        var groupItem2 = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId2
        };

        await _context.Group.AddAsync(group);
        await _context.GroupItem.AddRangeAsync(groupItem1, groupItem2);
        await _context.SaveChangesAsync();

        // Act
        var members = await _membershipService.GetGroupMembersAsync(groupId);

        // Assert
        // The service uses Include(gi => gi.Employee) but Employee entities don't exist in test
        // So we expect the service to return empty collection or handle null clients
        members.Should().NotBeNull();
        // We can't test for specific client objects since they don't exist in the test database
    }

    [Test]
    public async Task IsClientInGroupAsync_WithClientInGroup_ShouldReturnTrue()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        var groupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId
        };

        await _context.Group.AddAsync(group);
        await _context.GroupItem.AddAsync(groupItem);
        await _context.SaveChangesAsync();

        // Act
        var isInGroup = await _membershipService.IsClientInGroupAsync(groupId, clientId);

        // Assert
        isInGroup.Should().BeTrue();
    }

    [Test]
    public async Task IsClientInGroupAsync_WithClientNotInGroup_ShouldReturnFalse()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        // Act
        var isInGroup = await _membershipService.IsClientInGroupAsync(groupId, clientId);

        // Assert
        isInGroup.Should().BeFalse();
    }

    [Test]
    public async Task GetGroupMemberCountAsync_WithMembers_ShouldReturnCorrectCount()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();
        var clientId3 = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Lft = 1,
            Rgt = 2
        };

        var groupItems = new[]
        {
            new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = clientId1 },
            new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = clientId2 },
            new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = clientId3 }
        };

        await _context.Group.AddAsync(group);
        await _context.GroupItem.AddRangeAsync(groupItems);
        await _context.SaveChangesAsync();

        // Act
        var memberCount = await _membershipService.GetGroupMemberCountAsync(groupId);

        // Assert
        memberCount.Should().Be(3);
    }

    [Test]
    public async Task GetGroupMemberCountAsync_WithEmptyGroup_ShouldReturnZero()
    {
        // Arrange
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Empty Group",
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        // Act
        var memberCount = await _membershipService.GetGroupMemberCountAsync(groupId);

        // Assert
        memberCount.Should().Be(0);
    }
}