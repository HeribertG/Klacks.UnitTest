// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Every cached period-hours row carries the payment interval it was cut by. The period-closing dropdown groups by it and
/// the payroll export writes it; a row that always said Monthly made the period-closing page widen weekly and biweekly
/// periods to the whole calendar month when sealing.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
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
public class PeriodHoursPaymentIntervalStampTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly WeekStart = new(2026, 11, 2);
    private static readonly DateOnly WeekEnd = new(2026, 11, 8);

    private DataBaseContext _context = null!;
    private PeriodHoursService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _context.Settings.Add(new SettingsModel
        {
            Type = SettingKeys.PaymentInterval,
            Value = ((int)PaymentInterval.Weekly).ToString()
        });
        _context.Client.Add(new Client
        {
            Id = ClientId,
            Name = "Bauer",
            FirstName = "Marius",
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = ClientId,
                ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        });
        await _context.SaveChangesAsync();

        var groupFilter = Substitute.For<IClientGroupFilterService>();
        groupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(call => call.Arg<IQueryable<Client>>());
        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        contractData.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());

        _service = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            groupFilter,
            contractData,
            Substitute.For<IWeekConfiguration>());
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task RecalculatingOneClient_StampsTheConfiguredPaymentInterval()
    {
        await _service.RecalculatePeriodHoursAsync(ClientId, WeekStart, WeekEnd);

        (await StoredRowAsync()).PaymentInterval.ShouldBe(PaymentInterval.Weekly);
    }

    [Test]
    public async Task RecalculatingAllClients_StampsTheConfiguredPaymentInterval()
    {
        await _service.RecalculateAllClientsAsync(WeekStart, WeekEnd);

        (await StoredRowAsync()).PaymentInterval.ShouldBe(PaymentInterval.Weekly);
    }

    [Test]
    public async Task RecalculatingAnExistingRow_CorrectsAStaleMonthlyStamp()
    {
        _context.ClientPeriodHours.Add(new ClientPeriodHours
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            StartDate = WeekStart,
            EndDate = WeekEnd,
            PaymentInterval = PaymentInterval.Monthly,
            CalculatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await _service.RecalculatePeriodHoursAsync(ClientId, WeekStart, WeekEnd);

        (await StoredRowAsync()).PaymentInterval.ShouldBe(PaymentInterval.Weekly);
    }

    [Test]
    public async Task RefreshingCachedRowsInPlace_CorrectsAStaleMonthlyStamp()
    {
        _context.ClientPeriodHours.Add(new ClientPeriodHours
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            StartDate = WeekStart,
            EndDate = WeekEnd,
            PaymentInterval = PaymentInterval.Monthly,
            CalculatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await _service.RefreshCachedPeriodHoursAsync(WeekStart, WeekEnd);

        (await StoredRowAsync()).PaymentInterval.ShouldBe(PaymentInterval.Weekly);
    }

    private async Task<ClientPeriodHours> StoredRowAsync()
        => await _context.ClientPeriodHours.AsNoTracking()
            .SingleAsync(p => p.ClientId == ClientId && p.StartDate == WeekStart && p.EndDate == WeekEnd);
}
