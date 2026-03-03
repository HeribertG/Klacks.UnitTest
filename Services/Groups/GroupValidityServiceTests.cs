using FluentAssertions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupValidityServiceTests
{
    private DataBaseContext _context;
    private GroupValidityService _validityService;
    private ILogger<GroupValidityService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<GroupValidityService>>();

        _validityService = new GroupValidityService(_context, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithActiveGroups_ShouldReturnActiveGroups()
    {
        // Arrange
        var activeGroupId = Guid.NewGuid();
        var expiredGroupId = Guid.NewGuid();
        var futureGroupId = Guid.NewGuid();

        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = activeGroupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-10),
            ValidUntil = now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var expiredGroup = new Group
        {
            Id = expiredGroupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-20),
            ValidUntil = now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        var futureGroup = new Group
        {
            Id = futureGroupId,
            Name = "Future Group",
            ValidFrom = now.AddDays(5),
            ValidUntil = now.AddDays(15),
            Lft = 5,
            Rgt = 6
        };

        await _context.Group.AddRangeAsync(activeGroup, expiredGroup, futureGroup);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, true, false, false);
        var groups = await result.ToListAsync();

        // Assert
        groups.Should().HaveCount(1);
        groups.Should().Contain(g => g.Id == activeGroupId);
        groups.Should().NotContain(g => g.Id == expiredGroupId);
        groups.Should().NotContain(g => g.Id == futureGroupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithFormerGroups_ShouldReturnExpiredGroups()
    {
        // Arrange
        var activeGroupId = Guid.NewGuid();
        var expiredGroupId = Guid.NewGuid();
        var futureGroupId = Guid.NewGuid();

        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = activeGroupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-10),
            ValidUntil = now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var expiredGroup = new Group
        {
            Id = expiredGroupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-20),
            ValidUntil = now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        var futureGroup = new Group
        {
            Id = futureGroupId,
            Name = "Future Group",
            ValidFrom = now.AddDays(5),
            ValidUntil = now.AddDays(15),
            Lft = 5,
            Rgt = 6
        };

        await _context.Group.AddRangeAsync(activeGroup, expiredGroup, futureGroup);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, false, true, false);
        var groups = await result.ToListAsync();

        // Assert
        groups.Should().HaveCount(1);
        groups.Should().Contain(g => g.Id == expiredGroupId);
        groups.Should().NotContain(g => g.Id == activeGroupId);
        groups.Should().NotContain(g => g.Id == futureGroupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithFutureGroups_ShouldReturnFutureGroups()
    {
        // Arrange
        var activeGroupId = Guid.NewGuid();
        var expiredGroupId = Guid.NewGuid();
        var futureGroupId = Guid.NewGuid();

        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = activeGroupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-10),
            ValidUntil = now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var expiredGroup = new Group
        {
            Id = expiredGroupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-20),
            ValidUntil = now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        var futureGroup = new Group
        {
            Id = futureGroupId,
            Name = "Future Group",
            ValidFrom = now.AddDays(5),
            ValidUntil = now.AddDays(15),
            Lft = 5,
            Rgt = 6
        };

        await _context.Group.AddRangeAsync(activeGroup, expiredGroup, futureGroup);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, false, false, true);
        var groups = await result.ToListAsync();

        // Assert
        groups.Should().HaveCount(1);
        groups.Should().Contain(g => g.Id == futureGroupId);
        groups.Should().NotContain(g => g.Id == activeGroupId);
        groups.Should().NotContain(g => g.Id == expiredGroupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithAllRangesSelected_ShouldReturnAllGroups()
    {
        // Arrange
        var group1Id = Guid.NewGuid();
        var group2Id = Guid.NewGuid();

        var group1 = new Group
        {
            Id = group1Id,
            Name = "Group 1",
            ValidFrom = DateTime.Now.AddDays(-10),
            ValidUntil = DateTime.Now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var group2 = new Group
        {
            Id = group2Id,
            Name = "Group 2",
            ValidFrom = DateTime.Now.AddDays(-20),
            ValidUntil = DateTime.Now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        await _context.Group.AddRangeAsync(group1, group2);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, true, true, true);
        var groups = await result.ToListAsync();

        // Assert
        groups.Should().HaveCount(2);
        groups.Should().Contain(g => g.Id == group1Id);
        groups.Should().Contain(g => g.Id == group2Id);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithNoRangesSelected_ShouldReturnEmptyResult()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            ValidFrom = DateTime.Now.AddDays(-10),
            ValidUntil = DateTime.Now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, false, false, false);
        
        // The service returns Enumerable.Empty<Group>().AsQueryable() which can't be used with EF async operations
        // So we use the synchronous version
        var groups = result.ToList();

        // Assert
        groups.Should().BeEmpty();
    }

    [Test]
    public async Task IsGroupActive_WithActiveGroup_ShouldReturnTrue()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = groupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-5),
            ValidUntil = now.AddDays(5),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(activeGroup);
        await _context.SaveChangesAsync();

        // Act
        var isActive = _validityService.IsGroupActive(activeGroup);

        // Assert
        isActive.Should().BeTrue();
    }

    [Test]
    public async Task IsGroupActive_WithExpiredGroup_ShouldReturnFalse()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var now = DateTime.Now;

        var expiredGroup = new Group
        {
            Id = groupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-15),
            ValidUntil = now.AddDays(-5),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(expiredGroup);
        await _context.SaveChangesAsync();

        // Act
        var isActive = _validityService.IsGroupActive(expiredGroup);

        // Assert
        isActive.Should().BeFalse();
    }

    [Test]
    public async Task ValidateDateRange_WithValidRange_ShouldReturnTrue()
    {
        // Arrange
        var validFrom = DateTime.Now.AddDays(-5);
        var validUntil = DateTime.Now.AddDays(5);

        // Act
        var testGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Lft = 1,
            Rgt = 2
        };
        var isValid = _validityService.ValidateDateRange(testGroup);

        // Assert
        isValid.Should().BeTrue();
    }

    [Test]
    public async Task ValidateDateRange_WithInvalidRange_ShouldReturnFalse()
    {
        // Arrange
        var validFrom = DateTime.Now.AddDays(5);
        var validUntil = DateTime.Now.AddDays(-5);

        // Act
        var testGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Lft = 1,
            Rgt = 2
        };
        var isValid = _validityService.ValidateDateRange(testGroup);

        // Assert
        isValid.Should().BeFalse();
    }
}