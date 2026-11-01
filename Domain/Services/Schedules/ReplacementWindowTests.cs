// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// Which interval a replacement really occupies and when it occupies nothing: ReplacementStart and ReplacementEnd
/// decide by the hours handed over (a full 24-hour work included), every other type by its own span, whatever
/// ChangeTime says before the repository has derived it.
/// </summary>
[TestFixture]
public class ReplacementWindowTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Noon = new(12, 0);
    private static readonly TimeOnly Sixteen = new(16, 0);
    private static readonly TimeOnly TenPm = new(22, 0);
    private static readonly TimeOnly TwoAm = new(2, 0);
    private static readonly TimeOnly FiveAm = new(5, 0);
    private static readonly TimeOnly SixAm = new(6, 0);
    private static readonly TimeOnly Midnight = new(0, 0);
    private const decimal ShiftHours = 8m;
    private const decimal HalfShiftHours = 4m;
    private const decimal FullDayHours = 24m;

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(8)]
    public void Within_DecidesByItsOwnSpan_NotByChangeTime(int changeTime)
    {
        var found = ReplacementWindow.TryGetInterval(
            WorkChangeType.ReplacementWithin, Eight, Sixteen, Day, Ten, Noon, changeTime, out var startAt, out var endAt);

        found.ShouldBeTrue();
        startAt.ShouldBe(Day.ToDateTime(Ten));
        endAt.ShouldBe(Day.ToDateTime(Noon));
    }

    [Test]
    public void Within_WithEqualBounds_HandsNothingOver()
    {
        ReplacementWindow.TryGetInterval(
            WorkChangeType.ReplacementWithin, Eight, Sixteen, Day, Ten, Ten, ShiftHours, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void Within_AfterMidnightOfANightShift_LiesOnTheNextDay()
    {
        var found = ReplacementWindow.TryGetInterval(
            WorkChangeType.ReplacementWithin, TenPm, SixAm, Day, TwoAm, FiveAm, 0m, out var startAt, out var endAt);

        found.ShouldBeTrue();
        startAt.ShouldBe(Day.AddDays(1).ToDateTime(TwoAm));
        endAt.ShouldBe(Day.AddDays(1).ToDateTime(FiveAm));
    }

    [Test]
    public void Start_TakesTheFirstHoursOfTheWork()
    {
        var found = ReplacementWindow.TryGetInterval(
            WorkChangeType.ReplacementStart, Eight, Sixteen, Day, Midnight, Midnight, HalfShiftHours, out var startAt, out var endAt);

        found.ShouldBeTrue();
        startAt.ShouldBe(Day.ToDateTime(Eight));
        endAt.ShouldBe(Day.ToDateTime(Noon));
    }

    [Test]
    public void End_TakesTheLastHoursOfTheWork()
    {
        var found = ReplacementWindow.TryGetInterval(
            WorkChangeType.ReplacementEnd, Eight, Sixteen, Day, Midnight, Midnight, HalfShiftHours, out var startAt, out var endAt);

        found.ShouldBeTrue();
        startAt.ShouldBe(Day.ToDateTime(Noon));
        endAt.ShouldBe(Day.ToDateTime(Sixteen));
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    public void StartAndEnd_WithoutHours_HandNothingOver(WorkChangeType type)
    {
        ReplacementWindow.TryGetInterval(type, Eight, Sixteen, Day, Midnight, Midnight, 0m, out _, out _).ShouldBeFalse();
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    public void StartAndEnd_OfAWorkOf24Hours_TakeTheWholeDay(WorkChangeType type)
    {
        var found = ReplacementWindow.TryGetInterval(
            type, Eight, Eight, Day, Midnight, Midnight, FullDayHours, out var startAt, out var endAt);

        found.ShouldBeTrue();
        startAt.ShouldBe(Day.ToDateTime(Eight));
        endAt.ShouldBe(Day.AddDays(1).ToDateTime(Eight));
    }

    [Test]
    public void Start_LongerThanTheWork_IsLimitedToTheWork()
    {
        var found = ReplacementWindow.TryGetInterval(
            WorkChangeType.ReplacementStart, Eight, Sixteen, Day, Midnight, Midnight, FullDayHours, out var startAt, out var endAt);

        found.ShouldBeTrue();
        startAt.ShouldBe(Day.ToDateTime(Eight));
        endAt.ShouldBe(Day.ToDateTime(Sixteen));
    }
}
