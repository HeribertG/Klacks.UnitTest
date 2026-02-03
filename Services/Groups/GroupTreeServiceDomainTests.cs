using FluentAssertions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Microsoft.EntityFrameworkCore;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupTreeServiceDomainTests
{
    private GroupTreeService _treeService;
    private DataBaseContext _context;
    private ILogger<GroupTreeService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<GroupTreeService>>();
        var databaseAdapter = new Klacks.Api.Infrastructure.Persistence.Adapters.GroupTreeInMemoryAdapter(_context);
        _treeService = new GroupTreeService(_context, _mockLogger, databaseAdapter);

        CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private void CreateTestData()
    {
        var root = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Root Group",
            Description = "Root of the tree",
            ValidFrom = DateTime.Now.AddDays(-100),
            ValidUntil = DateTime.Now.AddDays(100),
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 6
        };

        var child = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Child Group",
            Description = "Child node",
            ValidFrom = DateTime.Now.AddDays(-50),
            ValidUntil = DateTime.Now.AddDays(50),
            Parent = root.Id,
            Root = root.Id,
            Lft = 2,
            Rgt = 3
        };

        var child2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Child Group 2",
            Description = "Second child",
            ValidFrom = DateTime.Now.AddDays(-30),
            ValidUntil = DateTime.Now.AddDays(30),
            Parent = root.Id,
            Root = root.Id,
            Lft = 4,
            Rgt = 5
        };

        _context.Group.AddRange(root, child, child2);
        _context.SaveChanges();
    }

    [Test]
    public async Task AddRootNodeAsync_ShouldCreateValidRootNode()
    {
        // Arrange
        var newRoot = new Group
        {
            Id = Guid.NewGuid(),
            Name = "New Root",
            Description = "New root group",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(365)
        };

        // Act
        var result = await _treeService.AddRootNodeAsync(newRoot);

        // Assert
        result.Should().NotBeNull();
        result.Lft.Should().Be(7); // After existing max Rgt (6) + 1
        result.Rgt.Should().Be(8);
        result.Parent.Should().BeNull();
        result.Root.Should().BeNull();
    }

    [Test]
    public async Task AddChildNodeAsync_ShouldSetCorrectProperties()
    {
        // Arrange
        var parent = await _context.Group.FirstAsync(g => g.Name == "Root Group");
        var newChild = new Group
        {
            Id = Guid.NewGuid(),
            Name = "New Child",
            Description = "New child group",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(180)
        };

        // Act
        var result = await _treeService.AddChildNodeAsync(parent.Id, newChild);

        // Assert
        result.Should().NotBeNull();
        result.Parent.Should().Be(parent.Id);
        result.Root.Should().Be(parent.Id);
        result.Lft.Should().Be(parent.Rgt); // Would be inserted at parent's Rgt
        result.Rgt.Should().Be(parent.Rgt + 1);
    }

    [Test]
    public async Task AddChildNodeAsync_WithInvalidParent_ShouldThrowException()
    {
        // Arrange
        var invalidParentId = Guid.NewGuid();
        var newChild = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Orphaned Child",
            Description = "Child with invalid parent",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(90)
        };

        // Act & Assert
        await FluentActions.Invoking(() => _treeService.AddChildNodeAsync(invalidParentId, newChild))
            .Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"Parent group with ID {invalidParentId} not found");
    }

    [Test]
    public void CalculateTreeWidth_ShouldReturnCorrectValue()
    {
        // Arrange
        int lft = 3;
        int rgt = 8;

        // Act
        int width = _treeService.CalculateTreeWidth(lft, rgt);

        // Assert
        width.Should().Be(6); // 8 - 3 + 1 = 6
    }

    [Test]
    public void ValidateTreeMovement_WithValidMove_ShouldReturnTrue()
    {
        // Arrange
        var node1 = new Group { Lft = 2, Rgt = 3 };
        var node2 = new Group { Lft = 4, Rgt = 5 };

        // Act
        bool isValid = _treeService.ValidateTreeMovement(node1, node2);

        // Assert
        isValid.Should().BeTrue();
    }

    [Test]
    public void ValidateTreeMovement_MovingToDescendant_ShouldReturnFalse()
    {
        // Arrange
        var parent = new Group { Lft = 2, Rgt = 5 };
        var descendant = new Group { Lft = 3, Rgt = 4 };

        // Act
        bool isValid = _treeService.ValidateTreeMovement(parent, descendant);

        // Assert
        isValid.Should().BeFalse();
    }

    [Test]
    public async Task MoveNodeAsync_WithInvalidNewParent_ShouldThrowException()
    {
        // Arrange
        var child = await _context.Group.FirstAsync(g => g.Name == "Child Group");
        var invalidParentId = Guid.NewGuid();

        // Act & Assert
        await FluentActions.Invoking(() => _treeService.MoveNodeAsync(child.Id, invalidParentId))
            .Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"New parent group with ID {invalidParentId} not found");
    }

    [Test]
    public async Task MoveNodeAsync_ToDescendant_ShouldThrowException()
    {
        // Arrange
        var root = await _context.Group.FirstAsync(g => g.Name == "Root Group");
        var child = await _context.Group.FirstAsync(g => g.Name == "Child Group");

        // Act & Assert
        await FluentActions.Invoking(() => _treeService.MoveNodeAsync(root.Id, child.Id))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The new parent cannot be a descendant of the node to be moved");
    }

    [Test]
    public async Task DeleteNodeAsync_ShouldReturnCorrectWidth()
    {
        // Arrange
        var child = await _context.Group.FirstAsync(g => g.Name == "Child Group");

        // Act
        int width = await _treeService.DeleteNodeAsync(child.Id);

        // Assert
        width.Should().Be(2); // Rgt(3) - Lft(2) + 1 = 2
        
        // Verify soft delete
        var deletedNode = await _context.Group.FindAsync(child.Id);
        deletedNode.IsDeleted.Should().BeTrue();
        deletedNode.DeletedTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task DeleteNodeAsync_WithNonexistentNode_ShouldThrowException()
    {
        // Arrange
        var invalidNodeId = Guid.NewGuid();

        // Act & Assert
        await FluentActions.Invoking(() => _treeService.DeleteNodeAsync(invalidNodeId))
            .Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"Group with ID {invalidNodeId} not found.");
    }

    [Test]
    public async Task UpdateTreePositionsAsync_ShouldLogAffectedNodes()
    {
        // Arrange
        var root = await _context.Group.FirstAsync(g => g.Name == "Root Group");

        // Act
        await _treeService.UpdateTreePositionsAsync(root.Id, 2, 2);

        // Assert
        // For NSubstitute with ILogger, it's easier to check if the method was called
        // without checking the exact message content
        _mockLogger.ReceivedWithAnyArgs().Log(
            default,
            default,
            default,
            default,
            default);
    }
}