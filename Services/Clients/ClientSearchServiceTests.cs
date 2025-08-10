using FluentAssertions;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.EntityFrameworkCore;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace UnitTest.Services.Clients;

[TestFixture]
public class ClientSearchServiceTests
{
    private ClientSearchService _searchService;
    private DataBaseContext _context;
    private List<Client> _testClients;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _searchService = new ClientSearchService();

        CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private void CreateTestData()
    {
        _testClients = new List<Client>
        {
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Müller",
                FirstName = "Hans",
                SecondName = "Peter",
                Company = "ABC GmbH",
                IdNumber = 12345,
                Gender = GenderEnum.Male,
                Addresses = new List<Address>
                {
                    new Address
                    {
                        Street = "Hauptstraße 1",
                        City = "Berlin",
                        State = "BE",
                        Country = "DE",
                        ValidFrom = DateTime.Now.AddDays(-30)
                    }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Schmidt",
                FirstName = "Anna",
                MaidenName = "Weber",
                Company = "XYZ AG",
                IdNumber = 67890,
                Gender = GenderEnum.Female,
                Addresses = new List<Address>
                {
                    new Address
                    {
                        Street = "Nebenstraße 2",
                        City = "München",
                        State = "BY",
                        Country = "DE",
                        ValidFrom = DateTime.Now.AddDays(-20)
                    }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "Johnson",
                FirstName = "John",
                Company = "Tech Solutions",
                IdNumber = 11111,
                Gender = GenderEnum.Male,
                Addresses = new List<Address>
                {
                    new Address
                    {
                        Street = "Main Street 10",
                        City = "Hamburg",
                        State = "HH",
                        Country = "DE",
                        ValidFrom = DateTime.Now.AddDays(-10)
                    }
                }
            },
            new Client
            {
                Id = Guid.NewGuid(),
                Name = "EmptyFirstName", // Name is required, but FirstName can be empty
                FirstName = null, // Test null handling for nullable property
                Company = "Empty Name Co",
                IdNumber = 22222,
                Gender = GenderEnum.Male // Required property
            }
        };

        _context.Client.AddRange(_testClients);
        _context.SaveChanges();
    }

    [Test]
    public void ApplySearchFilter_WithEmptySearchString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Client.AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, "", false);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [Test]
    public void ApplySearchFilter_WithNullSearchString_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Client.AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, null, false);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [TestCase("müller", 1, "Should find client by last name case-insensitive")]
    [TestCase("hans", 1, "Should find client by first name case-insensitive")]
    [TestCase("ABC", 1, "Should find client by company name")]
    [TestCase("gmbh", 1, "Should find client by partial company name")]
    [TestCase("schmidt", 1, "Should find client by last name")]
    [TestCase("weber", 1, "Should find client by maiden name")]
    [TestCase("xyz", 1, "Should find client by partial company name")]
    [TestCase("nonexistent", 0, "Should return no results for non-existent search")]
    public void ApplySearchFilter_WithSingleKeyword_ShouldReturnCorrectResults(string searchTerm, int expectedCount, string description)
    {
        // Arrange
        var query = _context.Client.AsQueryable();

        // Act
        var result = _searchService.ApplySearchFilter(query, searchTerm, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(expectedCount, description);
    }

    [Test]
    public void ApplySearchFilter_WithMultipleKeywords_ShouldApplyStandardSearch()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var searchString = "müller hans"; // Should find the client with both name and firstname

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().Name.Should().Be("Müller");
        clients.First().FirstName.Should().Be("Hans");
    }

    [Test]
    public void ApplySearchFilter_WithPlusOperator_ShouldApplyExactSearch()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var searchString = "müller+abc"; // Should find client with both terms

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().Name.Should().Be("Müller");
        clients.First().Company.Should().Be("ABC GmbH");
    }

    [Test]
    public void ApplySearchFilter_WithSingleCharacter_ShouldApplyFirstSymbolSearch()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var searchString = "M"; // Should find clients starting with M

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1); // Only Müller starts with M
        clients.First().Name.Should().Be("Müller");
    }

    [Test]
    public void ApplySearchFilter_WithAddressIncluded_ShouldSearchInAddresses()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var searchString = "berlin"; // Should find client with Berlin address

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, true);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().Addresses.Should().Contain(a => a.City.ToLower().Contains("berlin"));
    }

    [Test]
    public void ApplySearchFilter_WithAddressNotIncluded_ShouldNotSearchInAddresses()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var searchString = "berlin"; // Should not find anything when address search is disabled

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(0);
    }

    [Test]
    public void ApplyIdNumberSearch_ShouldFindCorrectClient()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var idNumber = 12345;

        // Act
        var result = _searchService.ApplyIdNumberSearch(query, idNumber);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().IdNumber.Should().Be(idNumber);
        clients.First().Name.Should().Be("Müller");
    }

    [Test]
    public void IsNumericSearch_WithNumericString_ShouldReturnTrue()
    {
        // Arrange & Act
        var result = _searchService.IsNumericSearch("12345");

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsNumericSearch_WithNonNumericString_ShouldReturnFalse()
    {
        // Arrange & Act
        var result = _searchService.IsNumericSearch("abc123");

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void IsNumericSearch_WithWhitespaceAroundNumber_ShouldReturnTrue()
    {
        // Arrange & Act
        var result = _searchService.IsNumericSearch("  12345  ");

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void ParseSearchString_ShouldSplitAndNormalizeKeywords()
    {
        // Arrange
        var searchString = "  Hans   Peter  ";

        // Act
        var result = _searchService.ParseSearchString(searchString);

        // Assert
        result.Should().BeEquivalentTo(new[] { "hans", "peter" });
    }

    [Test]
    public void ApplySearchFilter_WithNullClientProperties_ShouldHandleGracefully()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var searchString = "empty"; // Should find the client with null FirstName

        // Act
        var result = _searchService.ApplySearchFilter(query, searchString, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().Company.Should().Be("Empty Name Co");
        clients.First().FirstName.Should().BeNull(); // Verify null handling
    }

    [Test]
    public void ApplyFirstSymbolSearch_WithNullNames_ShouldHandleGracefully()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var symbol = "e"; // Should find "EmptyFirstName" client by name

        // Act
        var result = _searchService.ApplyFirstSymbolSearch(query, symbol);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().Name.Should().Be("EmptyFirstName");
        clients.First().FirstName.Should().BeNull(); // Verify null handling
    }

    [Test] 
    public void ApplyExactSearch_WithMultipleKeywords_ShouldFindMatchingClients()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var keywords = new[] { "tech", "solutions" };

        // Act
        var result = _searchService.ApplyExactSearch(query, keywords, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().Company.Should().Be("Tech Solutions");
    }

    [Test]
    public void ApplyStandardSearch_WithAllKeywordsRequired_ShouldFindOnlyMatchingClients()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var keywords = new[] { "john", "johnson" }; // Both must match

        // Act
        var result = _searchService.ApplyStandardSearch(query, keywords, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(1);
        clients.First().FirstName.Should().Be("John");
        clients.First().Name.Should().Be("Johnson");
    }

    [Test]
    public void ApplyStandardSearch_WithUnmatchedKeywords_ShouldReturnEmpty()
    {
        // Arrange
        var query = _context.Client.AsQueryable();
        var keywords = new[] { "john", "smith" }; // "smith" doesn't exist

        // Act
        var result = _searchService.ApplyStandardSearch(query, keywords, false);
        var clients = result.ToList();

        // Assert
        clients.Should().HaveCount(0);
    }
}