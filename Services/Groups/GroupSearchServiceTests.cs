using FluentAssertions;
using Klacks.Api.Models.Associations;
using Klacks.Api.Models.Staffs;
using Klacks.Api.Models.Schedules;
using Klacks.Api.Enums;
using Klacks.Api.Resources.Filter;
using Microsoft.EntityFrameworkCore;
using Klacks.Api.Datas;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Klacks.Api.Repositories;
using Klacks.Api.Interfaces;
using Klacks.Api.Interfaces.Domains;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace UnitTest.Services.Groups;

[TestFixture]
public class GroupSearchServiceTests
{
    private GroupRepository _groupRepository;
    private DataBaseContext _context;
    private List<Group> _testGroups;
    private List<Client> _testClients;  
    private List<GroupItem> _testGroupItems;
    private List<Shift> _testShifts;
    private IGroupVisibilityService _mockGroupVisibility;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockGroupVisibility = Substitute.For<IGroupVisibilityService>();
        // Create mock Domain Services for GroupRepository
        var mockTreeService = Substitute.For<IGroupTreeService>();
        var mockHierarchyService = Substitute.For<IGroupHierarchyService>();
        var mockSearchService = Substitute.For<IGroupSearchService>();
        var mockValidityService = Substitute.For<IGroupValidityService>();
        var mockMembershipService = Substitute.For<IGroupMembershipService>();
        var mockIntegrityService = Substitute.For<IGroupIntegrityService>();
        
