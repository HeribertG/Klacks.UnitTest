// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the date rules of the autonomous period close: which period is due on a given day
/// (PeriodCloseDateCalculator.DuePeriodEndBound + PeriodBoundaries.LastEndBefore, shared with
/// PeriodOverdueDetector), the first allowed day and the window after it. Covers month ends, weekly periods
/// with the configured week start, biweekly cycles anchored on ValidFrom, and the lag-0 rule that a period is
/// never closed on its own last day.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodAutoCloseBoundaryTests
{
    private static Group MakeGroup(PaymentInterval interval, DateTime? validFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Bern",
        PaymentInterval = interval,
        ValidFrom = validFrom ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static DateOnly D(string isoDate) =>
        DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static DateOnly MondayWeekStart(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));

    private static DateOnly DuePeriodEnd(Group group, DateOnly today, int lag)
    {
        var reference = PeriodCloseDateCalculator.DuePeriodEndBound(today, lag);
        return PeriodBoundaries.LastEndBefore(group, reference, MondayWeekStart(reference));
    }

    [TestCase("2026-08-31", 0, "2026-07-31")]
    [TestCase("2026-09-01", 0, "2026-08-31")]
    [TestCase("2026-09-01", 1, "2026-08-31")]
    [TestCase("2026-09-04", 5, "2026-07-31")]
    [TestCase("2026-09-05", 5, "2026-08-31")]
    [TestCase("2026-03-01", 0, "2026-02-28")]
    public void Monthly_DuePeriodEnd_IsTheLatestEndWhoseCloseDateHasCome(string today, int lag, string expectedEnd)
    {
        var end = DuePeriodEnd(MakeGroup(PaymentInterval.Monthly), D(today), lag);

        Assert.That(end, Is.EqualTo(D(expectedEnd)));
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(D(today), end, lag), Is.True);
    }

    [Test]
    public void Weekly_OnTheLastDayOfTheWeekWithLagZero_TheRunningWeekIsNotDue()
    {
        var sunday = new DateOnly(2026, 9, 6);

        var end = DuePeriodEnd(MakeGroup(PaymentInterval.Weekly), sunday, 0);

        Assert.That(end, Is.EqualTo(new DateOnly(2026, 8, 30)));
    }

    [Test]
    public void Weekly_OnTheMondayAfter_ThePreviousWeekIsDue()
    {
        var monday = new DateOnly(2026, 9, 7);

        var end = DuePeriodEnd(MakeGroup(PaymentInterval.Weekly), monday, 0);

        Assert.That(end, Is.EqualTo(new DateOnly(2026, 9, 6)));
        Assert.That(PeriodBoundaries.StartFor(PaymentInterval.Weekly, end), Is.EqualTo(new DateOnly(2026, 8, 31)));
    }

    [Test]
    public void Biweekly_FollowsTheValidFromAnchor()
    {
        var group = MakeGroup(PaymentInterval.Biweekly, new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

        var dayAfterFirstCycle = DuePeriodEnd(group, new DateOnly(2026, 8, 17), 0);
        var lastDayOfSecondCycle = DuePeriodEnd(group, new DateOnly(2026, 8, 30), 0);

        Assert.Multiple(() =>
        {
            Assert.That(dayAfterFirstCycle, Is.EqualTo(new DateOnly(2026, 8, 16)));
            Assert.That(lastDayOfSecondCycle, Is.EqualTo(new DateOnly(2026, 8, 16)));
            Assert.That(PeriodBoundaries.StartFor(PaymentInterval.Biweekly, dayAfterFirstCycle), Is.EqualTo(new DateOnly(2026, 8, 3)));
        });
    }

    [Test]
    public void Individual_HasNoBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PeriodBoundaries.LastEndBefore(MakeGroup(PaymentInterval.Individual), new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 31)));
    }

    [TestCase(0, "2026-09-01")]
    [TestCase(1, "2026-09-01")]
    [TestCase(5, "2026-09-05")]
    public void FirstAutoCloseDay_IsTheCloseDateButNeverThePeriodEndItself(int lag, string expected)
    {
        Assert.That(
            PeriodCloseDateCalculator.FirstAutoCloseDay(new DateOnly(2026, 8, 31), lag),
            Is.EqualTo(D(expected)));
    }

    [TestCase("2026-08-31", false)]
    [TestCase("2026-09-01", true)]
    [TestCase("2026-09-04", true)]
    [TestCase("2026-09-05", false)]
    public void IsWithinAutoCloseWindow_CountsFromTheFirstAllowedDay(string today, bool expected)
    {
        Assert.That(
            PeriodCloseDateCalculator.IsWithinAutoCloseWindow(D(today), new DateOnly(2026, 8, 31), 0, 3),
            Is.EqualTo(expected));
    }
}
