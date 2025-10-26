using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace UnitTest.Services.Shifts;

[TestFixture]
public class ShiftStatusFilterServiceTests
{
    private ShiftStatusFilterService _filterService;
    private DataBaseContext _context;
    private List<Shift> _testShifts;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _filterService = new ShiftStatusFilterService();

        CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private void CreateTestData()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        _testShifts = new List<Shift>
        {
            // OriginalOrder Shifts (Status = 0)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Original Order 1",
                Abbreviation = "OO1",
                Status = ShiftStatus.OriginalOrder,
                IsContainer = false,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Original Order 2",
                Abbreviation = "OO2",
                Status = ShiftStatus.OriginalOrder,
                IsContainer = false,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },

            // SealedOrder Shifts (Status = 1)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Sealed Order 1",
                Abbreviation = "SO1",
                Status = ShiftStatus.SealedOrder,
                IsContainer = false,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },

            // OriginalShift (Status = 2) - Not Container
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Original Shift 1",
                Abbreviation = "OS1",
                Status = ShiftStatus.OriginalShift,
                IsContainer = false,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },

            // SplitShift (Status = 3) - Not Container
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Split Shift 1",
                Abbreviation = "SS1",
                Status = ShiftStatus.SplitShift,
                IsContainer = false,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(12, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Split Shift 2",
                Abbreviation = "SS2",
                Status = ShiftStatus.SplitShift,
                IsContainer = false,
                FromDate = today,
                StartShift = new TimeOnly(12, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },

            // Container Shifts (IsContainer = true, verschiedene Status)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container Shift 1",
                Abbreviation = "CS1",
                Status = ShiftStatus.OriginalShift,
                IsContainer = true,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container Shift 2",
                Abbreviation = "CS2",
                Status = ShiftStatus.SplitShift,
                IsContainer = true,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            }
        };

        _context.Shift.AddRange(_testShifts);
        _context.SaveChanges();
    }

    [Test]
    public void ApplyStatusFilter_WithOriginalFilterType_ShouldReturnOnlyOriginalOrders()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2, "Should return only OriginalOrder shifts");
        shifts.Should().OnlyContain(s => s.Status == ShiftStatus.OriginalOrder);
        shifts.Should().Contain(s => s.Name == "Original Order 1");
        shifts.Should().Contain(s => s.Name == "Original Order 2");
    }

    [Test]
    public void ApplyStatusFilter_WithShiftFilterType_ShouldReturnOnlyNonContainerShifts()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(3, "Should return OriginalShift and SplitShift that are not containers");
        shifts.Should().OnlyContain(s => s.Status >= ShiftStatus.OriginalShift && !s.IsContainer);
        shifts.Should().Contain(s => s.Name == "Original Shift 1");
        shifts.Should().Contain(s => s.Name == "Split Shift 1");
        shifts.Should().Contain(s => s.Name == "Split Shift 2");
        shifts.Should().NotContain(s => s.IsContainer);
    }

    [Test]
    public void ApplyStatusFilter_WithContainerFilterType_ShouldReturnOnlyContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2, "Should return only Container shifts");
        shifts.Should().OnlyContain(s => s.IsContainer);
        shifts.Should().Contain(s => s.Name == "Container Shift 1");
        shifts.Should().Contain(s => s.Name == "Container Shift 2");
    }

    [Test]
    public void ApplyStatusFilter_WithAbsenceFilterType_ShouldReturnEmpty()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Absence);
        var shifts = result.ToList();

        // Assert
        shifts.Should().BeEmpty("Absence filter is not yet implemented (placeholder)");
    }

    [Test]
    public void ApplyStatusFilter_WithInvalidFilterType_ShouldReturnOriginalQuery()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();
        var invalidFilterType = (ShiftFilterType)999;

        // Act
        var result = _filterService.ApplyStatusFilter(query, invalidFilterType);

        // Assert
        result.Should().BeEquivalentTo(query);
    }

    [Test]
    public void ApplyStatusFilter_OriginalFilter_ShouldNotIncludeSealedOrders()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original);
        var shifts = result.ToList();

        // Assert
        shifts.Should().NotContain(s => s.Status == ShiftStatus.SealedOrder,
            "SealedOrder is status 1, but Original filter should only return status 0");
    }

    [Test]
    public void ApplyStatusFilter_ShiftFilter_ShouldExcludeContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift);
        var shifts = result.ToList();

        // Assert
        shifts.Should().NotContain(s => s.Name == "Container Shift 1");
        shifts.Should().NotContain(s => s.Name == "Container Shift 2");
        shifts.Should().NotContain(s => s.IsContainer == true);
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilter_ShouldIncludeBothOriginalAndSplitContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container);
        var shifts = result.ToList();

        // Assert
        var originalContainer = shifts.FirstOrDefault(s => s.Status == ShiftStatus.OriginalShift && s.IsContainer);
        var splitContainer = shifts.FirstOrDefault(s => s.Status == ShiftStatus.SplitShift && s.IsContainer);

        originalContainer.Should().NotBeNull("Should include OriginalShift containers");
        splitContainer.Should().NotBeNull("Should include SplitShift containers");
    }

    [Test]
    public void ApplyStatusFilter_ShiftFilter_ShouldIncludeOriginalShiftAndSplitShift()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift);
        var shifts = result.ToList();

        // Assert
        var hasOriginalShift = shifts.Any(s => s.Status == ShiftStatus.OriginalShift);
        var hasSplitShift = shifts.Any(s => s.Status == ShiftStatus.SplitShift);

        hasOriginalShift.Should().BeTrue("Should include OriginalShift (Status >= 2)");
        hasSplitShift.Should().BeTrue("Should include SplitShift (Status >= 2)");
    }

    [Test]
    public void ApplyStatusFilter_OriginalFilter_ShouldExcludePlannableShifts()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original);
        var shifts = result.ToList();

        // Assert
        shifts.Should().NotContain(s => s.Status == ShiftStatus.OriginalShift);
        shifts.Should().NotContain(s => s.Status == ShiftStatus.SplitShift);
        shifts.Should().OnlyContain(s => s.Status == ShiftStatus.OriginalOrder);
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilter_ShouldIgnoreStatus()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container);
        var shifts = result.ToList();

        // Assert
        var statuses = shifts.Select(s => s.Status).Distinct().ToList();
        statuses.Should().HaveCountGreaterThan(1, "Containers can have different statuses");
        shifts.Should().OnlyContain(s => s.IsContainer == true, "But all must be containers");
    }

    [Test]
    public void ApplyStatusFilter_MultipleFilters_ShouldBeExclusive()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var originalShifts = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original).ToList();
        var regularShifts = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift).ToList();
        var containerShifts = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container).ToList();

        // Assert
        var allFiltered = new[] { originalShifts, regularShifts, containerShifts };
        var intersection = originalShifts.Intersect(regularShifts).Union(regularShifts.Intersect(containerShifts)).Union(originalShifts.Intersect(containerShifts));

        intersection.Should().BeEmpty("Filters should be mutually exclusive - no shift should appear in multiple filter results");
    }
}
