using FluentAssertions;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using NUnit.Framework;

namespace UnitTest.Models;

[TestFixture]
public class ContractTests
{
    [Test]
    public void Contract_ShouldInitializeWithDefaultValues()
    {
        // Act
        var contract = new Contract();

        // Assert
        contract.Id.Should().Be(Guid.Empty);
        contract.Name.Should().BeEmpty();
        contract.GuaranteedHoursPerMonth.Should().Be(0);
        contract.MaximumHoursPerMonth.Should().Be(0);
        contract.MinimumHoursPerMonth.Should().Be(0);
        contract.ValidFrom.Should().Be(default(DateTime));
        contract.ValidUntil.Should().BeNull();
        contract.CalendarSelectionId.Should().Be(Guid.Empty);
        contract.CalendarSelection.Should().BeNull();
    }

    [Test]
    public void Contract_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var name = "Test Contract";
        var guaranteedHours = 160m;
        var maxHours = 200m;
        var minHours = 120m;
        var validFrom = DateTime.UtcNow;
        var validUntil = DateTime.UtcNow.AddYears(1);
        var calendarSelectionId = Guid.NewGuid();
        var calendarSelection = new CalendarSelection { Id = calendarSelectionId, Name = "Test Calendar" };

        // Act
        var contract = new Contract
        {
            Id = id,
            Name = name,
            GuaranteedHoursPerMonth = guaranteedHours,
            MaximumHoursPerMonth = maxHours,
            MinimumHoursPerMonth = minHours,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            CalendarSelectionId = calendarSelectionId,
            CalendarSelection = calendarSelection
        };

        // Assert
        contract.Id.Should().Be(id);
        contract.Name.Should().Be(name);
        contract.GuaranteedHoursPerMonth.Should().Be(guaranteedHours);
        contract.MaximumHoursPerMonth.Should().Be(maxHours);
        contract.MinimumHoursPerMonth.Should().Be(minHours);
        contract.ValidFrom.Should().Be(validFrom);
        contract.ValidUntil.Should().Be(validUntil);
        contract.CalendarSelectionId.Should().Be(calendarSelectionId);
        contract.CalendarSelection.Should().Be(calendarSelection);
    }

    [Test]
    public void Contract_ShouldInheritFromBaseEntity()
    {
        // Act
        var contract = new Contract();

        // Assert
        contract.Should().BeAssignableTo<BaseEntity>();
    }

    [Test]
    public void Contract_WithValidUntilNull_ShouldIndicateOpenEndedContract()
    {
        // Arrange
        var contract = new Contract
        {
            Name = "Open-ended Contract",
            ValidFrom = DateTime.UtcNow,
            ValidUntil = null
        };

        // Assert
        contract.ValidUntil.Should().BeNull();
    }

    [Test]
    public void Contract_ShouldAllowDecimalHours()
    {
        // Arrange
        var contract = new Contract
        {
            GuaranteedHoursPerMonth = 160.5m,
            MaximumHoursPerMonth = 200.75m,
            MinimumHoursPerMonth = 120.25m
        };

        // Assert
        contract.GuaranteedHoursPerMonth.Should().Be(160.5m);
        contract.MaximumHoursPerMonth.Should().Be(200.75m);
        contract.MinimumHoursPerMonth.Should().Be(120.25m);
    }

    [Test]
    public void Contract_ShouldBeValidWithMinimumRequiredFields()
    {
        // Arrange
        var contract = new Contract
        {
            Name = "Minimum Contract",
            ValidFrom = DateTime.UtcNow,
            CalendarSelectionId = Guid.NewGuid()
        };

        // Assert
        contract.Name.Should().NotBeEmpty();
        contract.ValidFrom.Should().BeAfter(DateTime.MinValue);
        contract.CalendarSelectionId.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Contract_WhenCreated_ShouldHaveBaseEntityProperties()
    {
        // Arrange
        var contract = new Contract();

        // Assert
        contract.CreateTime.Should().BeNull();
        contract.UpdateTime.Should().BeNull();
        contract.DeletedTime.Should().BeNull();
        contract.IsDeleted.Should().BeFalse();
        contract.CurrentUserCreated.Should().Be(string.Empty);
        contract.CurrentUserUpdated.Should().Be(string.Empty);
        contract.CurrentUserDeleted.Should().Be(string.Empty);
    }
}