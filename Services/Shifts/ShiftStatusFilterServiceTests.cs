using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Klacks.UnitTest.Services.Shifts;

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
            // Container Shift with OriginalShift status (ShiftType = IsContainer, Status = OriginalShift)
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
            // Container Shift with SplitShift status (ShiftType = IsContainer, Status = SplitShift)
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
        shifts.Count().ShouldBe(2, "Should return only non-container OriginalOrder shifts");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.OriginalOrder);
        shifts.ShouldContain(s => s.Name == "Original Order 1");
        shifts.ShouldContain(s => s.Name == "Original Order 2");
        shifts.ShouldNotContain(s => s.Name == "Container Order 1", "Container orders are filtered separately");
        shifts.ShouldNotContain(s => s.Name == "Container Order 2", "Container orders are filtered separately");
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
        shifts.Count().ShouldBe(3, "Should return OriginalShift and SplitShift that are not containers");
        shifts.ShouldAllBe(s => s.Status >= ShiftStatus.OriginalShift && s.ShiftType == ShiftType.IsTask);
        shifts.ShouldContain(s => s.Name == "Original Shift 1");
        shifts.ShouldContain(s => s.Name == "Split Shift 1");
        shifts.ShouldContain(s => s.Name == "Split Shift 2");
        shifts.ShouldNotContain(s => s.ShiftType == ShiftType.IsContainer);
    }

    [Test]
    public void ApplyStatusFilter_WithContainerFilterType_ShouldReturnAllContainersRegardlessOfStatus()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(5, "Should return every container shift regardless of status");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsContainer);
        shifts.ShouldContain(s => s.Name == "Container Shift 1");
        shifts.ShouldContain(s => s.Name == "Container Order 1", "OriginalOrder containers belong to the container view");
        shifts.ShouldContain(s => s.Name == "Container Order 2", "OriginalOrder containers belong to the container view");
        shifts.ShouldContain(s => s.Name == "Sealed Container Order 1", "SealedOrder containers belong to the container view");
        shifts.ShouldContain(s => s.Name == "Container Shift 2", "SplitShift containers belong to the container view");
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
        shifts.ShouldBeEmpty("Absence filter is not yet implemented (placeholder)");
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
        result.ShouldBeEquivalentTo(query);
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
        shifts.ShouldNotContain(s => s.Status == ShiftStatus.SealedOrder,
            "Without isSealedOrder flag, Original filter should only return OriginalOrder (status 0)");
        shifts.ShouldAllBe(s => s.Status == ShiftStatus.OriginalOrder);
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
        shifts.ShouldNotContain(s => s.Name == "Container Shift 1");
        shifts.ShouldNotContain(s => s.Name == "Container Shift 2");
        shifts.ShouldNotContain(s => s.ShiftType == ShiftType.IsContainer);
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilterWithSealedOrder_ShouldReturnAllContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: true);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(5, "Container filter returns every container, isSealedOrder flag has no effect");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsContainer);
        shifts.ShouldContain(s => s.Name == "Container Shift 1");
        shifts.ShouldContain(s => s.Name == "Container Order 1");
        shifts.ShouldContain(s => s.Name == "Container Order 2");
        shifts.ShouldContain(s => s.Name == "Sealed Container Order 1");
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

        hasOriginalShift.ShouldBeTrue("Should include OriginalShift (Status >= 2)");
        hasSplitShift.ShouldBeTrue("Should include SplitShift (Status >= 2)");
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
        shifts.ShouldNotContain(s => s.Status == ShiftStatus.OriginalShift);
        shifts.ShouldNotContain(s => s.Status == ShiftStatus.SplitShift);
        shifts.ShouldAllBe(s => s.Status == ShiftStatus.OriginalOrder);
    }

    [Test]
    public void ApplyStatusFilter_ContainerFilter_ShouldIncludeContainersOfEveryStatus()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container, isSealedOrder: false);
        var shifts = result.ToList();

        // Assert
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsContainer);
        var statuses = shifts.Select(s => s.Status).Distinct().ToList();
        statuses.ShouldContain(ShiftStatus.OriginalShift);
        statuses.ShouldContain(ShiftStatus.OriginalOrder);
        statuses.ShouldContain(ShiftStatus.SealedOrder);
        statuses.ShouldContain(ShiftStatus.SplitShift);
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

        intersection.ShouldBeEmpty("Filters should be mutually exclusive - no shift should appear in multiple filter results");
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
        shifts.Count().ShouldBe(2, "Should return only OriginalOrder shifts (Status = 0), excluding containers");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.OriginalOrder);
        shifts.ShouldNotContain(s => s.Status == ShiftStatus.SealedOrder, "SealedOrder should be excluded when isSealedOrder = false");
        shifts.ShouldNotContain(s => s.ShiftType == ShiftType.IsContainer, "Containers are filtered separately");
    }

    [Test]
    public void ApplyStatusFilter_WithIsSealedOrderTrue_ShouldReturnOnlySealedOrderTasksExcludingContainers()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var result = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: true);
        var shifts = result.ToList();

        // Assert
        shifts.Count().ShouldBe(1, "Should return only SealedOrder tasks, containers belong to the container view");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.SealedOrder);
        shifts.ShouldContain(s => s.Name == "Sealed Order 1", "Should include the SealedOrder task");
        shifts.ShouldNotContain(s => s.Name == "Sealed Container Order 1", "Sealed containers appear in the container view instead");
        shifts.ShouldNotContain(s => s.Status == ShiftStatus.OriginalOrder, "OriginalOrder should be excluded when isSealedOrder = true");
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
        shiftResultWithoutFlag.ShouldBeEquivalentTo(shiftResultWithFlag, "isSealedOrder flag should not affect Shift filter");
        containerResultWithoutFlag.ShouldBeEquivalentTo(containerResultWithFlag, "isSealedOrder flag should not affect Container filter");
        containerResultWithoutFlag.Count().ShouldBe(5, "Container filter returns every container shift");
        containerResultWithFlag.Count().ShouldBe(5, "Container filter returns every container shift");
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
        shifts.Count().ShouldBe(2, "Default behavior should return OriginalOrders (excluding containers)");
        shifts.ShouldNotContain(s => s.Status == ShiftStatus.SealedOrder, "SealedOrder should be excluded by default");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsTask && s.Status == ShiftStatus.OriginalOrder);
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
        shifts.Count().ShouldBe(5, "Container filter returns every container shift");
        shifts.ShouldContain(s => s.Name == "Container Shift 1");
        shifts.ShouldContain(s => s.Name == "Sealed Container Order 1",
            "Sealed containers (Status = SealedOrder) are included");
        shifts.ShouldContain(s => s.Name == "Container Order 1",
            "Container orders (Status = OriginalOrder) are included");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsContainer,
            "Only container shifts are returned");
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
        shifts.Count().ShouldBe(5, "Container filter returns every container shift, isSealedOrder flag has no effect");
        shifts.ShouldContain(s => s.Name == "Container Shift 1");
        shifts.ShouldContain(s => s.Name == "Sealed Container Order 1",
            "Sealed containers (Status = SealedOrder) are included");
        shifts.ShouldContain(s => s.Name == "Container Order 1",
            "Container orders (Status = OriginalOrder) are included");
        shifts.ShouldAllBe(s => s.ShiftType == ShiftType.IsContainer);
    }

    [Test]
    public void ApplyStatusFilter_ContainerWithOriginalOrderStatus_ShouldAppearOnlyInContainerView()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var ordersView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: false).ToList();
        var sealedOrdersView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: true).ToList();
        var shiftsView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift).ToList();
        var containersView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container).ToList();

        // Assert
        containersView.ShouldContain(s => s.Name == "Container Order 1",
            "A container with Status = OriginalOrder must appear in the container view");
        ordersView.ShouldNotContain(s => s.Name == "Container Order 1",
            "A container with Status = OriginalOrder must not appear in the orders view");
        sealedOrdersView.ShouldNotContain(s => s.Name == "Container Order 1",
            "A container with Status = OriginalOrder must not appear in the sealed orders view");
        shiftsView.ShouldNotContain(s => s.Name == "Container Order 1",
            "A container with Status = OriginalOrder must not appear in the plannable shifts view");
    }

    [Test]
    public void ApplyStatusFilter_AllViewsIncludingSealedOrders_ShouldBeMutuallyExclusive()
    {
        // Arrange
        var query = _context.Shift.AsQueryable();

        // Act
        var ordersView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: false).ToList();
        var sealedOrdersView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Original, isSealedOrder: true).ToList();
        var shiftsView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Shift).ToList();
        var containersView = _filterService.ApplyStatusFilter(query, ShiftFilterType.Container).ToList();

        // Assert
        var allViews = new[] { ordersView, sealedOrdersView, shiftsView, containersView };
        for (var i = 0; i < allViews.Length; i++)
        {
            for (var j = i + 1; j < allViews.Length; j++)
            {
                allViews[i].Intersect(allViews[j]).ShouldBeEmpty(
                    "No shift may appear in more than one list view");
            }
        }
    }
}
