using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Services;
using NSubstitute;

namespace Klacks.UnitTest.Services.Clients;

[TestFixture]
public class ClientGroupFilterServiceTests
{
    private ClientGroupFilterService _service;
    private IGetAllClientIdsFromGroupAndSubgroups _mockGroupClient;
    private IGroupVisibilityService _mockGroupVisibility;

    [SetUp]
    public void SetUp()
    {
        _mockGroupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _mockGroupVisibility = Substitute.For<IGroupVisibilityService>();
        _service = new ClientGroupFilterService(_mockGroupClient, _mockGroupVisibility);
    }

    [Test]
    public async Task FilterClientsByGroupId_WithGroupFilter_IncludesClientsWithMatchingGroup()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client with Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = groupId }
                }
            }
        }.AsQueryable();

        _mockGroupClient.GetAllGroupIdsIncludingSubgroups(groupId)
            .Returns(Task.FromResult(new HashSet<Guid> { groupId }));

        // Act
        var result = await _service.FilterClientsByGroupId(groupId, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Should().HaveCount(1);
        resultList.First().Name.Should().Be("Client with Group");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithGroupFilter_IncludesClientsWithoutGroups()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client without Group",
                GroupItems = new List<GroupItem>()
            }
        }.AsQueryable();

        _mockGroupClient.GetAllGroupIdsIncludingSubgroups(groupId)
            .Returns(Task.FromResult(new HashSet<Guid> { groupId }));

        // Act
        var result = await _service.FilterClientsByGroupId(groupId, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Should().HaveCount(1);
        resultList.First().Name.Should().Be("Client without Group");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithGroupFilter_ExcludesClientsWithDifferentGroup()
    {
        // Arrange
        var filterGroupId = Guid.NewGuid();
        var differentGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client with Different Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = differentGroupId }
                }
            }
        }.AsQueryable();

        _mockGroupClient.GetAllGroupIdsIncludingSubgroups(filterGroupId)
            .Returns(Task.FromResult(new HashSet<Guid> { filterGroupId }));

        // Act
        var result = await _service.FilterClientsByGroupId(filterGroupId, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Should().BeEmpty();
    }

    [Test]
    public async Task FilterClientsByGroupId_WithGroupFilter_HandlesMixedClientsCorrectly()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var differentGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client with Matching Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = groupId }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client without Group",
                GroupItems = new List<GroupItem>()
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client with Different Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = differentGroupId }
                }
            }
        }.AsQueryable();

        _mockGroupClient.GetAllGroupIdsIncludingSubgroups(groupId)
            .Returns(Task.FromResult(new HashSet<Guid> { groupId }));

        // Act
        var result = await _service.FilterClientsByGroupId(groupId, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Should().HaveCount(2);
        resultList.Should().Contain(c => c.Name == "Client with Matching Group");
        resultList.Should().Contain(c => c.Name == "Client without Group");
        resultList.Should().NotContain(c => c.Name == "Client with Different Group");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithoutGroupFilter_AndIsAdmin_ReturnsAllClients()
    {
        // Arrange
        var clients = new List<Client>
        {
            new Client { Id = Guid.NewGuid(), Name = "Client 1", GroupItems = new List<GroupItem>() },
            new Client { Id = Guid.NewGuid(), Name = "Client 2", GroupItems = new List<GroupItem>() }
        }.AsQueryable();

        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(true));

        // Act
        var result = await _service.FilterClientsByGroupId(null, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Should().HaveCount(2);
    }

    [Test]
    public async Task FilterClientsByGroupId_WithoutGroupFilter_AndNotAdmin_FiltersBasedOnVisibleRoots()
    {
        // Arrange
        var rootGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client in Visible Root",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = rootGroupId }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client without Group",
                GroupItems = new List<GroupItem>()
            }
        }.AsQueryable();

        _mockGroupVisibility.IsAdmin().Returns(Task.FromResult(false));
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid> { rootGroupId }));
        _mockGroupClient.GetAllGroupIdsIncludingSubgroupsFromList(Arg.Any<List<Guid>>())
            .Returns(Task.FromResult(new HashSet<Guid> { rootGroupId }));

        // Act
        var result = await _service.FilterClientsByGroupId(null, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Should().HaveCount(2);
        resultList.Should().Contain(c => c.Name == "Client in Visible Root");
        resultList.Should().Contain(c => c.Name == "Client without Group");
    }
}
