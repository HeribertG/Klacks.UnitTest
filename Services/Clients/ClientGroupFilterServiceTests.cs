using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Accounts;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Clients;

[TestFixture]
public class ClientGroupFilterServiceTests
{
    private ClientGroupFilterService _service;
    private IGetAllClientIdsFromGroupAndSubgroups _mockGroupClient;
    private IGroupVisibilityService _mockGroupVisibility;
    private IUserService _mockUser;

    [SetUp]
    public void SetUp()
    {
        _mockGroupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _mockGroupVisibility = Substitute.For<IGroupVisibilityService>();
        _mockGroupVisibility.GetVisibilityScopeAsync().Returns(Task.FromResult(GroupVisibilityScope.Unrestricted()));
        _mockUser = Substitute.For<IUserService>();
        _mockUser.GetIdString().Returns("some-user-id");
        var logger = Substitute.For<ILogger<ClientGroupFilterService>>();
        _service = new ClientGroupFilterService(_mockGroupClient, _mockGroupVisibility, _mockUser, logger);
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
        resultList.Count().ShouldBe(1);
        resultList.First().Name.ShouldBe("Client with Group");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithGroupFilter_ExcludesClientsWithoutGroups()
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
        resultList.ShouldBeEmpty();
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
        resultList.ShouldBeEmpty();
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
        resultList.Count().ShouldBe(1);
        resultList.ShouldContain(c => c.Name == "Client with Matching Group");
        resultList.ShouldNotContain(c => c.Name == "Client without Group");
        resultList.ShouldNotContain(c => c.Name == "Client with Different Group");
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

        _mockGroupVisibility.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(GroupVisibilityScope.Unrestricted()));

        // Act
        var result = await _service.FilterClientsByGroupId(null, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Count().ShouldBe(2);
    }

    [Test]
    public async Task FilterClientsByGroupId_WithoutGroupFilter_AndNotAdmin_KeepsVisibleRootsAndGrouplessClients()
    {
        // Arrange
        var rootGroupId = Guid.NewGuid();
        var otherGroupId = Guid.NewGuid();
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
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client in Other Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = otherGroupId }
                }
            }
        }.AsQueryable();

        _mockGroupVisibility.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(GroupVisibilityScope.Restricted([rootGroupId], [rootGroupId])));

        // Act
        var result = await _service.FilterClientsByGroupId(null, clients);
        var resultList = result.ToList();

        // Assert: clients in a visible group stay, group-less clients stay visible
        // (consistent with the schedule view), clients in non-visible groups are excluded.
        resultList.Count().ShouldBe(2);
        resultList.ShouldContain(c => c.Name == "Client in Visible Root");
        resultList.ShouldContain(c => c.Name == "Client without Group");
        resultList.ShouldNotContain(c => c.Name == "Client in Other Group");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithAGroupTheCallerMayNotSee_ReturnsNothing()
    {
        // Arrange: a restricted caller asks for a group outside their visible scope
        var foreignGroupId = Guid.NewGuid();
        var visibleGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client in Foreign Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = foreignGroupId }
                }
            }
        }.AsQueryable();

        _mockGroupVisibility.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(GroupVisibilityScope.Restricted([visibleGroupId], [visibleGroupId])));
        _mockGroupClient.GetAllGroupIdsIncludingSubgroups(foreignGroupId)
            .Returns(Task.FromResult(new HashSet<Guid> { foreignGroupId }));

        // Act
        var result = await _service.FilterClientsByGroupId(foreignGroupId, clients);

        // Assert
        result.ToList().ShouldBeEmpty(
            "a client-supplied group id must be intersected with the caller's visible groups");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithAnUnresolvableGroup_ReturnsNothingInsteadOfEverything()
    {
        // Arrange: a garbage group id resolves to no groups at all
        var unknownGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client { Id = Guid.NewGuid(), Name = "Client 1", GroupItems = new List<GroupItem>() },
            new Client { Id = Guid.NewGuid(), Name = "Client 2", GroupItems = new List<GroupItem>() }
        }.AsQueryable();

        _mockGroupClient.GetAllGroupIdsIncludingSubgroups(unknownGroupId)
            .Returns(Task.FromResult(new HashSet<Guid>()));

        // Act
        var result = await _service.FilterClientsByGroupId(unknownGroupId, clients);

        // Assert
        result.ToList().ShouldBeEmpty(
            "an unresolvable group used to skip the filter entirely and returned every client");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithoutGroupFilter_AndNoVisibleGroups_ReturnsOnlyGrouplessClients()
    {
        // Arrange: a restricted caller with an empty visibility configuration
        var someGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client in Some Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = someGroupId }
                }
            },
            new Client { Id = Guid.NewGuid(), Name = "Client without Group", GroupItems = new List<GroupItem>() }
        }.AsQueryable();

        _mockGroupVisibility.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(GroupVisibilityScope.Restricted([], [])));

        // Act
        var result = await _service.FilterClientsByGroupId(null, clients);
        var resultList = result.ToList();

        // Assert
        resultList.Count.ShouldBe(1);
        resultList.ShouldContain(c => c.Name == "Client without Group");
        resultList.ShouldNotContain(c => c.Name == "Client in Some Group",
            "an empty visibility configuration used to leave the query unfiltered");
    }

    [Test]
    public async Task FilterClientsByGroupId_WithoutAUser_ReturnsAllClients()
    {
        // Arrange: background services (PeriodHoursBackgroundService,
        // ThoroughRecalculationBackgroundService) run in their own scope without an HTTP user.
        // There is nobody whose visibility could apply, so the nightly recalculation must still
        // see every client - otherwise it silently skips everyone who is in a group.
        var someGroupId = Guid.NewGuid();
        var clients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Client in Some Group",
                GroupItems = new List<GroupItem>
                {
                    new GroupItem { Id = Guid.NewGuid(), GroupId = someGroupId }
                }
            },
            new Client { Id = Guid.NewGuid(), Name = "Client without Group", GroupItems = new List<GroupItem>() }
        }.AsQueryable();

        _mockUser.GetIdString().Returns((string?)null);
        _mockGroupVisibility.GetVisibilityScopeAsync()
            .Returns(Task.FromResult(GroupVisibilityScope.Restricted([], [])));

        // Act
        var result = await _service.FilterClientsByGroupId(null, clients);

        // Assert
        result.ToList().Count.ShouldBe(2,
            "a background job has no user, so it must not be treated as a restricted one");
    }
}
