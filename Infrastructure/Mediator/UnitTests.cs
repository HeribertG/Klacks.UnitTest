using FluentAssertions;
using Klacks.Api.Infrastructure.Mediator;
using NUnit.Framework;

namespace UnitTest.Infrastructure.Mediator;

[TestFixture]
public class UnitTests
{
    [Test]
    public void Unit_Value_ReturnsSameInstance()
    {
        // Arrange & Act
        var unit1 = Unit.Value;
        var unit2 = Unit.Value;

        // Assert
        unit1.Should().Be(unit2);
    }

    [Test]
    public void Unit_Equals_ReturnsTrueForAnyUnit()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        unit1.Equals(unit2).Should().BeTrue();
        unit1.Equals(Unit.Value).Should().BeTrue();
    }

    [Test]
    public void Unit_Equals_ReturnsFalseForNonUnit()
    {
        // Arrange
        var unit = Unit.Value;
        object? obj = "not a unit";

        // Act & Assert
        unit.Equals(obj).Should().BeFalse();
    }

    [Test]
    public void Unit_Equals_ReturnsFalseForNull()
    {
        // Arrange
        var unit = Unit.Value;
        object? nullObj = null;

        // Act & Assert
        unit.Equals(nullObj).Should().BeFalse();
    }

    [Test]
    public void Unit_GetHashCode_ReturnsZero()
    {
        // Arrange
        var unit = Unit.Value;

        // Act & Assert
        unit.GetHashCode().Should().Be(0);
    }

    [Test]
    public void Unit_CompareTo_ReturnsZero()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        unit1.CompareTo(unit2).Should().Be(0);
    }

    [Test]
    public void Unit_IComparable_CompareTo_ReturnsZero()
    {
        // Arrange
        IComparable unit = new Unit();
        object obj = new Unit();

        // Act & Assert
        unit.CompareTo(obj).Should().Be(0);
    }

    [Test]
    public void Unit_EqualityOperator_ReturnsTrueForAnyUnits()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        (unit1 == unit2).Should().BeTrue();
    }

    [Test]
    public void Unit_InequalityOperator_ReturnsFalseForAnyUnits()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        (unit1 != unit2).Should().BeFalse();
    }

    [Test]
    public void Unit_ToString_ReturnsParentheses()
    {
        // Arrange
        var unit = Unit.Value;

        // Act & Assert
        unit.ToString().Should().Be("()");
    }

    [Test]
    public async Task Unit_Task_ReturnsCompletedTask()
    {
        // Arrange & Act
        var result = await Unit.Task;

        // Assert
        result.Should().Be(Unit.Value);
    }

    [Test]
    public void Unit_Task_IsSameInstance()
    {
        // Arrange & Act
        var task1 = Unit.Task;
        var task2 = Unit.Task;

        // Assert
        task1.Should().BeSameAs(task2);
    }

    [Test]
    public void Unit_Task_IsCompleted()
    {
        // Arrange & Act
        var task = Unit.Task;

        // Assert
        task.IsCompleted.Should().BeTrue();
    }
}
