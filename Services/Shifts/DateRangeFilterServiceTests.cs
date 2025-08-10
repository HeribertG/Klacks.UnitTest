using FluentAssertions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace UnitTest.Services.Shifts;

[TestFixture]
public class DateRangeFilterServiceTests
{
    private DateRangeFilterService _service;
    private List<Shift> _testShifts;

    [SetUp]
    public void SetUp()
    {
        _service = new DateRangeFilterService();
        
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        
        _testShifts = new List<Shift>
        {
            // Active shift (started yesterday, ends tomorrow)
            new Shift { Id = Guid.NewGuid(), Name = "Active Shift", FromDate = today.AddDays(-1), UntilDate = today.AddDays(1) },
            
            // Former shift (ended yesterday)
            new Shift { Id = Guid.NewGuid(), Name = "Former Shift", FromDate = today.AddDays(-10), UntilDate = today.AddDays(-1) },
            
            // Future shift (starts tomorrow)
            new Shift { Id = Guid.NewGuid(), Name = "Future Shift", FromDate = today.AddDays(1), UntilDate = today.AddDays(5) },
            
            // Active shift with no end date
            new Shift { Id = Guid.NewGuid(), Name = "Active No End", FromDate = today.AddDays(-5), UntilDate = null },
            
            // Edge case: starts today
            new Shift { Id = Guid.NewGuid(), Name = "Starts Today", FromDate = today, UntilDate = today.AddDays(3) },
            
            // Edge case: ends today
            new Shift { Id = Guid.NewGuid(), Name = "Ends Today", FromDate = today.AddDays(-3), UntilDate = today }
        };
    }

    [Test]
    public void IsActiveShift_WithActiveShift_ReturnsTrue()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-1);
        var untilDate = today.AddDays(1);

        // Act
        var result = _service.IsActiveShift(fromDate, untilDate);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsActiveShift_WithFormerShift_ReturnsFalse()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-10);
        var untilDate = today.AddDays(-1);

        // Act
        var result = _service.IsActiveShift(fromDate, untilDate);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void IsActiveShift_WithFutureShift_ReturnsFalse()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(1);
        var untilDate = today.AddDays(5);

        // Act
        var result = _service.IsActiveShift(fromDate, untilDate);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void IsActiveShift_WithNoEndDate_ReturnsTrue()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-5);
        DateOnly? untilDate = null;

        // Act
        var result = _service.IsActiveShift(fromDate, untilDate);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsFormerShift_WithFormerShift_ReturnsTrue()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-10);
        var untilDate = today.AddDays(-1);

        // Act
        var result = _service.IsFormerShift(fromDate, untilDate);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsFormerShift_WithActiveShift_ReturnsFalse()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-1);
        var untilDate = today.AddDays(1);

        // Act
        var result = _service.IsFormerShift(fromDate, untilDate);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void IsFormerShift_WithNoEndDate_ReturnsFalse()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-5);
        DateOnly? untilDate = null;

        // Act
        var result = _service.IsFormerShift(fromDate, untilDate);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void IsFutureShift_WithFutureShift_ReturnsTrue()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(1);
        var untilDate = today.AddDays(5);

        // Act
        var result = _service.IsFutureShift(fromDate, untilDate);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsFutureShift_WithActiveShift_ReturnsFalse()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = today.AddDays(-1);
        var untilDate = today.AddDays(1);

        // Act
        var result = _service.IsFutureShift(fromDate, untilDate);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void ApplyDateRangeFilter_ActiveOnly_ReturnsOnlyActiveShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: true, formerDateRange: false, futureDateRange: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(4); // Active Shift, Active No End, Starts Today, Ends Today
        shifts.Should().Contain(s => s.Name == "Active Shift");
        shifts.Should().Contain(s => s.Name == "Active No End");
        shifts.Should().Contain(s => s.Name == "Starts Today");
        shifts.Should().Contain(s => s.Name == "Ends Today");
    }

    [Test]
    public void ApplyDateRangeFilter_FormerOnly_ReturnsOnlyFormerShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: false, formerDateRange: true, futureDateRange: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1);
        shifts.Should().Contain(s => s.Name == "Former Shift");
    }

    [Test]
    public void ApplyDateRangeFilter_FutureOnly_ReturnsOnlyFutureShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: false, formerDateRange: false, futureDateRange: true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(1);
        shifts.Should().Contain(s => s.Name == "Future Shift");
    }

    [Test]
    public void ApplyDateRangeFilter_AllFlags_ReturnsAllShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: true, formerDateRange: true, futureDateRange: true);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(_testShifts.Count);
    }

    [Test]
    public void ApplyDateRangeFilter_NoFlags_ReturnsEmpty()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: false, formerDateRange: false, futureDateRange: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().BeEmpty();
    }

    [Test]
    public void ApplyDateRangeFilter_ActiveAndFormer_ReturnsActiveAndFormerShifts()
    {
        // Arrange
        var query = _testShifts.AsQueryable();

        // Act
        var result = _service.ApplyDateRangeFilter(query, activeDateRange: true, formerDateRange: true, futureDateRange: false);
        var shifts = result.ToList();

        // Assert
        shifts.Should().HaveCount(5); // All except Future Shift (4 active + 1 former)
        shifts.Should().NotContain(s => s.Name == "Future Shift");
    }
}