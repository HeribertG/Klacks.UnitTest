// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that IndividualSort is correctly mapped from request filter to the internal work filter.
/// </summary>
using NUnit.Framework;
using Shouldly;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Handlers.ScheduleEntries;

namespace Klacks.UnitTest.Application.Handlers.ClientFilter;

[TestFixture]
public class IndividualSortMappingTests
{
    [Test]
    public void CreateWorkFilter_WhenIndividualSortTrue_MapsIndividualSortTrue()
    {
        var filter = new WorkScheduleFilter
        {
            IndividualSort = true,
            OrderBy = "name",
            SortOrder = "asc"
        };

        var result = GetScheduleEntriesQueryHandler.CreateWorkFilter(
            filter,
            DateOnly.FromDateTime(DateTime.Today),
            DateOnly.FromDateTime(DateTime.Today));

        result.IndividualSort.ShouldBeTrue();
    }

    [Test]
    public void CreateWorkFilter_WhenIndividualSortFalse_MapsIndividualSortFalse()
    {
        var filter = new WorkScheduleFilter
        {
            IndividualSort = false,
            OrderBy = "name",
            SortOrder = "asc"
        };

        var result = GetScheduleEntriesQueryHandler.CreateWorkFilter(
            filter,
            DateOnly.FromDateTime(DateTime.Today),
            DateOnly.FromDateTime(DateTime.Today));

        result.IndividualSort.ShouldBeFalse();
    }
}
