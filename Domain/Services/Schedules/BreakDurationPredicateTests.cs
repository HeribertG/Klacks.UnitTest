// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BreakDurationPredicate: equal bounds with recorded hours are a directly recorded duration; equal bounds
/// without hours are an empty entry, and a real time span is never one, whatever its WorkTime.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class BreakDurationPredicateTests
{
    private const decimal RecordedHours = 8m;

    private static readonly TimeOnly Morning = new(8, 0);
    private static readonly TimeOnly Noon = new(12, 0);

    [Test]
    public void EqualBoundsWithHours_IsADirectlyRecordedDuration()
    {
        BreakDurationPredicate.HasDirectlyRecordedDuration(Entry(TimeOnly.MinValue, TimeOnly.MinValue, RecordedHours))
            .ShouldBeTrue();
    }

    [Test]
    public void EqualBoundsWithoutHours_IsNotADirectlyRecordedDuration()
    {
        BreakDurationPredicate.HasDirectlyRecordedDuration(Entry(TimeOnly.MinValue, TimeOnly.MinValue, 0m))
            .ShouldBeFalse();
    }

    [Test]
    public void RealTimeSpanWithHours_IsNotADirectlyRecordedDuration()
    {
        BreakDurationPredicate.HasDirectlyRecordedDuration(Entry(Morning, Noon, RecordedHours))
            .ShouldBeFalse();
    }

    private static Break Entry(TimeOnly start, TimeOnly end, decimal workTime) =>
        new() { Id = Guid.NewGuid(), StartTime = start, EndTime = end, WorkTime = workTime };
}
