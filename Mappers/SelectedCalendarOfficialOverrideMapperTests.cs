// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the Mapperly-generated ScheduleMapper round-trips SelectedCalendar.OfficialOverride
/// between entity and resource in both directions, so the tri-state override survives JSON persistence
/// through the aggregate CalendarSelection endpoint.
/// </summary>

namespace Klacks.UnitTest.Mappers;

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.CalendarSelections;

[TestFixture]
public class SelectedCalendarOfficialOverrideMapperTests
{
    private ScheduleMapper _mapper = null!;

    [SetUp]
    public void SetUp() => _mapper = new ScheduleMapper();

    [TestCase(null)]
    [TestCase(true)]
    [TestCase(false)]
    public void ToSelectedCalendarResource_CarriesOfficialOverride(bool? officialOverride)
    {
        var entity = new SelectedCalendar
        {
            Id = Guid.NewGuid(),
            CalendarSelectionId = Guid.NewGuid(),
            Country = "US",
            State = "US",
            OfficialOverride = officialOverride
        };

        var resource = _mapper.ToSelectedCalendarResource(entity);

        resource.OfficialOverride.ShouldBe(officialOverride);
    }

    [TestCase(null)]
    [TestCase(true)]
    [TestCase(false)]
    public void ToSelectedCalendarEntity_CarriesOfficialOverride(bool? officialOverride)
    {
        var resource = new SelectedCalendarResource
        {
            Id = Guid.NewGuid(),
            CalendarSelectionId = Guid.NewGuid(),
            Country = "US",
            State = "US",
            OfficialOverride = officialOverride
        };

        var entity = _mapper.ToSelectedCalendarEntity(resource);

        entity.OfficialOverride.ShouldBe(officialOverride);
    }
}
