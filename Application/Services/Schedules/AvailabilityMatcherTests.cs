// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AvailabilityMatcher (positive, per-date semantics). A date with no record is open for
/// work; a date with at least one available hour is positively constrained (only those hours are usable,
/// so any shift hour not marked available blocks the client); a date carrying only unavailable records
/// keeps legacy negative semantics. Verifies the exclusive-end hour boundary (08:00-16:00 occupies
/// 8..15, not 16) and the cross-midnight same-date-only window.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class AvailabilityMatcherTests
{
    private static readonly DateOnly D = new(2026, 3, 10);

    private static ClientAvailability Available(int hour) => new() { Date = D, Hour = hour, IsAvailable = true };

    private static ClientAvailability Unavailable(int hour) => new() { Date = D, Hour = hour, IsAvailable = false };

    private static bool Match(IReadOnlyList<ClientAvailability> entries, TimeOnly start, TimeOnly end)
        => AvailabilityMatcher.IsUnavailable(entries, D, start, end);

    // --- open day: no record for the requested date ---

    [Test]
    public void NoEntries_NotUnavailable()
        => Match([], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();

    [Test]
    public void RecordsOnOtherDateOnly_NotUnavailable()
    {
        var otherDay = new ClientAvailability { Date = D.AddDays(1), Hour = 9, IsAvailable = true };
        Match([otherDay], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();
    }

    // --- positive day: at least one available hour marked ---

    [Test]
    public void PositiveDay_ShiftFullyWithinAvailableHours_NotUnavailable()
    {
        var entries = new[] { Available(8), Available(9), Available(10), Available(11) };
        Match(entries, new TimeOnly(8, 0), new TimeOnly(12, 0)).ShouldBeFalse();
    }

    [Test]
    public void PositiveDay_ShiftHourOutsideAvailable_IsUnavailable()
    {
        var entries = new[] { Available(8), Available(9) };
        Match(entries, new TimeOnly(8, 0), new TimeOnly(12, 0)).ShouldBeTrue();
    }

    [Test]
    public void PositiveDay_PartialOverlapWithAvailable_IsUnavailable()
    {
        var entries = new[] { Available(8), Available(9), Available(10), Available(11) };
        Match(entries, new TimeOnly(10, 0), new TimeOnly(14, 0)).ShouldBeTrue();
    }

    [Test]
    public void PositiveDay_AvailableExclusiveEndHourNotRequired_NotUnavailable()
    {
        var entries = new[]
        {
            Available(8), Available(9), Available(10), Available(11),
            Available(12), Available(13), Available(14), Available(15),
        };
        Match(entries, new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();
    }

    [Test]
    public void PositiveDay_EndWithMinutesRequiresLastHour_IsUnavailable()
    {
        var entries = new[]
        {
            Available(8), Available(9), Available(10), Available(11),
            Available(12), Available(13), Available(14), Available(15),
        };
        Match(entries, new TimeOnly(8, 0), new TimeOnly(16, 30)).ShouldBeTrue();
    }

    [Test]
    public void PositiveDay_CrossMidnight_SameDayPortionFullyAvailable_NotUnavailable()
    {
        var entries = new[] { Available(22), Available(23) };
        Match(entries, new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBeFalse();
    }

    [Test]
    public void PositiveDay_CrossMidnight_SameDayPortionPartiallyAvailable_IsUnavailable()
    {
        var entries = new[] { Available(22) };
        Match(entries, new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBeTrue();
    }

    // --- legacy negative day: only unavailable records, no available hour ---

    [Test]
    public void NegativeDay_UnavailableHourInsideShift_IsUnavailable()
        => Match([Unavailable(10)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeTrue();

    [Test]
    public void NegativeDay_UnavailableAtStartHour_IsUnavailable()
        => Match([Unavailable(8)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeTrue();

    [Test]
    public void NegativeDay_UnavailableAtExclusiveEndHour_NotUnavailable()
        => Match([Unavailable(16)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();

    [Test]
    public void NegativeDay_UnavailableBeforeWindow_NotUnavailable()
        => Match([Unavailable(7)], new TimeOnly(8, 0), new TimeOnly(16, 0)).ShouldBeFalse();

    [Test]
    public void NegativeDay_CrossMidnightSameDateHour_IsUnavailable()
        => Match([Unavailable(23)], new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBeTrue();

    [Test]
    public void NegativeDay_CrossMidnightNextDayHour_NotUnavailable_DocumentedV1Limitation()
        => Match([Unavailable(2)], new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBeFalse();
}
