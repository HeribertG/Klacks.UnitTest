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
                ShiftType = ShiftType.IsTask,
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
                ShiftType = ShiftType.IsTask,
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
                ShiftType = ShiftType.IsTask,
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
                ShiftType = ShiftType.IsTask,
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
                ShiftType = ShiftType.IsTask,
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
                ShiftType = ShiftType.IsTask,
                FromDate = today,
                StartShift = new TimeOnly(12, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },

            // Container Orders (ShiftType = IsContainer, Status = OriginalOrder)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container Order 1",
                Abbreviation = "CO1",
                Status = ShiftStatus.OriginalOrder,
                ShiftType = ShiftType.IsContainer,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container Order 2",
                Abbreviation = "CO2",
                Status = ShiftStatus.OriginalOrder,
                ShiftType = ShiftType.IsContainer,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            // Sealed Container Order (ShiftType = IsContainer, Status = SealedOrder)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Sealed Container Order 1",
                Abbreviation = "SCO1",
                Status = ShiftStatus.SealedOrder,
                ShiftType = ShiftType.IsContainer,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            // Container Shift with OriginalShift status (ShiftType = IsContainer, Status = OriginalShift - SHOULD appear in Container filter)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container Shift 1",
                Abbreviation = "CS1",
                Status = ShiftStatus.OriginalShift,
                ShiftType = ShiftType.IsContainer,
                FromDate = today,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0)
            },
            // Container Shift with SplitShift status (ShiftType = IsContainer, Status = SplitShift - should NOT appear in Container filter)
            new Shift
            {
                Id = Guid.NewGuid(),
                Name = "Container Shift 2",
                Abbreviation = "CS2",
                Status = ShiftStatus.SplitShift,
                ShiftType = ShiftType.IsContainer,
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
    public void ApplyStatusFilter_WithOriginalFilterType_ShouldReturnOnlyOriginalOrdersExcludingContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2, "Should return only non-container OriginalOrder shifts");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.OriginalOrder);
        shifts.Should().Contain(s => s.Name == "Original Order 1");
        shifts.Should().Contain(s => s.Name == "Original Order 2");
        shifts.Should().NotContain(s => s.Name == "Container Order 1", "Container orders are filtered separately");
        shifts.Should().NotContain(s => s.Name == "Container Order 2", "Container orders are filtered separately");
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
        shifts.Should().OnlyContain(s => s.Status >= ShiftStatus.OriginalShift && s.ShiftType == ShiftType.IsTask);
        shifts.Should().Contain(s => s.Name == "Original Shift 1");
        shifts.Should().Contain(s => s.Name == "Split Shift 1");
        shifts.Should().Contain(s => s.Name == "Split Shift 2");
        shifts.Should().NotContain(s => s.ShiftType == ShiftType.IsContainer);
    }

    [Test]
    public void ApplyStatusFilter_WithContainerFilterType_ShouldReturnOnlyOriginalShiftContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1, "Should return only containers with Status = OriginalShift");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsContainer && s.Status == ShiftStatus.OriginalShift);
        shifts.Should().Contain(s => s.Name == "Container Shift 1");
        shifts.Should().NotContain(s => s.Name == "Container Order 1", "OriginalOrder containers should be excluded");
        shifts.Should().NotContain(s => s.Name == "Container Order 2", "OriginalOrder containers should be excluded");
        shifts.Should().NotContain(s => s.Name == "Sealed Container Order 1", "SealedOrder containers should be excluded");
        shifts.Should().NotContain(s => s.Name == "Container Shift 2", "SplitShift containers should be excluded");
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
    public void ApplyStatusFilter_OriginalFilterWithoutSealedFlag_ShouldNotIncludeSealedOrders()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().NotContain(s => s.Status == ShiftStatus.SealedOrder,
            "Without isSealedOrder flag, Original filter should only return OriginalOrder (status 0)");
        shifts.Should().OnlyContain(s => s.Status == ShiftStatus.OriginalOrder);
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
        shifts.Should().NotContain(s => s.ShiftType == ShiftType.IsContainer);
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilterWithSealedOrder_ShouldReturnOnlyOriginalShiftContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1, "Container filter returns only Status=OriginalShift containers, isSealedOrder flag has no effect");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsContainer && s.Status == ShiftStatus.OriginalShift);
        shifts.Should().Contain(s => s.Name == "Container Shift 1");
        shifts.Should().NotContain(s => s.Name == "Container Order 1");
        shifts.Should().NotContain(s => s.Name == "Container Order 2");
        shifts.Should().NotContain(s => s.Name == "Sealed Container Order 1");
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
    public void ApplyStatusFilter_ContainerFilter_ShouldReturnOnlyOriginalShiftContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1, "Container filter returns only containers with Status = OriginalShift");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsContainer && s.Status == ShiftStatus.OriginalShift);
        var statuses = shifts.Select(s => s.Status).Distinct().ToList();
        statuses.Should().HaveCount(1);
        statuses.Should().Contain(ShiftStatus.OriginalShift);
        statuses.Should().NotContain(ShiftStatus.OriginalOrder);
        statuses.Should().NotContain(ShiftStatus.SealedOrder);
        statuses.Should().NotContain(ShiftStatus.SplitShift);
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

    [Test]
    public void ApplyStatusFilter_WithIsSealedOrderFalse_ShouldReturnOnlyOriginalOrders()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2, "Should return only OriginalOrder shifts (Status = 0), excluding containers");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.OriginalOrder);
        shifts.Should().NotContain(s => s.Status == ShiftStatus.SealedOrder, "SealedOrder should be excluded when isSealedOrder = false");
        shifts.Should().NotContain(s => s.ShiftType == ShiftType.IsContainer, "Containers are filtered separately");
    }

    [Test]
    public void ApplyStatusFilter_WithIsSealedOrderTrue_ShouldReturnAllSealedOrdersIncludingContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2, "Should return ALL SealedOrder shifts (tasks AND containers)");
        shifts.Should().OnlyContain(s => s.Status == ShiftStatus.SealedOrder);
        shifts.Should().Contain(s => s.Name == "Sealed Order 1", "Should include the SealedOrder task");
        shifts.Should().Contain(s => s.Name == "Sealed Container Order 1", "Should include the SealedOrder container");
        shifts.Should().NotContain(s => s.Status == ShiftStatus.OriginalOrder, "OriginalOrder should be excluded when isSealedOrder = true");
    }

    [Test]
    public void ApplyStatusFilter_IsSealedOrderDoesNotAffectShiftOrContainerFilters()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var shiftResultWithoutFlag = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift, isSealedOrder: false).ToList();
        var shiftResultWithFlag = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift, isSealedOrder: true).ToList();
        var containerResultWithoutFlag = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: false).ToList();
        var containerResultWithFlag = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: true).ToList();

        // Assert
        shiftResultWithoutFlag.Should().BeEquivalentTo(shiftResultWithFlag, "isSealedOrder flag should not affect Shift filter");
        containerResultWithoutFlag.Should().BeEquivalentTo(containerResultWithFlag, "isSealedOrder flag should not affect Container filter");
        containerResultWithoutFlag.Should().HaveCount(5, "Container filter returns all containers");
        containerResultWithFlag.Should().HaveCount(5, "Container filter returns all containers");
    }

    [Test]
    public void ApplyStatusFilter_DefaultBehavior_ShouldExcludeSealedOrders()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act - Aufruf ohne isSealedOrder Parameter (sollte Default = false verwenden)
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(2, "Default behavior should return OriginalOrders (excluding containers)");
        shifts.Should().NotContain(s => s.Status == ShiftStatus.SealedOrder, "SealedOrder should be excluded by default");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.OriginalOrder);
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilterWithoutSealedOrderFlag_ShouldReturnAllContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(5, "Container filter returns ALL containers regardless of isSealedOrder flag");
        shifts.Should().Contain(s => s.Name == "Sealed Container Order 1",
            "Sealed containers are included");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsContainer,
            "All returned items must be containers");
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilterWithSealedOrderFlag_ShouldReturnAllContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(5, "Container filter returns ALL containers regardless of isSealedOrder flag");
        shifts.Should().Contain(s => s.Name == "Sealed Container Order 1",
            "Sealed containers are included");
        shifts.Should().Contain(s => s.Name == "Container Order 1",
            "OriginalOrder containers are also included");
        shifts.Should().OnlyContain(s => s.ShiftType == ShiftType.IsContainer);
    }
}
