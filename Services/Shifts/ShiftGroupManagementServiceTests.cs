using Shouldly;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftGroupManagementServiceTests
{
    private DataBaseContext _context;
    private ShiftGroupManagementService _groupManagementService;
    private ILogger<ShiftGroupManagementService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<ShiftGroupManagementService>>();

        _groupManagementService = new ShiftGroupManagementService(_context, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithNewGroups_ShouldAddGroupItems()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();

        var actualGroupIds = new List<Guid> { groupId1, groupId2 };

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        groupItems.Count().ShouldBe(2);
        groupItems.ShouldContain(gi => gi.GroupId == groupId1);
        groupItems.ShouldContain(gi => gi.GroupId == groupId2);
        foreach (var _item in groupItems) { _item.ShiftId.ShouldBe(shiftId); };
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithExistingGroups_ShouldRemoveOldGroups()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var oldGroupId = Guid.NewGuid();
        var newGroupId = Guid.NewGuid();

        // Add existing group item
        var existingGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            GroupId = oldGroupId
        };

        await _context.GroupItem.AddAsync(existingGroupItem);
        await _context.SaveChangesAsync();

        var actualGroupIds = new List<Guid> { newGroupId };

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        groupItems.Count().ShouldBe(1);
        groupItems.ShouldContain(gi => gi.GroupId == newGroupId);
        groupItems.ShouldNotContain(gi => gi.GroupId == oldGroupId);
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithMixedChanges_ShouldUpdateCorrectly()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var keepGroupId = Guid.NewGuid();
        var removeGroupId = Guid.NewGuid();
        var addGroupId = Guid.NewGuid();

        // Add existing group items
        var existingGroupItems = new[]
        {
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                GroupId = keepGroupId
            },
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                GroupId = removeGroupId
            }
        };

        await _context.GroupItem.AddRangeAsync(existingGroupItems);
        await _context.SaveChangesAsync();

        var actualGroupIds = new List<Guid> { keepGroupId, addGroupId };

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        groupItems.Count().ShouldBe(2);
        groupItems.ShouldContain(gi => gi.GroupId == keepGroupId);
        groupItems.ShouldContain(gi => gi.GroupId == addGroupId);
        groupItems.ShouldNotContain(gi => gi.GroupId == removeGroupId);
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithEmptyGroupIds_ShouldRemoveAllGroups()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();

        // Add existing group items
        var existingGroupItems = new[]
        {
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                GroupId = groupId1
            },
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                GroupId = groupId2
            }
        };

        await _context.GroupItem.AddRangeAsync(existingGroupItems);
        await _context.SaveChangesAsync();

        var actualGroupIds = new List<Guid>(); // Empty list

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        groupItems.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithNoChanges_ShouldNotModifyDatabase()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();

        // Add existing group items
        var existingGroupItems = new[]
        {
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                GroupId = groupId1
            },
            new GroupItem
            {
                Id = Guid.NewGuid(),
                ShiftId = shiftId,
                GroupId = groupId2
            }
        };

        await _context.GroupItem.AddRangeAsync(existingGroupItems);
        await _context.SaveChangesAsync();

        var actualGroupIds = new List<Guid> { groupId1, groupId2 }; // Same as existing

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        groupItems.Count().ShouldBe(2);
        groupItems.ShouldContain(gi => gi.GroupId == groupId1);
        groupItems.ShouldContain(gi => gi.GroupId == groupId2);
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithNonExistentShift_ShouldCreateNewGroupItems()
    {
        // Arrange
        var nonExistentShiftId = Guid.NewGuid();
        var groupId1 = Guid.NewGuid();

        var actualGroupIds = new List<Guid> { groupId1 };

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(nonExistentShiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == nonExistentShiftId)
            .ToListAsync();

        groupItems.Count().ShouldBe(1);
        groupItems.First().GroupId.ShouldBe(groupId1);
        groupItems.First().ShiftId.ShouldBe(nonExistentShiftId);
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithDuplicateGroupIds_ShouldHandleGracefully()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        var actualGroupIds = new List<Guid> { groupId, groupId }; // Duplicate IDs

        // Act
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, actualGroupIds);

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        // Should handle duplicates gracefully (exact behavior depends on implementation)
        groupItems.ShouldNotBeEmpty();
        foreach (var _item in groupItems) { _item.GroupId.ShouldBe(groupId); };
    }

    [Test]
    public async Task UpdateGroupItemsAsync_WithLargeDataSet_ShouldPerformEfficiently()
    {
        // Arrange
        var shiftId = Guid.NewGuid();
        var largeGroupIdList = new List<Guid>();

        // Create 100 group IDs
        for (int i = 0; i < 100; i++)
        {
            largeGroupIdList.Add(Guid.NewGuid());
        }

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _groupManagementService.UpdateGroupItemsAsync(shiftId, largeGroupIdList);
        stopwatch.Stop();

        // Assert
        var groupItems = await _context.GroupItem
            .Where(gi => gi.ShiftId == shiftId)
            .ToListAsync();

        groupItems.Count().ShouldBe(100);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(1000); // Should complete within 1 second
    }
}