using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using NUnit.Framework;

namespace Klacks.UnitTest.Models;

[TestFixture]
public class ContractTests
{
    [Test]
    public void Contract_ShouldInitializeWithDefaultValues()
    {
        // Arrange & Act
        var contract = new Contract();

        // Assert
        contract.Id.ShouldBe(Guid.Empty);
        contract.Name.ShouldBeEmpty();
        contract.GuaranteedHours.ShouldBeNull();
        contract.MaximumHours.ShouldBeNull();
        contract.MinimumHours.ShouldBeNull();
        contract.FullTime.ShouldBeNull();
        contract.NightRate.ShouldBeNull();
        contract.HolidayRate.ShouldBeNull();
        contract.SaRate.ShouldBeNull();
        contract.SoRate.ShouldBeNull();
        contract.ValidFrom.ShouldBe(default(DateTime));
        contract.ValidUntil.ShouldBeNull();
        contract.CalendarSelectionId.ShouldBeNull();
        contract.CalendarSelection.ShouldBeNull();
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
        contract.Id.ShouldBe(id);
        contract.Name.ShouldBe(name);
        contract.GuaranteedHours.ShouldBe(guaranteedHours);
        contract.MaximumHours.ShouldBe(maxHours);
        contract.MinimumHours.ShouldBe(minHours);
        contract.ValidFrom.ShouldBe(validFrom);
        contract.ValidUntil.ShouldBe(validUntil);
        contract.CalendarSelectionId.ShouldBe(calendarSelectionId);
        contract.CalendarSelection.ShouldBe(calendarSelection);
    }

    [Test]
    public void Contract_ShouldInheritFromBaseEntity()
    {
        // Act
        var contract = new Contract();

        // Assert
        contract.ShouldBeAssignableTo<BaseEntity>();
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
        contract.ValidUntil.ShouldBeNull();
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
        contract.GuaranteedHours.ShouldBe(160.5m);
        contract.MaximumHours.ShouldBe(200.75m);
        contract.MinimumHours.ShouldBe(120.25m);
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
        contract.Name.ShouldNotBeEmpty();
        contract.ValidFrom.ShouldBeGreaterThan(DateTime.MinValue);
        contract.CalendarSelectionId.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public void Contract_WhenCreated_ShouldHaveBaseEntityProperties()
    {
        // Arrange
        var contract = new Contract();

        // Assert
        contract.CreateTime.ShouldBeNull();
        contract.UpdateTime.ShouldBeNull();
        contract.DeletedTime.ShouldBeNull();
        contract.IsDeleted.ShouldBeFalse();
        contract.CurrentUserCreated.ShouldBe(string.Empty);
        contract.CurrentUserUpdated.ShouldBe(string.Empty);
        contract.CurrentUserDeleted.ShouldBe(string.Empty);
    }
}