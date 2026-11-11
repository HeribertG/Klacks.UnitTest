// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for <see cref="RecoveryReliefDays.Around"/>: the days within the relief reach of the engine on both sides
/// of an absence, without the absence days, cut at the window the snapshot reads, ascending and without duplicates.
/// </summary>
[TestFixture]
public sealed class RecoveryReliefDaysTests
{
    private const int Reach = RulesetDefaults.ReliefReachDays;
    private static readonly DateOnly AbsenceDay = new(2026, 6, 10);
    private static readonly DateOnly WideWindowStart = new(2026, 5, 1);
    private static readonly DateOnly WideWindowEnd = new(2026, 7, 31);

    private static IEnumerable<DateOnly> DaysFrom(DateOnly first, DateOnly last)
    {
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    [Test]
    public void OneAbsenceDay_GivesTheReachOnBothSides_WithoutTheDayItself()
    {
        // Arrange
        var expected = DaysFrom(AbsenceDay.AddDays(-Reach), AbsenceDay.AddDays(Reach)).Where(day => day != AbsenceDay).ToList();

        // Act
        var days = RecoveryReliefDays.Around([AbsenceDay], WideWindowStart, WideWindowEnd);

        // Assert
        days.ShouldBe(expected);
        days.Count.ShouldBe(2 * Reach);
        days[0].ShouldBe(AbsenceDay.AddDays(-Reach));
        days[^1].ShouldBe(AbsenceDay.AddDays(Reach));
        days.ShouldNotContain(AbsenceDay);
    }

    [Test]
    public void DaysOutsideTheWindow_AreLeftOut_AndTheWindowEdgesStayIn()
    {
        // Arrange: the window ends two days before and three days after the absence day.
        var windowStart = AbsenceDay.AddDays(-2);
        var windowEnd = AbsenceDay.AddDays(3);

        // Act
        var days = RecoveryReliefDays.Around([AbsenceDay], windowStart, windowEnd);

        // Assert
        days.ShouldBe(
        [
            AbsenceDay.AddDays(-2), AbsenceDay.AddDays(-1), AbsenceDay.AddDays(1), AbsenceDay.AddDays(2), AbsenceDay.AddDays(3),
        ]);
    }

    [Test]
    public void AnAbsenceOfSeveralDays_ListsEveryReliefDayOnce_AndNoneOfItsOwnDays()
    {
        // Arrange: three absence days in a row; their reaches overlap almost completely.
        var lastAbsenceDay = AbsenceDay.AddDays(2);
        DateOnly[] absence = [AbsenceDay, AbsenceDay.AddDays(1), lastAbsenceDay];
        var expected = DaysFrom(AbsenceDay.AddDays(-Reach), AbsenceDay.AddDays(-1))
            .Concat(DaysFrom(lastAbsenceDay.AddDays(1), lastAbsenceDay.AddDays(Reach)))
            .ToList();

        // Act
        var days = RecoveryReliefDays.Around(absence, WideWindowStart, WideWindowEnd);

        // Assert
        days.ShouldBe(expected);
        days.Distinct().Count().ShouldBe(days.Count);
        days.Intersect(absence).ShouldBeEmpty();
    }

    [Test]
    public void TheDaysBetweenTwoAbsenceDays_AreReliefDays_InAscendingOrder()
    {
        // Arrange: the absence days are passed out of order and lie three days apart.
        var laterAbsenceDay = AbsenceDay.AddDays(3);

        // Act
        var days = RecoveryReliefDays.Around([laterAbsenceDay, AbsenceDay], WideWindowStart, WideWindowEnd);

        // Assert
        days.ShouldContain(AbsenceDay.AddDays(1));
        days.ShouldContain(AbsenceDay.AddDays(2));
        days.ShouldNotContain(AbsenceDay);
        days.ShouldNotContain(laterAbsenceDay);
        days.ShouldBe(days.OrderBy(day => day.DayNumber).ToList());
        days[0].ShouldBe(AbsenceDay.AddDays(-Reach));
        days[^1].ShouldBe(laterAbsenceDay.AddDays(Reach));
    }

    [Test]
    public void AWindowThatHoldsOnlyTheAbsenceDay_GivesNoReliefDay()
    {
        // Act
        var days = RecoveryReliefDays.Around([AbsenceDay], AbsenceDay, AbsenceDay);

        // Assert
        days.ShouldBeEmpty();
    }

    [Test]
    public void NoAbsenceDay_GivesNoReliefDay()
    {
        // Act
        var days = RecoveryReliefDays.Around([], WideWindowStart, WideWindowEnd);

        // Assert
        days.ShouldBeEmpty();
    }
}
