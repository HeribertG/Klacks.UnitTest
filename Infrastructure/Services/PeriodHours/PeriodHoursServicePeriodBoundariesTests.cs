// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PeriodHoursService period boundary resolution: how the Weekly/Biweekly payment interval
/// boundaries react to a configured (non-default) week start day, and how the contract scoped
/// resolution handles PaymentInterval.Individual.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
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

    [Test]
    public async Task GetPeriodBoundariesAsync_GlobalIntervalIndividual_FallsBackToCalendarMonth()
    {
        // Arrange
        // The global setting cannot name an IndividualPeriod, so month boundaries stay the documented
        // fallback for the 20 existing call sites; only the log warning is new.
        await SeedPaymentIntervalAsync(PaymentInterval.Individual);
        var service = CreateService(weekStartDay: null);

        // Act
        var (start, end) = await service.GetPeriodBoundariesAsync(new DateOnly(2026, 7, 8));

        // Assert
        start.ShouldBe(new DateOnly(2026, 7, 1));
        end.ShouldBe(new DateOnly(2026, 7, 31));
    }

    [Test]
    public async Task GetPeriodBoundariesForContractAsync_IndividualWithMatchingPeriod_ReturnsCustomBoundaries()
    {
        // Arrange
        var contractId = await SeedIndividualContractAsync(
            (new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 28)),
            (new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 25)));
        var service = CreateService(weekStartDay: null);

        // Act
        var (start, end) = await service.GetPeriodBoundariesForContractAsync(contractId, new DateOnly(2026, 2, 10));

        // Assert
        start.ShouldBe(new DateOnly(2026, 1, 29));
        end.ShouldBe(new DateOnly(2026, 2, 25));
    }

    [Test]
    public async Task GetPeriodBoundariesForContractAsync_IndividualWithOpenPeriodAndSuccessor_EndsBeforeSuccessor()
    {
        // Arrange
        var contractId = await SeedIndividualContractAsync(
            (new DateOnly(2026, 1, 1), null),
            (new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)));
        var service = CreateService(weekStartDay: null);

        // Act
        var (start, end) = await service.GetPeriodBoundariesForContractAsync(contractId, new DateOnly(2026, 2, 15));

        // Assert
        start.ShouldBe(new DateOnly(2026, 1, 1));
        end.ShouldBe(new DateOnly(2026, 3, 31));
    }

    [Test]
    public async Task GetPeriodBoundariesForContractAsync_IndividualWithoutLinkedIndividualPeriod_Throws()
    {
        // Arrange
        var contractId = Guid.NewGuid();
        _context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = "Individual without period",
            PaymentInterval = PaymentInterval.Individual,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.SaveChangesAsync();
        var service = CreateService(weekStartDay: null);

        // Act
        var act = async () => await service.GetPeriodBoundariesForContractAsync(contractId, new DateOnly(2026, 2, 10));

        // Assert
        var ex = await Should.ThrowAsync<InvalidRequestException>(act);
        ex.Message.ShouldContain("no individual period assigned");
    }

    [Test]
    public async Task GetPeriodBoundariesForContractAsync_IndividualWithDateOutsideAllPeriods_Throws()
    {
        // Arrange
        var contractId = await SeedIndividualContractAsync(
            (new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)),
            (new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        var service = CreateService(weekStartDay: null);

        // Act
        var act = async () => await service.GetPeriodBoundariesForContractAsync(contractId, new DateOnly(2026, 2, 15));

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    [Test]
    public async Task GetPeriodBoundariesForContractAsync_NonIndividualContract_UsesContractIntervalNotGlobalSetting()
    {
        // Arrange
        // Global setting says Weekly, the contract says Monthly: the contract must win.
        await SeedPaymentIntervalAsync(PaymentInterval.Weekly);
        var contractId = Guid.NewGuid();
        _context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = "Monthly contract",
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = new DateTime(2026, 1, 1)
        });
        await _context.SaveChangesAsync();
        var service = CreateService(weekStartDay: null);

        // Act
        var (start, end) = await service.GetPeriodBoundariesForContractAsync(contractId, new DateOnly(2026, 7, 8));

        // Assert
        start.ShouldBe(new DateOnly(2026, 7, 1));
        end.ShouldBe(new DateOnly(2026, 7, 31));
    }

    [Test]
    public async Task GetPeriodBoundariesForContractAsync_UnknownContract_Throws()
    {
        // Arrange
        var service = CreateService(weekStartDay: null);

        // Act
        var act = async () => await service.GetPeriodBoundariesForContractAsync(Guid.NewGuid(), new DateOnly(2026, 2, 10));

        // Assert
        await Should.ThrowAsync<InvalidRequestException>(act);
    }

    private async Task<Guid> SeedIndividualContractAsync(params (DateOnly FromDate, DateOnly? UntilDate)[] periods)
    {
        var individualPeriodId = Guid.NewGuid();
        _context.IndividualPeriod.Add(new IndividualPeriod
        {
            Id = individualPeriodId,
            Name = "Payroll 2026"
        });

        foreach (var (fromDate, untilDate) in periods)
        {
            _context.Period.Add(new Period
            {
                Id = Guid.NewGuid(),
                IndividualPeriodId = individualPeriodId,
                FromDate = fromDate,
                UntilDate = untilDate
            });
        }

        var contractId = Guid.NewGuid();
        _context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = "Individual contract",
            PaymentInterval = PaymentInterval.Individual,
            IndividualPeriodId = individualPeriodId,
            ValidFrom = new DateTime(2026, 1, 1)
        });

        await _context.SaveChangesAsync();
        return contractId;
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
        return new PeriodHoursService(
            _context,
            logger,
            notificationService,
            clientGroupFilterService,
            contractDataProvider,
            weekConfiguration);
    }
}
