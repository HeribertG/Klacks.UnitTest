using Shouldly;
using Klacks.Api.Infrastructure.Mediator;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Mediator;

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
        unit1.ShouldBe(unit2);
    }

    [Test]
    public void Unit_Equals_ReturnsTrueForAnyUnit()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        unit1.Equals(unit2).ShouldBeTrue();
        unit1.Equals(Unit.Value).ShouldBeTrue();
    }

    [Test]
    public void Unit_Equals_ReturnsFalseForNonUnit()
    {
        // Arrange
        var unit = Unit.Value;
        object? obj = "not a unit";

        // Act & Assert
        unit.Equals(obj).ShouldBeFalse();
    }

    [Test]
    public void Unit_Equals_ReturnsFalseForNull()
    {
        // Arrange
        var unit = Unit.Value;
        object? nullObj = null;

        // Act & Assert
        unit.Equals(nullObj).ShouldBeFalse();
    }

    [Test]
    public void Unit_GetHashCode_ReturnsZero()
    {
        // Arrange
        var unit = Unit.Value;

        // Act & Assert
        unit.GetHashCode().ShouldBe(0);
    }

    [Test]
    public void Unit_CompareTo_ReturnsZero()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        unit1.CompareTo(unit2).ShouldBe(0);
    }

    [Test]
    public void Unit_IComparable_CompareTo_ReturnsZero()
    {
        // Arrange
        IComparable unit = new Unit();
        object obj = new Unit();

        // Act & Assert
        unit.CompareTo(obj).ShouldBe(0);
    }

    [Test]
    public void Unit_EqualityOperator_ReturnsTrueForAnyUnits()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        (unit1 == unit2).ShouldBeTrue();
    }

    [Test]
    public void Unit_InequalityOperator_ReturnsFalseForAnyUnits()
    {
        // Arrange
        var unit1 = new Unit();
        var unit2 = new Unit();

        // Act & Assert
        (unit1 != unit2).ShouldBeFalse();
    }

    [Test]
    public void Unit_ToString_ReturnsParentheses()
    {
        // Arrange
        var unit = Unit.Value;

        // Act & Assert
        unit.ToString().ShouldBe("()");
    }

    [Test]
    public async Task Unit_Task_ReturnsCompletedTask()
    {
        // Arrange & Act
        var result = await Unit.Task;

        // Assert
        result.ShouldBe(Unit.Value);
    }

    [Test]
    public void Unit_Task_IsSameInstance()
    {
        // Arrange & Act
        var task1 = Unit.Task;
        var task2 = Unit.Task;

        // Assert
        task1.ShouldBeSameAs(task2);
    }

    [Test]
    public void Unit_Task_IsCompleted()
    {
        // Arrange & Act
        var task = Unit.Task;

        // Assert
        task.IsCompleted.ShouldBeTrue();
    }
}
