using FluentAssertions;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;

namespace UnitTest.Application.Mappers;

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
        result.Should().NotBeNull();
        result.Id.Should().Be(shift.Id);
        result.Name.Should().Be("Morning Shift");
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
        result.Groups.Should().HaveCount(1);
        result.Groups[0].Id.Should().Be(group.Id);
        result.Groups[0].Name.Should().Be("Team A");
        result.Groups[0].Description.Should().Be("First Team");
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
        result.Id.Should().Be(Guid.Empty);
        result.Name.Should().Be("Original Shift");
        result.Abbreviation.Should().Be("OS");
        result.Description.Should().Be("Original Description");
        result.StartShift.Should().Be(new TimeOnly(8, 0));
        result.EndShift.Should().Be(new TimeOnly(16, 0));
        result.FromDate.Should().Be(new DateOnly(2024, 1, 1));
        result.ClientId.Should().Be(originalShift.ClientId);
        result.IsMonday.Should().BeTrue();
        result.IsTuesday.Should().BeTrue();
        result.GroupItems.Should().HaveCount(1);
        result.GroupItems[0].Id.Should().Be(Guid.Empty);
        result.GroupItems[0].ShiftId.Should().Be(Guid.Empty);
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
        result.Should().NotBeNull();
        result.TotalDistanceKm.Should().Be(15.5);
        result.EstimatedTravelTime.Should().Be("30 min");
        result.StartBase.Should().Be("Zurich");
        result.EndBase.Should().Be("Bern");
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
        result.Should().NotBeNull();
        result.Latitude.Should().Be(47.3769);
        result.Longitude.Should().Be(8.5417);
        result.Name.Should().Be("Zurich");
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
        result.Should().NotBeNull();
        result.Id.Should().Be(template.Id);
        result.ContainerId.Should().Be(template.ContainerId);
        result.FromTime.Should().Be(new TimeOnly(8, 0));
        result.UntilTime.Should().Be(new TimeOnly(17, 0));
    }
}
