// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class PayPeriodTargetHoursProratorTests
{
    private const decimal MonthlyTarget = 180m;
    private const decimal Precision = 0.0000001m;

    [Test]
    public void Weekly_SevenDays_YieldExactlyTheWeeklyTarget()
    {
        var total = Sum(PaymentInterval.Weekly, 42m, new DateOnly(2026, 3, 2), days: 7);

        total.ShouldBe(42m, Precision);
    }

    [Test]
    public void Biweekly_SevenDays_YieldHalfTheTarget()
    {
        var total = Sum(PaymentInterval.Biweekly, 84m, new DateOnly(2026, 3, 2), days: 7);

        total.ShouldBe(42m, Precision);
    }

    [Test]
    public void Monthly_SevenDaysOfJanuary_YieldSevenThirtyFirsts()
    {
        var total = Sum(PaymentInterval.Monthly, MonthlyTarget, new DateOnly(2026, 1, 5), days: 7);

        total.ShouldBe(MonthlyTarget * 7m / 31m, Precision);
    }

    [Test]
    public void Monthly_FullFebruary_YieldExactlyTheMonthlyTarget()
    {
        var total = Sum(PaymentInterval.Monthly, MonthlyTarget, new DateOnly(2026, 2, 1), days: 28);

        total.ShouldBe(MonthlyTarget, Precision);
    }

    [Test]
    public void Monthly_FullLeapFebruary_YieldExactlyTheMonthlyTarget()
    {
        var total = Sum(PaymentInterval.Monthly, MonthlyTarget, new DateOnly(2028, 2, 1), days: 29);

        total.ShouldBe(MonthlyTarget, Precision);
    }

    [Test]
    public void MonthlyTargetHours_PartialMonth_UsesCalendarMonthLength()
    {
        var total = Sum(PaymentInterval.MonthlyTargetHours, 160m, new DateOnly(2026, 4, 21), days: 10);

        total.ShouldBe(160m * 10m / 30m, Precision);
    }

    [Test]
    public void Monthly_RangeCrossingMonthBoundary_TakesEachDayFromItsOwnMonth()
    {
        // 2026-01-29..2026-02-04: three January days (31-day month), four February days (28-day month).
        var start = new DateOnly(2026, 1, 29);
        var total = 0m;
        for (var d = 0; d < 7; d++)
        {
            var date = start.AddDays(d);
            var monthValue = date.Month == 1 ? 184m : 160m;
            total += PayPeriodTargetHoursProrator.DailyShare(monthValue, PaymentInterval.Monthly, date);
        }

        total.ShouldBe((184m * 3m / 31m) + (160m * 4m / 28m), Precision);
    }

    [Test]
    public void Individual_WithPeriods_UsesTheResolvedPeriodLength()
    {
        var periods = new List<Period>
        {
            new() { Id = Guid.NewGuid(), FromDate = new DateOnly(2026, 1, 1), UntilDate = new DateOnly(2026, 1, 20) },
            new() { Id = Guid.NewGuid(), FromDate = new DateOnly(2026, 1, 21), UntilDate = new DateOnly(2026, 2, 19) },
        };

        PayPeriodTargetHoursProrator.PayPeriodLengthDays(PaymentInterval.Individual, new DateOnly(2026, 1, 10), periods)
            .ShouldBe(20);
        PayPeriodTargetHoursProrator.PayPeriodLengthDays(PaymentInterval.Individual, new DateOnly(2026, 2, 1), periods)
            .ShouldBe(30);
    }

    [Test]
    public void Individual_WithoutResolvablePeriod_FallsBackToCalendarMonth()
    {
        var uncovered = new List<Period>
        {
            new() { Id = Guid.NewGuid(), FromDate = new DateOnly(2026, 5, 1), UntilDate = new DateOnly(2026, 5, 31) },
        };

        PayPeriodTargetHoursProrator.PayPeriodLengthDays(PaymentInterval.Individual, new DateOnly(2026, 4, 10), uncovered)
            .ShouldBe(30);
        PayPeriodTargetHoursProrator.PayPeriodLengthDays(PaymentInterval.Individual, new DateOnly(2026, 4, 10))
            .ShouldBe(30);
    }

    [Test]
    public void DailyShare_NonPositiveLength_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PayPeriodTargetHoursProrator.DailyShare(10m, 0));
    }

    private static decimal Sum(PaymentInterval interval, decimal guaranteedHours, DateOnly start, int days)
    {
        var total = 0m;
        for (var d = 0; d < days; d++)
        {
            total += PayPeriodTargetHoursProrator.DailyShare(guaranteedHours, interval, start.AddDays(d));
        }
        return total;
    }
}
