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
        // Arrange & Act
        var contract = new Contract();

        // Assert
        contract.Id.Should().Be(Guid.Empty);
        contract.Name.Should().BeEmpty();
        contract.GuaranteedHours.Should().BeNull();
        contract.MaximumHours.Should().BeNull();
        contract.MinimumHours.Should().BeNull();
        contract.FullTime.Should().BeNull();
        contract.NightRate.Should().BeNull();
        contract.HolidayRate.Should().BeNull();
        contract.SaRate.Should().BeNull();
        contract.SoRate.Should().BeNull();
        contract.ValidFrom.Should().Be(default(DateTime));
        contract.ValidUntil.Should().BeNull();
        contract.CalendarSelectionId.Should().BeNull();
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
            GuaranteedHours = guaranteedHours,
            MaximumHours = maxHours,
            MinimumHours = minHours,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            CalendarSelectionId = calendarSelectionId,
            CalendarSelection = calendarSelection
        };

        // Assert
        contract.Id.Should().Be(id);
        contract.Name.Should().Be(name);
        contract.GuaranteedHours.Should().Be(guaranteedHours);
        contract.MaximumHours.Should().Be(maxHours);
        contract.MinimumHours.Should().Be(minHours);
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
            GuaranteedHours = 160.5m,
            MaximumHours = 200.75m,
            MinimumHours = 120.25m
        };

        // Assert
        contract.GuaranteedHours.Should().Be(160.5m);
        contract.MaximumHours.Should().Be(200.75m);
        contract.MinimumHours.Should().Be(120.25m);
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