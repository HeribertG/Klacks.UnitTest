// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RecalculateAllClientsAsync writes the cached rows of one real pay period for every client in scope and, like the
/// single-client recalculation, removes the rows that overlap that period with other boundaries (left behind by an
/// earlier window over several periods), while rows of other, non-overlapping periods stay for the payroll export.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

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

[TestFixture]
public class PeriodHoursRecalculateAllClientsMiscutTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly OctoberStart = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);
    private static readonly DateOnly NovemberStart = new(2026, 11, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);
    private static readonly DateOnly MiscutEnd = new(2026, 12, 31);

    private const decimal NovemberWorkTime = 8m;
    private const decimal OctoberHours = 42m;
    private const decimal MiscutHours = 99m;

    private DataBaseContext _context = null!;
    private PeriodHoursService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

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
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = new DateOnly(2026, 11, 3),
            WorkTime = NovemberWorkTime,
            StartTime = new TimeOnly(5, 0),
            EndTime = new TimeOnly(13, 0)
        });
        _context.ClientPeriodHours.AddRange(
            CacheRow(OctoberStart, OctoberEnd, OctoberHours),
            CacheRow(NovemberStart, MiscutEnd, MiscutHours));
        await _context.SaveChangesAsync();

        var groupFilter = Substitute.For<IClientGroupFilterService>();
        groupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(call => call.Arg<IQueryable<Client>>());
        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

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
    public async Task RecalculatingOnePayPeriodForAllClients_RemovesTheOverlappingMiscutRow_AndKeepsTheEarlierPeriod()
    {
        await _service.RecalculateAllClientsAsync(NovemberStart, NovemberEnd);

        var rows = await _context.ClientPeriodHours.AsNoTracking()
            .Where(p => p.ClientId == ClientId)
            .OrderBy(p => p.StartDate)
            .ToListAsync();

        rows.Select(p => (p.StartDate, p.EndDate, p.Hours)).ShouldBe(
        [
            (OctoberStart, OctoberEnd, OctoberHours),
            (NovemberStart, NovemberEnd, NovemberWorkTime)
        ]);
    }

    private static ClientPeriodHours CacheRow(DateOnly start, DateOnly end, decimal hours) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        StartDate = start,
        EndDate = end,
        Hours = hours,
        CalculatedAt = DateTime.UtcNow
    };
}
