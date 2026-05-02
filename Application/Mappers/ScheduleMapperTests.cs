using Shouldly;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Mappers;

[TestFixture]
public class ScheduleMapperTests
{
    private ScheduleMapper _mapper = null!;

    [SetUp]
    public void Setup()
    {
        _mapper = new ScheduleMapper();
    }

    [Test]
    public void ToShiftResource_ValidShift_MapsBasicProperties()
    {
        // Arrange
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Morning Shift",
            Abbreviation = "MS",
            Description = "Morning work shift",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            FromDate = new DateOnly(2024, 1, 1),
            GroupItems = new List<GroupItem>()
        };

        // Act
        var result = _mapper.ToShiftResource(shift);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(shift.Id);
        result.Name.ShouldBe("Morning Shift");
    }

    [Test]
    public void ToShiftResource_ShiftWithGroupItems_MapsGroups()
    {
        // Arrange
        var group = new Group { Id = Guid.NewGuid(), Name = "Team A", Description = "First Team" };
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Morning Shift",
            GroupItems = new List<GroupItem>
            {
                new GroupItem
                {
                    GroupId = group.Id,
                    Group = group,
                    ValidFrom = new DateTime(2024, 1, 1),
                    ValidUntil = new DateTime(2024, 12, 31)
                }
            }
        };

        // Act
        var result = _mapper.ToShiftResource(shift);

        // Assert
        result.Groups.Count().ShouldBe(1);
        result.Groups[0].Id.ShouldBe(group.Id);
        result.Groups[0].Name.ShouldBe("Team A");
        result.Groups[0].Description.ShouldBe("First Team");
    }

    [Test]
    public void CloneShift_ValidShift_CreatesNewShiftWithEmptyIds()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Abbreviation = "OS",
            Description = "Original Description",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            FromDate = new DateOnly(2024, 1, 1),
            ClientId = Guid.NewGuid(),
            IsMonday = true,
            IsTuesday = true,
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() }
            }
        };

        // Act
        var result = _mapper.CloneShift(originalShift);

        // Assert
        result.Id.ShouldBe(Guid.Empty);
        result.Name.ShouldBe("Original Shift");
        result.Abbreviation.ShouldBe("OS");
        result.Description.ShouldBe("Original Description");
        result.StartShift.ShouldBe(new TimeOnly(8, 0));
        result.EndShift.ShouldBe(new TimeOnly(16, 0));
        result.FromDate.ShouldBe(new DateOnly(2024, 1, 1));
        result.ClientId.ShouldBe(originalShift.ClientId);
        result.IsMonday.ShouldBeTrue();
        result.IsTuesday.ShouldBeTrue();
        result.GroupItems.Count().ShouldBe(1);
        result.GroupItems[0].Id.ShouldBe(Guid.Empty);
        result.GroupItems[0].ShiftId.ShouldBe(Guid.Empty);
    }

    [Test]
    public void ToRouteInfoResource_ValidRouteInfo_MapsCorrectly()
    {
        // Arrange
        var routeInfo = new RouteInfo
        {
            TotalDistanceKm = 15.5,
            EstimatedTravelTime = "30 min",
            StartBase = "Zurich",
            EndBase = "Bern",
            OptimizedRoute = new List<RouteLocation>()
        };

        // Act
        var result = _mapper.ToRouteInfoResource(routeInfo);

        // Assert
        result.ShouldNotBeNull();
        result.TotalDistanceKm.ShouldBe(15.5);
        result.EstimatedTravelTime.ShouldBe("30 min");
        result.StartBase.ShouldBe("Zurich");
        result.EndBase.ShouldBe("Bern");
    }

    [Test]
    public void ToRouteLocationResource_ValidLocation_MapsCorrectly()
    {
        // Arrange
        var location = new RouteLocation
        {
            Latitude = 47.3769,
            Longitude = 8.5417,
            Name = "Zurich"
        };

        // Act
        var result = _mapper.ToRouteLocationResource(location);

        // Assert
        result.ShouldNotBeNull();
        result.Latitude.ShouldBe(47.3769);
        result.Longitude.ShouldBe(8.5417);
        result.Name.ShouldBe("Zurich");
    }

    [Test]
    public void ToContainerTemplateResource_ValidTemplate_MapsCorrectly()
    {
        // Arrange
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Shift",
            GroupItems = new List<GroupItem>()
        };
        var template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = shift.Id,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(17, 0),
            Weekday = 1,
            Shift = shift,
            ContainerTemplateItems = new List<ContainerTemplateItem>()
        };

        // Act
        var result = _mapper.ToContainerTemplateResource(template);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(template.Id);
        result.ContainerId.ShouldBe(template.ContainerId);
        result.FromTime.ShouldBe(new TimeOnly(8, 0));
        result.UntilTime.ShouldBe(new TimeOnly(17, 0));
    }

    [Test]
    public void ToShiftResource_ShiftWithExpenses_MapsDefaultExpenses()
    {
        var shiftId = Guid.NewGuid();
        var shift = new Shift
        {
            Id = shiftId,
            Name = "Shift with Expenses",
            GroupItems = [],
            ShiftExpenses =
            [
                new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = shiftId, Amount = 25.50m, Description = "Fahrtkosten", Taxable = true },
                new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = shiftId, Amount = 10.00m, Description = "Verpflegung", Taxable = false }
            ]
        };

        var result = _mapper.ToShiftResource(shift);

        result.DefaultExpenses.Count().ShouldBe(2);
        result.DefaultExpenses.ShouldContain(e => e.Description == "Fahrtkosten" && e.Amount == 25.50m && e.Taxable);
        result.DefaultExpenses.ShouldContain(e => e.Description == "Verpflegung" && e.Amount == 10.00m && !e.Taxable);
    }

    [Test]
    public void ToShiftResource_ShiftWithoutExpenses_ReturnsEmptyList()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift without Expenses",
            GroupItems = [],
            ShiftExpenses = []
        };

        var result = _mapper.ToShiftResource(shift);

        result.DefaultExpenses.ShouldBeEmpty();
    }

    [Test]
    public void ToShiftEntity_ResourceWithExpenses_MapsShiftExpenses()
    {
        var shiftId = Guid.NewGuid();
        var resource = new Api.Application.DTOs.Schedules.ShiftResource
        {
            Id = shiftId,
            Name = "Test",
            Groups = [],
            DefaultExpenses =
            [
                new Api.Application.DTOs.Schedules.ShiftExpensesResource { Id = Guid.NewGuid(), ShiftId = shiftId, Amount = 15m, Description = "Taxi", Taxable = true }
            ]
        };

        var result = _mapper.ToShiftEntity(resource);

        result.ShiftExpenses.Count().ShouldBe(1);
        result.ShiftExpenses[0].Amount.ShouldBe(15m);
        result.ShiftExpenses[0].Description.ShouldBe("Taxi");
    }

    [Test]
    public void CloneShift_WithExpenses_ClonesWithEmptyIds()
    {
        var originalExpenseId = Guid.NewGuid();
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            GroupItems = [],
            ShiftExpenses =
            [
                new ShiftExpenses { Id = originalExpenseId, ShiftId = Guid.NewGuid(), Amount = 20m, Description = "Parking", Taxable = true }
            ]
        };

        var result = _mapper.CloneShift(originalShift);

        result.ShiftExpenses.Count().ShouldBe(1);
        result.ShiftExpenses[0].Id.ShouldBe(Guid.Empty);
        result.ShiftExpenses[0].ShiftId.ShouldBe(Guid.Empty);
        result.ShiftExpenses[0].Amount.ShouldBe(20m);
        result.ShiftExpenses[0].Description.ShouldBe("Parking");
    }
}
