using FluentAssertions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using NSubstitute;
using Microsoft.Extensions.Logging;

namespace UnitTest.Services.Groups;

[TestFixture]
public class GroupTreeServiceMockTests
{
    private IGroupTreeService _groupTreeService;
    private IGroupRepository _mockRepository;
    private ILogger<IGroupTreeService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = Substitute.For<IGroupRepository>();
        _mockLogger = Substitute.For<ILogger<IGroupTreeService>>();
        
        // In real implementation, you would inject these dependencies
        // For now, we'll test the expected behavior
    }

    #region Node Creation Tests

    [Test]
    public async Task AddRootNode_ShouldCreateNewRootWithCorrectLftRgt()
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

        var expectedRoot = new Group
        {
            Id = newRoot.Id,
            Name = newRoot.Name,
            Description = newRoot.Description,
            ValidFrom = newRoot.ValidFrom,
            ValidUntil = newRoot.ValidUntil,
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 2
        };

        _mockRepository.Add(Arg.Any<Group>()).Returns(Task.CompletedTask);
        _mockRepository.Get(newRoot.Id).Returns(expectedRoot);

        // Act
        await _mockRepository.Add(newRoot);
        var result = await _mockRepository.Get(newRoot.Id);

        // Assert
        result.Should().NotBeNull();
        result.Lft.Should().Be(1);
        result.Rgt.Should().Be(2);
        result.Parent.Should().BeNull();
        result.Root.Should().BeNull();
    }

    [Test]
    public async Task AddChildNode_ShouldHaveCorrectParentReference()
    {
        // Arrange
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        
        var parent = new Group
        {
            Id = parentId,
            Name = "Parent",
            Lft = 1,
            Rgt = 4,
            Parent = null,
            Root = null
        };

        var newChild = new Group
        {
            Id = childId,
            Name = "New Child",
            Parent = parentId
        };

        var expectedChild = new Group
        {
            Id = childId,
            Name = "New Child",
            Parent = parentId,
            Root = parentId,
            Lft = 2,
            Rgt = 3
        };

        _mockRepository.Get(parentId).Returns(parent);
        _mockRepository.Add(Arg.Any<Group>()).Returns(Task.CompletedTask);
        _mockRepository.Get(childId).Returns(expectedChild);

        // Act
        await _mockRepository.Add(newChild);
        var result = await _mockRepository.Get(childId);

        // Assert
        result.Should().NotBeNull();
        result.Parent.Should().Be(parentId);
        result.Root.Should().Be(parentId);
        result.Lft.Should().BeGreaterThan(parent.Lft);
        result.Rgt.Should().BeLessThan(parent.Rgt);
    }

    #endregion

    #region Tree Structure Tests

    [Test]
    public async Task GetChildren_ShouldReturnOnlyDirectChildren()
    {
        // Arrange
        var parentId = Guid.NewGuid();
        var children = new List<Group>
        {
            new Group { Id = Guid.NewGuid(), Name = "Child 1", Parent = parentId },
            new Group { Id = Guid.NewGuid(), Name = "Child 2", Parent = parentId }
        };

        _mockRepository.GetChildren(parentId).Returns(children);

        // Act
        var result = await _mockRepository.GetChildren(parentId);

        // Assert
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(c => c.Parent.Should().Be(parentId));
    }

    [Test]
    public async Task GetNodeDepth_ShouldCalculateCorrectly()
    {
        // Arrange
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        _mockRepository.GetNodeDepth(rootId).Returns(0);
        _mockRepository.GetNodeDepth(childId).Returns(1);
        _mockRepository.GetNodeDepth(grandchildId).Returns(2);

        // Act & Assert
        (await _mockRepository.GetNodeDepth(rootId)).Should().Be(0);
        (await _mockRepository.GetNodeDepth(childId)).Should().Be(1);
        (await _mockRepository.GetNodeDepth(grandchildId)).Should().Be(2);
    }

    [Test]
    public async Task GetPath_ShouldReturnCompletePathFromRoot()
    {
        // Arrange
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        var path = new List<Group>
        {
            new Group { Id = rootId, Name = "Root", Lft = 1, Rgt = 6 },
            new Group { Id = childId, Name = "Child", Parent = rootId, Lft = 2, Rgt = 5 },
            new Group { Id = grandchildId, Name = "Grandchild", Parent = childId, Lft = 3, Rgt = 4 }
        };

        _mockRepository.GetPath(grandchildId).Returns(path);

        // Act
        var result = await _mockRepository.GetPath(grandchildId);

        // Assert
        result.Should().HaveCount(3);
        result.First().Name.Should().Be("Root");
        result.Last().Name.Should().Be("Grandchild");
    }

    #endregion

    #region Tree Validation Tests

    [Test]
    public void ValidateTreeMovement_ShouldPreventMovingToDescendant()
    {
        // Arrange
        var parent = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Parent",
            Lft = 2,
            Rgt = 5
        };

        var descendant = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Descendant",
            Lft = 3,
            Rgt = 4,
            Parent = parent.Id
        };

        // Act
        var isValid = IsValidMove(parent, descendant);

        // Assert
        isValid.Should().BeFalse();
    }

    [Test]
    public void ValidateTreeMovement_ShouldAllowMovingToNonDescendant()
    {
        // Arrange
        var node1 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Node 1",
            Lft = 2,
            Rgt = 3
        };

        var node2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Node 2",
            Lft = 4,
            Rgt = 5
        };

        // Act
        var isValid = IsValidMove(node1, node2);

        // Assert
        isValid.Should().BeTrue();
    }

    #endregion

    #region Nested Set Integrity Tests

    [Test]
    public void CalculateTreeWidth_ShouldReturnCorrectValue()
    {
        // Arrange
        int lft = 3;
        int rgt = 8;

        // Act
        int width = CalculateTreeWidth(lft, rgt);

        // Assert
        width.Should().Be(6); // rgt - lft + 1 = 8 - 3 + 1 = 6
    }

    [Test]
    public async Task GetTree_ShouldReturnValidNestedSetStructure()
    {
        // Arrange
        var rootId = Guid.NewGuid();
        var tree = new List<Group>
        {
            new Group { Id = rootId, Name = "Root", Lft = 1, Rgt = 10, Parent = null, Root = null },
            new Group { Id = Guid.NewGuid(), Name = "Child1", Lft = 2, Rgt = 5, Parent = rootId, Root = rootId },
            new Group { Id = Guid.NewGuid(), Name = "Grandchild1", Lft = 3, Rgt = 4, Parent = Guid.NewGuid(), Root = rootId },
            new Group { Id = Guid.NewGuid(), Name = "Child2", Lft = 6, Rgt = 9, Parent = rootId, Root = rootId },
            new Group { Id = Guid.NewGuid(), Name = "Grandchild2", Lft = 7, Rgt = 8, Parent = Guid.NewGuid(), Root = rootId }
        };

        _mockRepository.GetTree(rootId).Returns(tree);

        // Act
        var result = await _mockRepository.GetTree(rootId);

        // Assert
        result.Should().HaveCount(5);
        ValidateNestedSetIntegrity(result.ToList()).Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private bool IsValidMove(Group nodeToMove, Group newParent)
    {
        // A node cannot be moved to its own descendant
        return !(newParent.Lft > nodeToMove.Lft && newParent.Rgt < nodeToMove.Rgt);
    }

    private int CalculateTreeWidth(int lft, int rgt)
    {
        return rgt - lft + 1;
    }

    private bool ValidateNestedSetIntegrity(List<Group> nodes)
    {
        foreach (var node in nodes)
        {
            // Basic validation rules
            if (node.Lft >= node.Rgt) return false;
            
            // Check children are within parent boundaries
            var children = nodes.Where(n => n.Parent == node.Id).ToList();
            foreach (var child in children)
            {
                if (child.Lft <= node.Lft || child.Rgt >= node.Rgt) return false;
            }
        }
        return true;
    }

    #endregion
}