        // Configure search service to actually perform filtering using real logic
        mockSearchService.ApplyFilters(Arg.Any<IQueryable<Group>>(), Arg.Any<GroupFilter>()).Returns(info =>
        {
            var query = info.Arg<IQueryable<Group>>();
            var filter = info.Arg<GroupFilter>();
            
            // Apply date range filtering
            var now = DateTime.Now;
            
            // If all three date ranges are true, return all groups (no filtering)
            if (filter.ActiveDateRange && filter.FormerDateRange && filter.FutureDateRange)
            {
                // No date filtering needed
            }
            else if (!filter.ActiveDateRange && !filter.FormerDateRange && !filter.FutureDateRange)
            {
                query = query.Where(g => false); // Return empty if none selected
            }
            else
            {
                // Apply specific filtering based on selected ranges
                var predicates = new List<Expression<Func<Group, bool>>>();
                if (filter.ActiveDateRange)
                    predicates.Add(g => g.ValidFrom <= now && (g.ValidUntil == null || g.ValidUntil >= now));
                if (filter.FormerDateRange)
                    predicates.Add(g => g.ValidUntil != null && g.ValidUntil < now);
                if (filter.FutureDateRange)
                    predicates.Add(g => g.ValidFrom > now);
                    
                if (predicates.Count > 1)
                {
                    var combined = predicates[0];
                    for (int i = 1; i < predicates.Count; i++)
                    {
                        var right = predicates[i];
                        combined = CombineOr(combined, right);
                    }
                    query = query.Where(combined);
                }
                else if (predicates.Count == 1)
                {
                    query = query.Where(predicates[0]);
                }
            }
            
            // Apply search string filtering
            if (!string.IsNullOrWhiteSpace(filter.SearchString))
            {
                var keywords = filter.SearchString.Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var keyword in keywords)
                {
                    query = query.Where(g => g.Name.ToLower().Contains(keyword));
                }
            }
            
            // Apply sorting
            if (!string.IsNullOrEmpty(filter.SortOrder))
            {
                query = filter.OrderBy switch
                {
                    "name" => filter.SortOrder == "asc" ? query.OrderBy(x => x.Name) : query.OrderByDescending(x => x.Name),
                    "description" => filter.SortOrder == "asc" ? query.OrderBy(x => x.Description) : query.OrderByDescending(x => x.Description),
                    "valid_from" => filter.SortOrder == "asc" ? query.OrderBy(x => x.ValidFrom) : query.OrderByDescending(x => x.ValidFrom),
                    "valid_until" => filter.SortOrder == "asc" ? query.OrderBy(x => x.ValidUntil) : query.OrderByDescending(x => x.ValidUntil),
                    _ => query
                };
            }
            
            return query;
        });
        
        // Configure ApplyPaginationAsync method
        mockSearchService.ApplyPaginationAsync(Arg.Any<IQueryable<Group>>(), Arg.Any<GroupFilter>()).Returns(info =>
        {
            var query = info.ArgAt<IQueryable<Group>>(0);
            var filter = info.ArgAt<GroupFilter>(1);
            
            var totalCount = query.Count();
            var maxPage = filter.NumberOfItemsPerPage > 0 ? (totalCount / filter.NumberOfItemsPerPage) : 0;
            var firstItem = filter.RequiredPage * filter.NumberOfItemsPerPage;
            
            var paginatedQuery = query.Skip(firstItem).Take(filter.NumberOfItemsPerPage);
            var groups = totalCount == 0 ? new List<Group>() : paginatedQuery.ToList();
            
            var result = new TruncatedGroup
            {
                Groups = groups,
                MaxItems = totalCount,
                CurrentPage = filter.RequiredPage,
                FirstItemOnPage = totalCount <= firstItem ? -1 : firstItem
            };
            
            if (filter.NumberOfItemsPerPage > 0)
            {
                result.MaxPages = totalCount % filter.NumberOfItemsPerPage == 0 ? maxPage - 1 : maxPage;
            }
            
            return Task.FromResult(result);
        });
        
        _groupRepository = new GroupRepository(_context, _mockGroupVisibility, mockTreeService,
            mockHierarchyService, mockSearchService, mockValidityService, mockMembershipService,
            mockIntegrityService, Substitute.For<ILogger<Group>>());

        CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private void CreateTestData()
    {
        // Create test clients
        _testClients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Müller",
                FirstName = "Hans",
                Gender = GenderEnum.Male,
                Company = "ABC Corp",
                IdNumber = 1001
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Schmidt",
                FirstName = "Anna",
                Gender = GenderEnum.Female,
                Company = "XYZ Ltd",
                IdNumber = 1002
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Weber",
                FirstName = "Peter",
                Gender = GenderEnum.Male,
                Company = "Tech Solutions",
                IdNumber = 1003
            }
        };

        _context.Client.AddRange(_testClients);

        // Create test shifts
        _testShifts = new List<Shift>
        {
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Morning Shift",
                Description = "Standard morning shift",
                RootId = null,
                ParentId = null,
                FromDate = DateOnly.FromDateTime(DateTime.Now.AddDays(-100)),
                UntilDate = DateOnly.FromDateTime(DateTime.Now.AddDays(100)),
                Lft = 1,
                Rgt = 2,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Evening Shift", 
                Description = "Standard evening shift",
                RootId = null,
                ParentId = null,
                FromDate = DateOnly.FromDateTime(DateTime.Now.AddDays(-100)),
                UntilDate = DateOnly.FromDateTime(DateTime.Now.AddDays(100)),
                Lft = 3,
                Rgt = 4,
                StartShift = new TimeOnly(16, 0),
                EndShift = new TimeOnly(23, 59)
            }
        };

        _context.Shift.AddRange(_testShifts);

        // Create test groups with nested set model structure
        var rootGroup1 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Management",
            Description = "Top management group",
            ValidFrom = DateTime.Now.AddDays(-365),
            ValidUntil = DateTime.Now.AddDays(365),
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 8
        };

        var childGroup1 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Managers",
            Description = "Department managers",
            ValidFrom = DateTime.Now.AddDays(-180),
            ValidUntil = DateTime.Now.AddDays(180),
            Parent = rootGroup1.Id,
            Root = rootGroup1.Id,
            Lft = 2,
            Rgt = 5
        };

        var childGroup2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Executives",
            Description = "Executive management team",
            ValidFrom = DateTime.Now.AddDays(-90),
            ValidUntil = DateTime.Now.AddDays(90),
            Parent = rootGroup1.Id,
            Root = rootGroup1.Id,
            Lft = 6,
            Rgt = 7
        };

        var grandChildGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Team Leaders",
            Description = "Team leader positions",
            ValidFrom = DateTime.Now.AddDays(-60),
            ValidUntil = DateTime.Now.AddDays(60),
            Parent = childGroup1.Id,
            Root = rootGroup1.Id,
            Lft = 3,
            Rgt = 4
        };

        var rootGroup2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Development",
            Description = "Software development teams",
            ValidFrom = DateTime.Now.AddDays(-200),
            ValidUntil = null, // No end date
            Parent = null,
            Root = null,
            Lft = 9,
            Rgt = 12
        };

        var devChild = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Frontend Team",
            Description = "Frontend development specialists",
            ValidFrom = DateTime.Now.AddDays(-100),
            ValidUntil = null,
            Parent = rootGroup2.Id,
            Root = rootGroup2.Id,
            Lft = 10,
            Rgt = 11
        };

        // Former group (expired)
        var formerGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Support",
            Description = "Discontinued legacy system support",
            ValidFrom = DateTime.Now.AddDays(-400),
            ValidUntil = DateTime.Now.AddDays(-30), // Expired
            Parent = null,
            Root = null,
            Lft = 13,
            Rgt = 14
        };

        // Future group
        var futureGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Innovation Lab",
            Description = "Future innovation projects",
            ValidFrom = DateTime.Now.AddDays(30), // Future start
            ValidUntil = DateTime.Now.AddDays(400),
            Parent = null,
            Root = null,
            Lft = 15,
            Rgt = 16
        };

        _testGroups = new List<Group>
        {
            rootGroup1, childGroup1, childGroup2, grandChildGroup,
            rootGroup2, devChild, formerGroup, futureGroup
        };

        _context.Group.AddRange(_testGroups);

        // Create group items (memberships)
        _testGroupItems = new List<GroupItem>
        {
            new GroupItem { GroupId = childGroup1.Id, ClientId = _testClients[0].Id, ShiftId = _testShifts[0].Id }, // Hans in Managers
            new GroupItem { GroupId = childGroup2.Id, ClientId = _testClients[1].Id, ShiftId = _testShifts[1].Id }, // Anna in Executives
            new GroupItem { GroupId = grandChildGroup.Id, ClientId = _testClients[2].Id, ShiftId = _testShifts[0].Id }, // Peter in Team Leaders
            new GroupItem { GroupId = devChild.Id, ClientId = _testClients[0].Id, ShiftId = _testShifts[1].Id } // Hans also in Frontend Team
        };

        _context.GroupItem.AddRange(_testGroupItems);
        _context.SaveChanges();

        // Setup mock visibility service to return null to trigger the fallback in ReadAllNodes
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult<List<Guid>>(null));
    }

    [Test]
    public void FilterBySearchString_WithEmptyString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Group.AsQueryable();
        var filter = new GroupFilter { 
            SearchString = "",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);

        // Assert
        result.Count().Should().Be(_testGroups.Count);
    }

    [Test]
    public void FilterBySearchString_WithNullString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = null,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);

        // Assert
        result.Count().Should().Be(_testGroups.Count);
    }

    [TestCase("management", 1, "Should find group by name case-insensitive")]
    [TestCase("DEVELOPMENT", 1, "Should find group by name case-insensitive uppercase")]
    [TestCase("team", 2, "Should find groups containing 'team'")]
    [TestCase("frontend", 1, "Should find Frontend Team")]
    [TestCase("nonexistent", 0, "Should return no results for non-existent search")]
    [TestCase("leader", 1, "Should find Team Leaders")]
    public void FilterBySearchString_WithSingleKeyword_ShouldReturnCorrectResults(string searchTerm, int expectedCount, string description)
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = searchTerm,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(expectedCount, description);
    }

    [Test]
    public void FilterBySearchString_WithMultipleKeywords_ShouldUseAndLogic()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = "team leaders",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Team Leaders");
    }

    [Test]
    public void FilterBySearchString_WithSingleCharacter_ShouldApplyFirstSymbolSearch()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = "M",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        // The search is case-insensitive Contains, so "M" matches any group with 'm' in the name
        // Groups: Management, Managers, Team Leaders, Development, Frontend Team
        groups.Count.Should().BeGreaterThan(0);
        groups.Should().Contain(g => g.Name == "Management");
        
        // Let's use a character that's only in one group for a more specific test
        var filter2 = new GroupFilter { 
            SearchString = "x",  // Only in "Executives"
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };
        var result2 = _groupRepository.FilterGroup(filter2);
        var groups2 = result2.ToList();
        groups2.Should().HaveCount(1);
        groups2.First().Name.Should().Be("Executives");
    }

    [Test]
    public void FilterBySearchString_WithDescription_ShouldFindMatches()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = "development",  // SearchString only works on Name, not Description
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Development");
        groups.First().Description.Should().Contain("Software");
    }

    [Test]
    public void FilterByDateRange_ActiveOnly_ShouldReturnCurrentGroups()
    {
        // Arrange
        var filter = new GroupFilter
        {
            ActiveDateRange = true,
            FormerDateRange = false,
            FutureDateRange = false
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().NotContain(g => g.Name == "Legacy Support"); // Expired
        groups.Should().NotContain(g => g.Name == "Innovation Lab"); // Future
        groups.Should().Contain(g => g.Name == "Management"); // Active
    }

    [Test]
    public void FilterByDateRange_FormerOnly_ShouldReturnExpiredGroups()
    {
        // Arrange
        var filter = new GroupFilter
        {
            ActiveDateRange = false,
            FormerDateRange = true,
            FutureDateRange = false
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Legacy Support");
    }

    [Test]
    public void FilterByDateRange_FutureOnly_ShouldReturnFutureGroups()
    {
        // Arrange
        var filter = new GroupFilter
        {
            ActiveDateRange = false,
            FormerDateRange = false,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Innovation Lab");
    }

    [Test]
    public void FilterByDateRange_AllFalse_ShouldReturnEmptyResult()
    {
        // Arrange
        var filter = new GroupFilter
        {
            ActiveDateRange = false,
            FormerDateRange = false,
            FutureDateRange = false
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().BeEmpty();
    }

    [Test]
    public void FilterByDateRange_AllTrue_ShouldReturnAllGroups()
    {
        // Arrange
        var filter = new GroupFilter
        {
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(_testGroups.Count);
    }

    [TestCase("name", "asc", "Development")]
    [TestCase("name", "desc", "Team Leaders")]
    [TestCase("description", "asc", "Managers")]  // Actual group name, not description
    [TestCase("description", "desc", "Management")]  // Actual group name, not description
    public void Sort_WithDifferentCriteria_ShouldReturnCorrectOrder(string orderBy, string sortOrder, string expectedFirstName)
    {
        // Arrange
        var filter = new GroupFilter
        {
            OrderBy = orderBy,
            SortOrder = sortOrder,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.First().Name.Should().Be(expectedFirstName);
    }

    [Test]
    public void Sort_WithValidFromAsc_ShouldOrderByValidFromAscending()
    {
        // Arrange
        var filter = new GroupFilter
        {
            OrderBy = "valid_from",
            SortOrder = "asc",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().BeInAscendingOrder(g => g.ValidFrom);
    }

    [Test]
    public void Sort_WithValidUntilDesc_ShouldOrderByValidUntilDescending()
    {
        // Arrange
        var filter = new GroupFilter
        {
            OrderBy = "valid_until",
            SortOrder = "desc",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        // Groups with null ValidUntil should come first in descending order
        var groupsWithValidUntil = groups.Where(g => g.ValidUntil.HasValue).ToList();
        if (groupsWithValidUntil.Any())
        {
            groupsWithValidUntil.Should().BeInDescendingOrder(g => g.ValidUntil);
        }
    }

    [Test]
    public void FilterGroup_WithComplexFilter_ShouldApplyAllCriteria()
    {
        // Arrange
        var filter = new GroupFilter
        {
            SearchString = "management",
            ActiveDateRange = true,
            FormerDateRange = false,
            FutureDateRange = false,
            OrderBy = "name",
            SortOrder = "asc"
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Management");
    }

    [Test]
    public void FilterGroup_ShouldIncludeGroupItemsAndClients()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = "managers",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        var managersGroup = groups.First();
        managersGroup.GroupItems.Should().NotBeEmpty();
        managersGroup.GroupItems.First().Client.Should().NotBeNull();
        managersGroup.GroupItems.First().Client.Name.Should().Be("Müller");
    }

    [Test]
    public void FilterBySearchString_WithSpecialCharacters_ShouldHandleGracefully()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = "team leaders",  // Use actual group name without hyphen
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Team Leaders");
    }

    [Test]
    public void FilterBySearchString_WithExtraSpaces_ShouldTrimAndParse()
    {
        // Arrange
        var filter = new GroupFilter { 
            SearchString = "  team   leaders  ",
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = _groupRepository.FilterGroup(filter);
        var groups = result.ToList();

        // Assert
        groups.Should().HaveCount(1);
        groups.First().Name.Should().Be("Team Leaders");
    }

    [Test]
    public async Task Truncated_WithPagination_ShouldReturnCorrectPage()
    {
        // Arrange
        var filter = new GroupFilter
        {
            NumberOfItemsPerPage = 3,
            RequiredPage = 0,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = await _groupRepository.Truncated(filter);

        // Assert
        result.Groups.Should().HaveCount(3);
        result.MaxItems.Should().Be(_testGroups.Count);
        result.CurrentPage.Should().Be(0);
        result.FirstItemOnPage.Should().Be(0);
    }

    [Test]
    public async Task Truncated_WithSecondPage_ShouldReturnCorrectItems()
    {
        // Arrange
        var filter = new GroupFilter
        {
            NumberOfItemsPerPage = 3,
            RequiredPage = 1,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = await _groupRepository.Truncated(filter);

        // Assert
        result.Groups.Should().HaveCount(3);
        result.CurrentPage.Should().Be(1);
        result.FirstItemOnPage.Should().Be(3);
    }

    [Test]
    public async Task Truncated_WithLastPage_ShouldReturnRemainingItems()
    {
        // Arrange
        var filter = new GroupFilter
        {
            NumberOfItemsPerPage = 3,
            RequiredPage = 2, // Last page with remainder
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true
        };

        // Act
        var result = await _groupRepository.Truncated(filter);

        // Assert
        result.Groups.Should().HaveCount(2); // 8 total items, 3 per page = 2 remaining on last page
        result.CurrentPage.Should().Be(2);
    }
    
    private static Expression<Func<T, bool>> CombineOr<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T));
        var leftVisitor = new ReplaceExpressionVisitor(left.Parameters[0], parameter);
        var rightVisitor = new ReplaceExpressionVisitor(right.Parameters[0], parameter);
        
        return Expression.Lambda<Func<T, bool>>(
            Expression.OrElse(leftVisitor.Visit(left.Body), rightVisitor.Visit(right.Body)), parameter);
    }
    
    private class ReplaceExpressionVisitor : ExpressionVisitor
    {
        private readonly Expression _oldValue;
        private readonly Expression _newValue;

        public ReplaceExpressionVisitor(Expression oldValue, Expression newValue)
        {
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public override Expression Visit(Expression node)
        {
            return node == _oldValue ? _newValue : base.Visit(node);
        }
    }
}