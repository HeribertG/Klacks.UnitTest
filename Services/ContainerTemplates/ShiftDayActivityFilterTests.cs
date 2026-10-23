// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Table tests of ShiftDayActivityFilter, the slot rule of the available-task list and the container autofill, over an
/// in-memory query: every stored weekday (0=Sunday .. 6=Saturday) maps to its own flag in the normal slot and in the holiday
/// slot of that weekday, an out-of-range weekday does not restrict the weekday, and the generic holiday slot (every public
/// holiday, also when IsWeekdayAndHoliday is not given) only takes the shifts that run on every holiday.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Shouldly;

namespace Klacks.UnitTest.Services.ContainerTemplates;

[TestFixture]
public class ShiftDayActivityFilterTests
{
    private const int OutOfRangeWeekday = -1;
    private const int DaysPerWeek = 7;

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public void NormalDay_KeepsExactlyTheShiftsTickedOnThatWeekday_WhateverTheirHolidayFlags(int weekday)
    {
        // Arrange
        var shifts = Enumerable.Range(0, DaysPerWeek).Select(day => Ticked(day, isHoliday: day % 2 == 0)).ToList();

        // Act
        var result = shifts.AsQueryable().Where(ShiftDayActivityFilter.RunsOn(weekday, isHoliday: false, isWeekdayAndHoliday: false));

        // Assert
        result.ShouldBe([shifts[weekday]]);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public void HolidaySlotOfAWeekday_KeepsHolidayShiftsAndTheWeekdayAndHolidayShiftOfThatWeekday(int weekday)
    {
        // Arrange
        var weekdayAndHoliday = Enumerable.Range(0, DaysPerWeek).Select(day => Ticked(day, isWeekdayAndHoliday: true)).ToList();
        var plain = Enumerable.Range(0, DaysPerWeek).Select(day => Ticked(day)).ToList();
        var pureHoliday = new Shift { Id = Guid.NewGuid(), IsHoliday = true };

        // Act
        var result = weekdayAndHoliday.Concat(plain).Append(pureHoliday).AsQueryable()
            .Where(ShiftDayActivityFilter.RunsOn(weekday, isHoliday: true, isWeekdayAndHoliday: true));

        // Assert
        result.ShouldBe([weekdayAndHoliday[weekday], pureHoliday], ignoreOrder: true);
    }

    [Test]
    public void OutOfRangeWeekday_DoesNotRestrictTheWeekday()
    {
        // Arrange
        var monday = Ticked(1);
        var tuesdayAndHoliday = Ticked(2, isWeekdayAndHoliday: true);
        var nothingTicked = new Shift { Id = Guid.NewGuid() };
        var shifts = new[] { monday, tuesdayAndHoliday, nothingTicked }.AsQueryable();

        // Act
        var normalDay = shifts.Where(ShiftDayActivityFilter.RunsOn(OutOfRangeWeekday, isHoliday: null, isWeekdayAndHoliday: null));
        var holiday = shifts.Where(ShiftDayActivityFilter.RunsOn(OutOfRangeWeekday, isHoliday: true, isWeekdayAndHoliday: true));

        // Assert
        normalDay.ShouldBe([monday, tuesdayAndHoliday, nothingTicked], ignoreOrder: true);
        holiday.ShouldBe([tuesdayAndHoliday]);
    }

    [TestCase(false)]
    [TestCase(null)]
    public void GenericHolidaySlot_KeepsOnlyShiftsThatRunOnEveryHoliday_WhateverTheWeekday(bool? isWeekdayAndHoliday)
    {
        // Arrange
        var pureHoliday = new Shift { Id = Guid.NewGuid(), IsHoliday = true };
        var mondayAndHoliday = Ticked(1, isHoliday: true);
        var everyWeekdayAndHoliday = new Shift
        {
            Id = Guid.NewGuid(),
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            IsWeekdayAndHoliday = true
        };
        var mondayWeekdayAndHoliday = Ticked(1, isWeekdayAndHoliday: true);
        var monday = Ticked(1);
        var shifts = new[] { pureHoliday, mondayAndHoliday, everyWeekdayAndHoliday, mondayWeekdayAndHoliday, monday }.AsQueryable();

        // Act
        var result = shifts.Where(ShiftDayActivityFilter.RunsOn(1, isHoliday: true, isWeekdayAndHoliday: isWeekdayAndHoliday));

        // Assert
        result.ShouldBe([pureHoliday, mondayAndHoliday, everyWeekdayAndHoliday], ignoreOrder: true);
    }

    private static Shift Ticked(int weekday, bool isHoliday = false, bool isWeekdayAndHoliday = false)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            IsSunday = weekday == 0,
            IsMonday = weekday == 1,
            IsTuesday = weekday == 2,
            IsWednesday = weekday == 3,
            IsThursday = weekday == 4,
            IsFriday = weekday == 5,
            IsSaturday = weekday == 6,
            IsHoliday = isHoliday,
            IsWeekdayAndHoliday = isWeekdayAndHoliday
        };
    }
}
