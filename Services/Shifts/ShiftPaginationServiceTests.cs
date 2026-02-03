using FluentAssertions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.DTOs.Filter;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftPaginationServiceTests
{
    private DataBaseContext _context;
    private ShiftPaginationService _paginationService;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _paginationService = new ShiftPaginationService();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task ApplyPaginationAsync_WithValidFilter_ShouldReturnPaginatedResult()
    {
        // Arrange
        await SeedTestShifts(10);
        var query = _context.Shift.AsQueryable();

        var filter = new ShiftFilter
        {
            RequiredPage = 0,
            NumberOfItemsPerPage = 5
        };

        // Act
        var result = await _paginationService.ApplyPaginationAsync(query, filter);

        // Assert
        result.Should().NotBeNull();
        result.Shifts.Should().HaveCount(5);
        result.MaxItems.Should().Be(10);
        result.CurrentPage.Should().Be(0);
        result.MaxPages.Should().Be(1); // 10 items, 5 per page = 2 pages (0-indexed)
        result.FirstItemOnPage.Should().Be(0);
    }

    [Test]
    public async Task ApplyPaginationAsync_WithEmptyQuery_ShouldReturnEmptyResult()
    {
        // Arrange - No shifts seeded
        var query = _context.Shift.AsQueryable();
        var filter = new ShiftFilter
        {
            RequiredPage = 0,
            NumberOfItemsPerPage = 5
        };

        // Act
        var result = await _paginationService.ApplyPaginationAsync(query, filter);

        // Assert
        result.Should().NotBeNull();
        result.Shifts.Should().BeEmpty();
        result.MaxItems.Should().Be(0);
        result.FirstItemOnPage.Should().Be(-1);
    }

    [Test]
    public async Task ApplyPaginationAsync_WithSecondPage_ShouldReturnCorrectItems()
    {
        // Arrange
        await SeedTestShifts(10);
        var query = _context.Shift.AsQueryable();

        var filter = new ShiftFilter
        {
            RequiredPage = 1,
            NumberOfItemsPerPage = 3,
            FirstItemOnLastPage = 0, // First item of previous page
            IsNextPage = true
        };

        // Act
        var result = await _paginationService.ApplyPaginationAsync(query, filter);

        // Assert
        result.Should().NotBeNull();
        result.Shifts.Should().HaveCount(3);
        result.CurrentPage.Should().Be(1);
        result.FirstItemOnPage.Should().Be(3); // Should skip first 3 items
    }

    [Test]
    public void CalculateFirstItem_WithBasicPagination_ShouldCalculateCorrectly()
    {
        // Arrange
        var filter = new ShiftFilter
        {
            RequiredPage = 2,
            NumberOfItemsPerPage = 5
        };
        var totalCount = 20;

        // Act
        var firstItem = _paginationService.CalculateFirstItem(filter, totalCount);

        // Assert
        firstItem.Should().Be(10); // Page 2 with 5 items per page should start at item 10
    }

    [Test]
    public void CalculateFirstItem_WithNextPageNavigation_ShouldCalculateCorrectly()
    {
        // Arrange
        var filter = new ShiftFilter
        {
            RequiredPage = 1,
            NumberOfItemsPerPage = 5,
            FirstItemOnLastPage = 5,
            IsNextPage = true
        };
        var totalCount = 20;

        // Act
        var firstItem = _paginationService.CalculateFirstItem(filter, totalCount);

        // Assert
        firstItem.Should().Be(10); // FirstItemOnLastPage (5) + NumberOfItemsPerPage (5)
    }

    [Test]
    public void CalculateFirstItem_WithPreviousPageNavigation_ShouldCalculateCorrectly()
    {
        // Arrange
        var filter = new ShiftFilter
        {
            RequiredPage = 1,
            NumberOfItemsPerPage = 5,
            FirstItemOnLastPage = 10,
            IsPreviousPage = true,
            NumberOfItemOnPreviousPage = 5
        };
        var totalCount = 20;

        // Act
        var firstItem = _paginationService.CalculateFirstItem(filter, totalCount);

        // Assert
        firstItem.Should().Be(5); // FirstItemOnLastPage (10) - NumberOfItemOnPreviousPage (5)
    }

    [Test]
    public void CalculateFirstItem_WithSmallTotalCount_ShouldReturnZero()
    {
        // Arrange
        var filter = new ShiftFilter
        {
            RequiredPage = 0,
            NumberOfItemsPerPage = 10
        };
        var totalCount = 5; // Less than NumberOfItemsPerPage

        // Act
        var firstItem = _paginationService.CalculateFirstItem(filter, totalCount);

        // Assert
        firstItem.Should().Be(0);
    }

    [Test]
    public async Task ApplyPaginationAsync_WithZeroItemsPerPage_ShouldHandleGracefully()
    {
        // Arrange
        await SeedTestShifts(10);
        var query = _context.Shift.AsQueryable();

        var filter = new ShiftFilter
        {
            RequiredPage = 0,
            NumberOfItemsPerPage = 0 // Edge case: zero items per page
        };

        // Act
        var result = await _paginationService.ApplyPaginationAsync(query, filter);

        // Assert
        result.Should().NotBeNull();
        result.MaxPages.Should().Be(0);
    }

    [Test]
    public async Task ApplyPaginationAsync_WithLastPage_ShouldReturnRemainingItems()
    {
        // Arrange
        await SeedTestShifts(7); // 7 total items
        var query = _context.Shift.AsQueryable();

        var filter = new ShiftFilter
        {
            RequiredPage = 2,
            NumberOfItemsPerPage = 3,
            FirstItemOnLastPage = 3,
            IsNextPage = true
        };

        // Act
        var result = await _paginationService.ApplyPaginationAsync(query, filter);

        // Assert
        result.Should().NotBeNull();
        result.Shifts.Should().HaveCount(1); // Only 1 item remaining on last page
        result.MaxItems.Should().Be(7);
        result.FirstItemOnPage.Should().Be(6);
    }

    [Test]
    public void CalculateFirstItem_WithPreviousPageAndNegativeResult_ShouldReturnZero()
    {
        // Arrange
        var filter = new ShiftFilter
        {
            RequiredPage = 0,
            NumberOfItemsPerPage = 5,
            FirstItemOnLastPage = 2,
            IsPreviousPage = true,
            NumberOfItemOnPreviousPage = 10 // More than FirstItemOnLastPage
        };
        var totalCount = 20;

        // Act
        var firstItem = _paginationService.CalculateFirstItem(filter, totalCount);

        // Assert
        firstItem.Should().Be(0); // Should not return negative value
    }

    private async Task SeedTestShifts(int count)
    {
        var shifts = CreateTestShifts(count);
        await _context.Shift.AddRangeAsync(shifts);
        await _context.SaveChangesAsync();
    }

    private static List<Shift> CreateTestShifts(int count)
    {
        var shifts = new List<Shift>();
        var baseDate = DateOnly.FromDateTime(DateTime.Today);
        var baseTime = TimeOnly.FromDateTime(DateTime.Now);

        for (int i = 0; i < count; i++)
        {
            shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                Name = $"Shift {i}",
                Description = $"Test Shift {i}",
                FromDate = baseDate.AddDays(i),
                StartShift = baseTime.AddHours(i % 12), // Wrap hours within day
                EndShift = baseTime.AddHours((i % 12) + 8),
                Quantity = 1,
                SumEmployees = 1 + (i % 5) // Vary between 1-5 employees
            });
        }
        return shifts;
    }
}