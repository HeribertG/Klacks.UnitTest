// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
using Klacks.Api.Infrastructure.Services.Clients;

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
    public async Task GetPeriodBoundariesAsync_BiweeklyWithSundayStart_FollowsTheFixedAnchorMovedToSunday()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Biweekly);
        var service = CreateService(weekStartDay: DayOfWeek.Sunday);

        var (start, end) = await service.GetPeriodBoundariesAsync(new DateOnly(2027, 1, 4));

        start.ShouldBe(new DateOnly(2026, 12, 27));
        end.ShouldBe(new DateOnly(2027, 1, 9));
    }

    [TestCase("2026-12-21", "2026-12-14", "2026-12-27")]
    [TestCase("2026-12-28", "2026-12-28", "2027-01-10")]
    [TestCase("2027-01-04", "2026-12-28", "2027-01-10")]
    [TestCase("2027-01-11", "2027-01-11", "2027-01-24")]
    public async Task GetPeriodBoundariesAsync_BiweeklyAcrossTheIsoYear2026With53Weeks_DoesNotOverlap(
        string date, string expectedStart, string expectedEnd)
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Biweekly);
        var service = CreateService(weekStartDay: null);

        var (start, end) = await service.GetPeriodBoundariesAsync(IsoDate(date));

        start.ShouldBe(IsoDate(expectedStart));
        end.ShouldBe(IsoDate(expectedEnd));
    }

    [Test]
    public async Task GetPeriodsInRangeAsync_BiweeklyFromDecember2026ToFebruary2027_ReturnsContiguousPeriods()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Biweekly);
        var service = CreateService(weekStartDay: null);

        var periods = await service.GetPeriodsInRangeAsync(new DateOnly(2026, 12, 1), new DateOnly(2027, 2, 28));

        periods.ShouldBe(new[]
        {
            (new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 13)),
            (new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 27)),
            (new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 10)),
            (new DateOnly(2027, 1, 11), new DateOnly(2027, 1, 24)),
            (new DateOnly(2027, 1, 25), new DateOnly(2027, 2, 7)),
            (new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 21)),
            (new DateOnly(2027, 2, 22), new DateOnly(2027, 3, 7)),
        });
    }

    [Test]
    public async Task GetPeriodsInRangeAsync_BiweeklyWithSundayStart_ReturnsContiguousPeriods()
    {
        await SeedPaymentIntervalAsync(PaymentInterval.Biweekly);
        var service = CreateService(weekStartDay: DayOfWeek.Sunday);

        var periods = await service.GetPeriodsInRangeAsync(new DateOnly(2026, 12, 1), new DateOnly(2027, 2, 28));

        periods[0].StartDate.ShouldBe(new DateOnly(2026, 11, 29));
        for (var index = 1; index < periods.Count; index++)
        {
            periods[index].StartDate.ShouldBe(periods[index - 1].EndDate.AddDays(1));
            periods[index].StartDate.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        }

        periods[^1].EndDate.ShouldBeGreaterThanOrEqualTo(new DateOnly(2027, 2, 28));
    }

    private static DateOnly IsoDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None);

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
            weekConfiguration,
            new FrozenPeriodPolicy(_context));
    }
}
