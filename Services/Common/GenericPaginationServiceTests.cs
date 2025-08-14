using FluentAssertions;
using Klacks.Api.Domain.Services.Common;
using NUnit.Framework;

namespace UnitTest.Services.Common;

[TestFixture]
public class GenericPaginationServiceTests
{
    private GenericPaginationService<TestEntity> _service;

    [SetUp]
    public void Setup()
    {
        _service = new GenericPaginationService<TestEntity>();
    }

    [Test]
    public void CalculateFirstItem_WithValidParameters_ShouldReturnCorrectValue()
    {
        var result = _service.CalculateFirstItem(3, 10, 50);

        result.Should().Be(20);
    }

    [Test]
    public void CalculateFirstItem_WithEmptyData_ShouldReturnZero()
    {
        var result = _service.CalculateFirstItem(1, 10, 0);

        result.Should().Be(0);
    }

    [Test]
    public void CalculateFirstItem_WithPageBeyondData_ShouldReturnMaxValue()
    {
        var result = _service.CalculateFirstItem(10, 10, 25);

        result.Should().Be(24);
    }

    [Test]
    public void CalculateFirstItem_WithSinglePage_ShouldReturnZero()
    {
        var result = _service.CalculateFirstItem(1, 10, 5);

        result.Should().Be(0);
    }

    [Test]
    public void CalculateFirstItem_WithExactPageBoundary_ShouldReturnCorrectValue()
    {
        var result = _service.CalculateFirstItem(2, 10, 20);

        result.Should().Be(10);
    }

    [Test]
    public void CalculateFirstItem_WithPage1_ShouldReturnZero()
    {
        var result = _service.CalculateFirstItem(1, 10, 50);

        result.Should().Be(0);
    }

    [Test]
    public void CalculateFirstItem_WithLargePageSize_ShouldHandleCorrectly()
    {
        var result = _service.CalculateFirstItem(1, 100, 50);

        result.Should().Be(0);
    }

    [Test]
    public void CalculateFirstItem_WithSmallDataSet_ShouldNotExceedBounds()
    {
        var result = _service.CalculateFirstItem(5, 10, 15);

        result.Should().Be(14);
    }
}

public class TestEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}