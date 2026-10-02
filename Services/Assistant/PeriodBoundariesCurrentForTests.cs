// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins PeriodBoundaries.CurrentFor: the running pay period that contains today, ending the day before
/// NextPeriodBoundaries.ComputeStart so the running and the next period always abut.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodBoundariesCurrentForTests
{
    private static Group MakeGroup(PaymentInterval interval, DateTime? validFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Bern",
        PaymentInterval = interval,
        ValidFrom = validFrom ?? new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
    };

    [TestCase(PaymentInterval.Monthly)]
    [TestCase(PaymentInterval.MonthlyTargetHours)]
    public void Monthly_IsTheCalendarMonthOfToday(PaymentInterval interval)
    {
        var (start, end) = PeriodBoundaries.CurrentFor(
            MakeGroup(interval), new DateOnly(2026, 2, 14), new DateOnly(2026, 2, 16));

        start.ShouldBe(new DateOnly(2026, 2, 1));
        end.ShouldBe(new DateOnly(2026, 2, 28));
    }

    [Test]
    public void Weekly_IsTheConfiguredWeekOfToday()
    {
        var (start, end) = PeriodBoundaries.CurrentFor(
            MakeGroup(PaymentInterval.Weekly), new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 5));

        start.ShouldBe(new DateOnly(2026, 9, 28));
        end.ShouldBe(new DateOnly(2026, 10, 4));
    }

    [Test]
    public void Biweekly_FollowsTheValidFromAnchor()
    {
        var group = MakeGroup(PaymentInterval.Biweekly, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        var (start, end) = PeriodBoundaries.CurrentFor(group, new DateOnly(2026, 1, 20), new DateOnly(2026, 1, 26));

        start.ShouldBe(new DateOnly(2026, 1, 19));
        end.ShouldBe(new DateOnly(2026, 2, 1));
    }

    [TestCase(PaymentInterval.Weekly)]
    [TestCase(PaymentInterval.Biweekly)]
    [TestCase(PaymentInterval.Monthly)]
    public void TheRunningPeriodContainsToday_AndEndsTheDayBeforeTheNextOne(PaymentInterval interval)
    {
        var group = MakeGroup(interval);
        var today = new DateOnly(2026, 3, 31);
        var nextWeekStart = new DateOnly(2026, 4, 6);

        var (start, end) = PeriodBoundaries.CurrentFor(group, today, nextWeekStart);

        start.ShouldBeLessThanOrEqualTo(today);
        end.ShouldBeGreaterThanOrEqualTo(today);
        end.AddDays(1).ShouldBe(NextPeriodBoundaries.ComputeStart(group, today, nextWeekStart));
    }

    [Test]
    public void Individual_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            PeriodBoundaries.CurrentFor(MakeGroup(PaymentInterval.Individual), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 6)));
    }
}
