// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationShiftSelector: picks the first shift that has not ended yet, converts its
/// start from the company time zone to UTC (positive and negative offsets, a night shift across
/// midnight, summer vs. winter offset, the spring-forward gap, the fall-back ambiguity), and renders the
/// shift context text including the shift name as stored.
/// </summary>

using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Domain.Services.Inbound;

namespace Klacks.UnitTest.Domain.Services.Inbound;

[TestFixture]
public class ClarificationShiftSelectorTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    private static TimeZoneInfo FixedOffsetZone(int offsetHours)
    {
        var id = $"Test{offsetHours:+0;-0;0}";
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(offsetHours), id, id);
    }

    private static TimeZoneInfo DaylightSavingZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("TestDst+1", TimeSpan.FromHours(1), "TestDst+1", "TestDst+1", "TestDst+1 Summer", [rule]);
    }

    [Test]
    public void LateShiftToday_PositiveOffset_StartsAtNoonUtc()
    {
        var shifts = new[] { new ClarificationShift(Day, new TimeOnly(14, 0), new TimeOnly(22, 0), "Spätdienst") };

        var selected = ClarificationShiftSelector.SelectNext(shifts, new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc), FixedOffsetZone(2));

        selected.ShouldNotBeNull();
        selected.StartUtc.ShouldBe(new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc));
        selected.StartUtc.Kind.ShouldBe(DateTimeKind.Utc);
        selected.Context.ShouldBe("Spätdienst 2026-09-23 14:00-22:00");
    }

    [Test]
    public void NegativeOffset_ConvertsCorrectly()
    {
        var shifts = new[] { new ClarificationShift(Day, new TimeOnly(8, 0), new TimeOnly(16, 0), "Day") };

        var selected = ClarificationShiftSelector.SelectNext(shifts, new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc), FixedOffsetZone(-5));

        selected!.StartUtc.ShouldBe(new DateTime(2026, 9, 23, 13, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void EndedShift_IsSkippedInFavourOfTheNextOne()
    {
        var shifts = new[]
        {
            new ClarificationShift(Day, new TimeOnly(6, 0), new TimeOnly(14, 0), "Frühdienst"),
            new ClarificationShift(Day, new TimeOnly(14, 0), new TimeOnly(22, 0), "Spätdienst")
        };

        var selected = ClarificationShiftSelector.SelectNext(shifts, new DateTime(2026, 9, 23, 13, 0, 0, DateTimeKind.Utc), FixedOffsetZone(2));

        selected!.Context.ShouldStartWith("Spätdienst");
    }

    [Test]
    public void RunningNightShift_AcrossMidnight_IsSelected()
    {
        var shifts = new[] { new ClarificationShift(new DateOnly(2026, 9, 22), new TimeOnly(22, 0), new TimeOnly(6, 0), "Nachtdienst") };

        var selected = ClarificationShiftSelector.SelectNext(shifts, new DateTime(2026, 9, 23, 2, 0, 0, DateTimeKind.Utc), FixedOffsetZone(2));

        selected.ShouldNotBeNull();
        selected.StartUtc.ShouldBe(new DateTime(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void AllShiftsEnded_ReturnsNull()
    {
        var shifts = new[] { new ClarificationShift(Day, new TimeOnly(6, 0), new TimeOnly(14, 0), "Frühdienst") };

        ClarificationShiftSelector.SelectNext(shifts, new DateTime(2026, 9, 23, 20, 0, 0, DateTimeKind.Utc), FixedOffsetZone(2)).ShouldBeNull();
    }

    [Test]
    public void NoShifts_ReturnsNull()
    {
        ClarificationShiftSelector.SelectNext([], new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc), FixedOffsetZone(2)).ShouldBeNull();
    }

    [Test]
    public void ShiftWithoutName_ContextHasOnlyDateAndTimes()
    {
        ClarificationShiftSelector.Describe(new ClarificationShift(Day, new TimeOnly(7, 30), new TimeOnly(16, 0), string.Empty))
            .ShouldBe("2026-09-23 07:30-16:00");
    }

    [Test]
    public void ShiftName_IsKeptAsStored_WithStationWords()
    {
        ClarificationShiftSelector.Describe(new ClarificationShift(Day, new TimeOnly(6, 0), new TimeOnly(14, 0), "  Frühdienst Chirurgie "))
            .ShouldBe("Frühdienst Chirurgie 2026-09-23 06:00-14:00");
    }

    [Test]
    public void SameLocalStart_UsesSummerOffsetInSummerAndWinterOffsetInWinter()
    {
        var zone = DaylightSavingZone();

        ClarificationShiftSelector.ToUtc(new DateOnly(2026, 9, 23), new TimeOnly(14, 0), zone)
            .ShouldBe(new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc));
        ClarificationShiftSelector.ToUtc(new DateOnly(2026, 11, 23), new TimeOnly(14, 0), zone)
            .ShouldBe(new DateTime(2026, 11, 23, 13, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void StartInsideTheSpringForwardGap_MovesToTheFirstValidInstant()
    {
        var utc = ClarificationShiftSelector.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), DaylightSavingZone());

        utc.ShouldBe(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc));
        utc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void StartInsideTheFallBackOverlap_ResolvesToTheEarlierOccurrence()
    {
        var utc = ClarificationShiftSelector.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), DaylightSavingZone());

        utc.ShouldBe(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc));
    }

    [Test]
    public void NightShiftEndingAfterTheFallBack_StillRunning_IsSelected()
    {
        var shifts = new[] { new ClarificationShift(new DateOnly(2026, 10, 24), new TimeOnly(22, 0), new TimeOnly(6, 0), "Nachtdienst") };

        var selected = ClarificationShiftSelector.SelectNext(shifts, new DateTime(2026, 10, 25, 4, 30, 0, DateTimeKind.Utc), DaylightSavingZone());

        selected.ShouldNotBeNull();
        selected.StartUtc.ShouldBe(new DateTime(2026, 10, 24, 20, 0, 0, DateTimeKind.Utc));
    }
}
