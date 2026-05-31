// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AvailabilityMatcher: only an explicit unavailable record overlapping an hour the
/// shift occupies blocks the client; missing or available-only records never block. Verifies the
/// exclusive-end hour boundary (e.g. 08:00-16:00 occupies 8..15, not 16) and the cross-midnight
/// same-date-only window.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class AvailabilityMatcherTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static ClientAvailability Unavailable(int hour) => new()
    {
        ClientId = Client,
        Date = new DateOnly(2026, 3, 10),
        Hour = hour,
        IsAvailable = false
    };

    private static bool Match(IReadOnlyList<ClientAvailability> entries, TimeOnly start, TimeOnly end)
        => AvailabilityMatcher.IsExplicitlyUnavailable(entries, start, end);

    [Test]
    public void NoEntries_NotUnavailable()
        => Match([], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();

    [Test]
    public void OnlyAvailableEntries_NotUnavailable()
    {
        var entries = new List<ClientAvailability>
        {
            new() { ClientId = Client, Hour = 8, IsAvailable = true }
        };
        Match(entries, new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();
    }

    [Test]
    public void UnavailableInsideWindow_IsUnavailable()
        => Match([Unavailable(10)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeTrue();

    [Test]
    public void UnavailableAtStartHour_IsUnavailable()
        => Match([Unavailable(8)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeTrue();

    [Test]
    public void UnavailableAtExclusiveEndHour_IsNotUnavailable()
        => Match([Unavailable(16)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();

    [Test]
    public void UnavailableAtLastOccupiedHour_IsUnavailable()
        => Match([Unavailable(15)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeTrue();

    [Test]
    public void EndWithMinutes_ExtendsLastOccupiedHour()
        => Match([Unavailable(16)], new TimeOnly(8, 0), new TimeOnly(16, 30)).ShouldBeTrue();

    [Test]
    public void UnavailableBeforeWindow_IsNotUnavailable()
        => Match([Unavailable(7)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();

    [Test]
    public void CrossMidnight_SameDateHour_IsUnavailable()
        => Match([Unavailable(23)], new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBeTrue();

    [Test]
    public void CrossMidnight_NextDayHour_IsNotUnavailable_DocumentedV1Limitation()
        => Match([Unavailable(2)], new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBeFalse();
}
