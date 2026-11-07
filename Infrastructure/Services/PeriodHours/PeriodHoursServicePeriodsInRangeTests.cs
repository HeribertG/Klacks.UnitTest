// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PeriodHoursService.GetPeriodsInRangeAsync: a date range is split into the real pay periods it touches
/// (the boundaries GetPeriodBoundariesAsync resolves for the configured payment interval), in date order, the first
/// period possibly starting before the range and the last possibly ending after it; an inverted range yields none, and a
/// range over more than PayPeriodLimits.MaxPeriodsInRange periods is refused.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class PeriodHoursServicePeriodsInRangeTests
{
    private const int DaysPerWeek = 7;

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Monthly_RangeInsideOnePeriod_ReturnsThatWholeMonth()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);

        var periods = await CreateService().GetPeriodsInRangeAsync(new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 20));

        periods.ShouldBe([(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31))]);
    }

    [Test]
    public async Task Monthly_FromEqualsUntil_ReturnsThePeriodOfThatDay()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);
        var day = new DateOnly(2026, 2, 28);

        var periods = await CreateService().GetPeriodsInRangeAsync(day, day);

        periods.ShouldBe([(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28))]);
    }

    [Test]
    public async Task Monthly_RangeExactlyOnePeriod_ReturnsOnlyThatPeriod()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);

        var periods = await CreateService().GetPeriodsInRangeAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

        periods.ShouldBe([(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31))]);
    }

    [Test]
    public async Task Monthly_RangeOverAMonthBoundary_ReturnsBothMonths()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);

        var periods = await CreateService().GetPeriodsInRangeAsync(new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 1));

        periods.ShouldBe([
            (new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)),
            (new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31))
        ]);
    }

    [Test]
    public async Task Monthly_RangeOverThreePeriods_ReturnsTheThreeMonthsInOrder()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);

        var periods = await CreateService().GetPeriodsInRangeAsync(new DateOnly(2026, 7, 15), new DateOnly(2026, 9, 2));

        periods.ShouldBe([
            (new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)),
            (new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)),
            (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))
        ]);
    }

    [Test]
    public async Task Weekly_RangeOverThreeWeeks_ReturnsTheThreeMondayToSundayWeeks()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Weekly);

        var periods = await CreateService().GetPeriodsInRangeAsync(new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 22));

        periods.ShouldBe([
            (new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 12)),
            (new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 19)),
            (new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 26))
        ]);
    }

    [Test]
    public async Task FromAfterUntil_ReturnsNoPeriod()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);

        var periods = await CreateService().GetPeriodsInRangeAsync(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 8));

        periods.ShouldBeEmpty();
    }

    [Test]
    public async Task Monthly_RangeOfExactlyTheLimit_IsAccepted()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);
        var from = new DateOnly(2026, 1, 1);

        var periods = await CreateService().GetPeriodsInRangeAsync(
            from, from.AddMonths(PayPeriodLimits.MaxPeriodsInRange).AddDays(-1));

        periods.Count.ShouldBe(PayPeriodLimits.MaxPeriodsInRange);
    }

    [Test]
    public async Task Monthly_RangeBeyondTheLimit_IsRefused()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Monthly);
        var from = new DateOnly(2026, 1, 1);

        await Should.ThrowAsync<InvalidRequestException>(() => CreateService().GetPeriodsInRangeAsync(
            from, from.AddMonths(PayPeriodLimits.MaxPeriodsInRange)));
    }

    private async Task SeedPaymentIntervalAsync(PaymentInterval interval)
    {
        _context.Settings.Add(new SettingsModel
        {
            Type = SettingKeys.PaymentInterval,
            Value = ((int)interval).ToString()
        });
        await _context.SaveChangesAsync();
    }

    private PeriodHoursService CreateService()
    {
        var weekConfiguration = Substitute.For<IWeekConfiguration>();
        weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var date = callInfo.Arg<DateOnly>();
            var offset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + DaysPerWeek) % DaysPerWeek;
            return Task.FromResult(date.AddDays(-offset));
        });

        return new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientContractDataProvider>(),
            weekConfiguration);
    }
}
