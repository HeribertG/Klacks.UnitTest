using FluentAssertions;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Models.Schedules;
using Klacks.Api.Models.Staffs;
using Microsoft.EntityFrameworkCore;
using Klacks.Api.Datas;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace UnitTest.Services.Shifts;

[TestFixture]
public class ShiftSearchServiceTests
{
    private ShiftSearchService _searchService;
    private DataBaseContext _context;
    private List<Shift> _testShifts;
    private List<Client> _testClients;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _searchService = new ShiftSearchService();

        CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private void CreateTestData()
    {
        // Create test clients first
        _testClients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Müller",
                FirstName = "Hans",
                Company = "ABC Corp",
                IdNumber = 1001
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Schmidt",
                FirstName = "Anna",
                Company = "XYZ Ltd",
                IdNumber = 1002
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
                Abbreviation = "MS",
                Description = "Standard morning work shift",
                ClientId = _testClients[0].Id,
                Client = _testClients[0],
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Evening Shift",
                Abbreviation = "ES",
                Description = "Standard evening work shift",
                ClientId = _testClients[1].Id,
                Client = _testClients[1],
                StartShift = new TimeOnly(16, 0),
                EndShift = new TimeOnly(0, 0),
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Night Shift",
                Abbreviation = "NS",
                Description = "Standard night work shift",
                ClientId = _testClients[0].Id,
                Client = _testClients[0],
                StartShift = new TimeOnly(0, 0),
                EndShift = new TimeOnly(8, 0),
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Weekend Shift",
                Abbreviation = "WS",
                Description = "Weekend work coverage",
                ClientId = null, // No client assigned
                Client = null,
                StartShift = new TimeOnly(9, 0),
                EndShift = new TimeOnly(17, 0),
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "EmptyShift", // Name is required
                Abbreviation = "NULL",
                Description = "", // Test empty description handling
                ClientId = null,
                Client = null,
                StartShift = new TimeOnly(10, 0),
                EndShift = new TimeOnly(18, 0),
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            }
        };

        _context.Shift.AddRange(_testShifts);
        _context.SaveChanges();
    }

    [Test]
    public void ApplySearchFilter_WithEmptySearchString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, "", false);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [Test]
    public void ApplySearchFilter_WithNullSearchString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, null, false);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [Test]
    public void ApplySearchFilter_WithWhitespaceOnlySearchString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, "   ", false);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [TestCase("morning", 1, "Should find shift by name case-insensitive")]
    [TestCase("EVENING", 1, "Should find shift by name case-insensitive uppercase")]
    [TestCase("shift", 5, "Should find all shifts with 'shift' in name including EmptyShift")]
    [TestCase("MS", 1, "Should find shift by abbreviation")]
    [TestCase("es", 1, "Should find shift by abbreviation case-insensitive")]
    [TestCase("nonexistent", 0, "Should return no results for non-existent search")]
    public void ApplySearchFilter_WithSingleKeyword_ShouldReturnCorrectResults(string searchTerm, int expectedCount, string description)
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, searchTerm, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(expectedCount, description);
    }

    [Test]
    public void ApplySearchFilter_WithClientIncluded_ShouldSearchInClientData()
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();
        var searchString = "müller"; // Should find shifts assigned to client Müller

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2); // Morning and Night shifts are assigned to Müller
        shifts.Should().AllSatisfy(s => s.Client.Name.Should().Be("Müller"));
    }

    [Test]
    public void ApplySearchFilter_WithClientNotIncluded_ShouldNotSearchInClientData()
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();
        var searchString = "müller"; // Should not find anything when client search is disabled

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(0);
    }

    [Test]
    public void ApplySearchFilter_WithClientFirstName_ShouldFindCorrectShifts()
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();
        var searchString = "anna"; // Should find shifts assigned to client Anna

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1); // Evening shift is assigned to Anna
        shifts.First().Client.FirstName.Should().Be("Anna");
        shifts.First().Name.Should().Be("Evening Shift");
    }

    [Test]
    public void ApplySearchFilter_WithClientCompany_ShouldFindCorrectShifts()
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();
        var searchString = "abc"; // Should find shifts assigned to client with ABC Corp

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2); // Morning and Night shifts are assigned to client with ABC Corp
        shifts.Should().AllSatisfy(s => s.Client.Company.Should().Contain("ABC"));
    }

    [Test]
    public void ApplySearchFilter_WithSingleCharacter_ShouldApplyFirstSymbolSearch()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var searchString = "M"; // Should find shifts starting with M

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1); // Only Morning Shift starts with M
        shifts.First().Name.Should().Be("Morning Shift");
    }

    [Test]
    public void ApplySearchFilter_WithSingleCharacter_E_ShouldFindMatchingShifts()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var searchString = "E"; // Should find Evening Shift and EmptyShift

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2);
        shifts.Should().Contain(s => s.Name == "Evening Shift");
        shifts.Should().Contain(s => s.Name == "EmptyShift");
    }

    [Test]
    public void ApplySearchFilter_WithMultipleKeywords_ShouldUseOrLogic()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var searchString = "weekend shift"; // Should find all shifts with "weekend" OR "shift" (OR logic)

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(5); // All shifts contain "shift" + Weekend Shift contains "weekend"
        shifts.Should().Contain(s => s.Name == "Weekend Shift");
        shifts.Should().Contain(s => s.Name == "Morning Shift");
        shifts.Should().Contain(s => s.Name == "Evening Shift");
        shifts.Should().Contain(s => s.Name == "Night Shift");
        shifts.Should().Contain(s => s.Name == "EmptyShift");
    }

    [Test]
    public void ApplyKeywordSearch_WithEmptyKeywords_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var keywords = new string[] { };

        // Act
        var result = _searchService.ApplyKeywordSearch(query, keywords, false);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [Test]
    public void ApplyKeywordSearch_WithWhitespaceKeywords_ShouldIgnoreThem()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var keywords = new[] { " ", "", "  ", "morning" };

        // Act
        var result = _searchService.ApplyKeywordSearch(query, keywords, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1);
        shifts.First().Name.Should().Be("Morning Shift");
    }

    [Test]
    public void ApplyKeywordSearch_WithDuplicateKeywords_ShouldRemoveDuplicates()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var keywords = new[] { "morning", "MORNING", "Morning" };

        // Act
        var result = _searchService.ApplyKeywordSearch(query, keywords, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1);
        shifts.First().Name.Should().Be("Morning Shift");
    }

    [Test]
    public void ApplyFirstSymbolSearch_WithEmptyDescription_ShouldHandleGracefully()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var symbol = "e"; // Should find EmptyShift and Evening Shift (both start with 'e')

        // Act
        var result = _searchService.ApplyFirstSymbolSearch(query, symbol);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2); // EmptyShift and Evening Shift both start with 'e'
        shifts.Should().Contain(s => s.Name == "EmptyShift");
        shifts.Should().Contain(s => s.Name == "Evening Shift");
    }

    [Test]
    public void ApplySearchFilter_WithEmptyDescription_ShouldHandleGracefully()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var searchString = "empty"; // Should find shift with EmptyShift name

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1);
        shifts.First().Name.Should().Be("EmptyShift");
        shifts.First().Description.Should().Be(""); // Verify empty description handling
    }

    [Test]
    public void ApplySearchFilter_WithSpecificKeywords_ShouldFindExactMatches()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var searchString = "morning evening"; // Should find shifts with "morning" OR "evening" (OR logic)

        // Act  
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2);
        shifts.Should().Contain(s => s.Name == "Morning Shift");
        shifts.Should().Contain(s => s.Name == "Evening Shift");
    }

    [Test]
    public void ApplyKeywordSearch_WithClientSearch_ShouldFindShiftsByClientAndShiftData()
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();
        var keywords = new[] { "morning" }; // Should find by shift name

        // Act
        var result1 = _searchService.ApplyKeywordSearch(query, keywords, false);
        var result2 = _searchService.ApplyKeywordSearch(query, new[] { "hans" }, true); // Should find by client name

        // Assert
        result1.Should().HaveCount(1);
        result1.First().Name.Should().Be("Morning Shift");
        
        result2.Should().HaveCount(2); // Hans has 2 shifts
        result2.Should().AllSatisfy(s => s.Client.FirstName.Should().Be("Hans"));
    }

    [Test]
    public void ApplySearchFilter_WithComplexClientSearch_ShouldFindCorrectResults()
    {
        // Arrange
        var query = _context.Shift.Include(s => s.Client).AsQueryable();
        var searchString = "xyz anna"; // Should find shift with client Anna from XYZ

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1);
        shifts.First().Client.FirstName.Should().Be("Anna");
        shifts.First().Client.Company.Should().Contain("XYZ");
    }
}