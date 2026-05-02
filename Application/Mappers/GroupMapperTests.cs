using Shouldly;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Results;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Mappers;

[TestFixture]
public class GroupMapperTests
{
    private GroupMapper _mapper = null!;

    [SetUp]
    public void Setup()
    {
        _mapper = new GroupMapper();
    }

    [Test]
    public void ToGroupResource_ValidGroup_MapsBasicProperties()
    {
        // Arrange
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            Description = "Test Description",
            ValidFrom = new DateTime(2024, 1, 1),
            ValidUntil = new DateTime(2024, 12, 31),
            Lft = 1,
            Rgt = 10,
            GroupItems = new List<GroupItem>()
        };

        // Act
        var result = _mapper.ToGroupResource(group);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(group.Id);
        result.Name.ShouldBe("Test Group");
        result.Description.ShouldBe("Test Description");
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
        result.ValidUntil.ShouldBe(new DateTime(2024, 12, 31));
        result.Lft.ShouldBe(1);
        result.Rgt.ShouldBe(10);
    }

    [Test]
    public void ToGroupResources_MultipleGroups_MapsAll()
    {
        // Arrange
        var groups = new List<Group>
        {
            new Group { Id = Guid.NewGuid(), Name = "Group 1", GroupItems = new List<GroupItem>() },
            new Group { Id = Guid.NewGuid(), Name = "Group 2", GroupItems = new List<GroupItem>() },
            new Group { Id = Guid.NewGuid(), Name = "Group 3", GroupItems = new List<GroupItem>() }
        };

        // Act
        var result = _mapper.ToGroupResources(groups);

        // Assert
        result.Count().ShouldBe(3);
        result[0].Name.ShouldBe("Group 1");
        result[1].Name.ShouldBe("Group 2");
        result[2].Name.ShouldBe("Group 3");
    }

    [Test]
    public void ToGroupEntity_ValidResource_MapsCorrectly()
    {
        // Arrange
        var resource = new GroupResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            Description = "Test Description",
            ValidFrom = new DateTime(2024, 1, 1),
            ValidUntil = new DateTime(2024, 12, 31)
        };

        // Act
        var result = _mapper.ToGroupEntity(resource);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(resource.Id);
        result.Name.ShouldBe("Test Group");
        result.Description.ShouldBe("Test Description");
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
        result.ValidUntil.ShouldBe(new DateTime(2024, 12, 31));
    }

    [Test]
    public void ToSimpleGroupResource_ValidGroup_MapsCorrectly()
    {
        // Arrange
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Simple Group",
            Description = "Simple Description",
            ValidFrom = new DateTime(2024, 1, 1)
        };

        // Act
        var result = _mapper.ToSimpleGroupResource(group);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(group.Id);
        result.Name.ShouldBe("Simple Group");
        result.Description.ShouldBe("Simple Description");
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
    }

    [Test]
    public void ToGroupFromSimple_ValidResource_MapsCorrectly()
    {
        // Arrange
        var resource = new SimpleGroupResource
        {
            Id = Guid.NewGuid(),
            Name = "Simple Group",
            Description = "Simple Description",
            ValidFrom = new DateTime(2024, 1, 1),
            ValidUntil = new DateTime(2024, 12, 31)
        };

        // Act
        var result = _mapper.ToGroupFromSimple(resource);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(resource.Id);
        result.Name.ShouldBe("Simple Group");
        result.Description.ShouldBe("Simple Description");
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
        result.ValidUntil.ShouldBe(new DateTime(2024, 12, 31));
    }

    [Test]
    public void ToGroupItemResource_ValidGroupItem_MapsCorrectly()
    {
        // Arrange
        var groupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ValidFrom = new DateTime(2024, 1, 1),
            ValidUntil = new DateTime(2024, 12, 31)
        };

        // Act
        var result = _mapper.ToGroupItemResource(groupItem);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(groupItem.Id);
        result.GroupId.ShouldBe(groupItem.GroupId);
        result.ClientId.ShouldBe(groupItem.ClientId);
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
        result.ValidUntil.ShouldBe(new DateTime(2024, 12, 31));
    }

    [Test]
    public void FromSummary_ValidSummary_MapsToGroupResource()
    {
        // Arrange
        var summary = new GroupSummary
        {
            Name = "Summary Group",
            Description = "Summary Description",
            ValidFrom = new DateOnly(2024, 1, 1),
            ValidTo = new DateOnly(2024, 12, 31),
            LeftValue = 1,
            RightValue = 10
        };

        // Act
        var result = _mapper.FromSummary(summary);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Summary Group");
        result.Description.ShouldBe("Summary Description");
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
        result.ValidUntil.ShouldBe(new DateTime(2024, 12, 31));
        result.Lft.ShouldBe(1);
        result.Rgt.ShouldBe(10);
    }

    [Test]
    public void SummaryToEntity_ValidSummary_MapsToGroup()
    {
        // Arrange
        var summary = new GroupSummary
        {
            Name = "Summary Group",
            Description = "Summary Description",
            ValidFrom = new DateOnly(2024, 1, 1),
            ValidTo = new DateOnly(2024, 12, 31),
            LeftValue = 1,
            RightValue = 10,
            IsActive = true,
            CreateTime = DateTime.UtcNow.AddDays(-10),
            UpdateTime = DateTime.UtcNow
        };

        // Act
        var result = _mapper.SummaryToEntity(summary);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Summary Group");
        result.Description.ShouldBe("Summary Description");
        result.ValidFrom.ShouldBe(new DateTime(2024, 1, 1));
        result.ValidUntil.ShouldBe(new DateTime(2024, 12, 31));
        result.Lft.ShouldBe(1);
        result.Rgt.ShouldBe(10);
        result.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public void ToTruncatedGroup_ValidPagedResult_MapsCorrectly()
    {
        // Arrange
        var summaries = new List<GroupSummary>
        {
            new GroupSummary { Name = "Group 1", IsActive = true },
            new GroupSummary { Name = "Group 2", IsActive = true }
        };

        var pagedResult = new PagedResult<GroupSummary>
        {
            Items = summaries,
            TotalCount = 50,
            PageNumber = 1,
            PageSize = 10
        };

        // Act
        var result = _mapper.ToTruncatedGroup(pagedResult);

        // Assert
        result.ShouldNotBeNull();
        result.Groups.Count().ShouldBe(2);
        result.MaxItems.ShouldBe(50);
        result.MaxPages.ShouldBe(5);
        result.CurrentPage.ShouldBe(1);
    }

    [Test]
    public void ToGroupSearchCriteria_ValidFilter_MapsCorrectly()
    {
        // Arrange
        var filter = new GroupFilter
        {
            FirstItemOnLastPage = 10,
            IsNextPage = true,
            IsPreviousPage = false,
            NumberOfItemsPerPage = 20,
            OrderBy = "name",
            RequiredPage = 2,
            SortOrder = "asc",
            SearchString = "test",
            ActiveDateRange = true,
            FormerDateRange = false,
            FutureDateRange = false
        };

        // Act
        var result = _mapper.ToGroupSearchCriteria(filter);

        // Assert
        result.ShouldNotBeNull();
        result.FirstItemOnLastPage.ShouldBe(10);
        result.IsNextPage.ShouldBe(true);
        result.IsPreviousPage.ShouldBe(false);
        result.NumberOfItemsPerPage.ShouldBe(20);
        result.OrderBy.ShouldBe("name");
        result.RequiredPage.ShouldBe(2);
        result.SortOrder.ShouldBe("asc");
        result.SearchString.ShouldBe("test");
        result.ActiveDateRange.ShouldBeTrue();
        result.FormerDateRange.ShouldBeFalse();
        result.FutureDateRange.ShouldBeFalse();
    }
}
