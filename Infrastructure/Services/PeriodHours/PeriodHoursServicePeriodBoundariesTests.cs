// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PeriodHoursService.GetPeriodBoundariesAsync, focused on how the Weekly/Biweekly payment
/// interval boundaries react to a configured (non-default) week start day.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class PeriodHoursServicePeriodBoundariesTests
{
    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetPeriodBoundariesAsync_WeeklyWithDefaultMondayStart_ReturnsMondayToSunday()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Weekly);
        var service = CreateService(weekStartDay: null);

        var (start, end) = await service.GetPeriodBoundariesAsync(new DateOnly(2026, 7, 8)); // Wednesday

        start.ShouldBe(new DateOnly(2026, 7, 6)); // Monday
        end.ShouldBe(new DateOnly(2026, 7, 12)); // Sunday
    }

    [Test]
    public async Task GetPeriodBoundariesAsync_WeeklyWithSundayStart_ReturnsSundayToSaturday()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Weekly);
        var service = CreateService(weekStartDay: DayOfWeek.Sunday);

        var (start, end) = await service.GetPeriodBoundariesAsync(new DateOnly(2026, 7, 8)); // Wednesday

        start.ShouldBe(new DateOnly(2026, 7, 5)); // Sunday
        end.ShouldBe(new DateOnly(2026, 7, 11)); // Saturday
    }

    [Test]
    public async Task GetPeriodBoundariesAsync_BiweeklyWithDefaultMondayStart_ReturnsFourteenDaySpan()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Biweekly);
        var service = CreateService(weekStartDay: null);

        var (start, end) = await service.GetPeriodBoundariesAsync(new DateOnly(2026, 7, 8));

        (end.DayNumber - start.DayNumber).ShouldBe(13);
        start.DayOfWeek.ShouldBe(DayOfWeek.Monday);
    }

    [Test]
    public async Task GetPeriodBoundariesAsync_BiweeklyWithSundayStart_StartDayMatchesConfiguredWeekStart()
    {
        // Documents a known limitation: the biweekly odd/even alternation still derives from the
        // Monday-anchored ISO week number, so a Sunday week start is honored for the boundary's
        // weekday but the 14-day window may not align with a Sunday-first payroll calendar.
        await SeedPaymentIntervalAsync(PaymentInterval.Biweekly);
        var service = CreateService(weekStartDay: DayOfWeek.Sunday);

        var (start, end) = await service.GetPeriodBoundariesAsync(new DateOnly(2026, 7, 8));

        (end.DayNumber - start.DayNumber).ShouldBe(13);
        start.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
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

    private PeriodHoursService CreateService(DayOfWeek? weekStartDay)
    {
        var logger = Substitute.For<ILogger<PeriodHoursService>>();
        var notificationService = Substitute.For<IWorkNotificationService>();
        var clientGroupFilterService = Substitute.For<IClientGroupFilterService>();
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        var weekConfiguration = Substitute.For<IWeekConfiguration>();
        weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>()).Returns(callInfo =>
        {
            var date = callInfo.Arg<DateOnly>();
            var effectiveStartDay = weekStartDay ?? DayOfWeek.Monday;
            var offset = ((int)date.DayOfWeek - (int)effectiveStartDay + 7) % 7;
            return Task.FromResult(date.AddDays(-offset));
        });
        var onCallConfigResolver = Substitute.For<IOnCallConfigResolver>();
        onCallConfigResolver.ResolveAsync().Returns(new OnCallConfig(false, 1m, 0m, false));

        return new PeriodHoursService(
            _context,
            logger,
            notificationService,
            clientGroupFilterService,
            contractDataProvider,
            weekConfiguration,
            onCallConfigResolver);
    }
}
