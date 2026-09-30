// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Regression for the schedule row header showing 00:00 after a scenario accept: the accept promotes works
/// in bulk without the per-work period-hours hooks, and the read path only falls back to the live sum on a
/// cache MISS - a stale cache row therefore keeps winning. RefreshCachedPeriodHoursAsync recomputes the
/// overlapping rows of the given plan in place and must leave other plans and other periods untouched.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class PeriodHoursRefreshCachedTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly NovemberStart = new(2026, 11, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);
    private static readonly DateOnly OctoberStart = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);
    private static readonly DateOnly AcceptedFrom = new(2026, 11, 2);
    private static readonly DateOnly AcceptedUntil = new(2026, 11, 8);
    private static readonly Guid ScenarioToken = Guid.NewGuid();

    private const decimal PromotedWorkTime = 8m;
    private const decimal PromotedSurcharges = 0.7m;
    private const decimal StaleOctoberHours = 42m;

    private DataBaseContext _context = null!;
    private PeriodHoursService _periodHoursService = null!;
    private WorkRepository _workRepository = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _context.Client.Add(new Client { Id = ClientId, Name = "Bauer", FirstName = "Marius" });
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = new DateOnly(2026, 11, 3),
            WorkTime = PromotedWorkTime,
            Surcharges = PromotedSurcharges,
            AnalyseToken = null,
            StartTime = new TimeOnly(5, 0),
            EndTime = new TimeOnly(13, 0),
        });
        _context.ClientPeriodHours.AddRange(
            CacheRow(NovemberStart, NovemberEnd, hours: 0m, analyseToken: null),
            CacheRow(OctoberStart, OctoberEnd, hours: StaleOctoberHours, analyseToken: null),
            CacheRow(NovemberStart, NovemberEnd, hours: 0m, analyseToken: ScenarioToken));
        await _context.SaveChangesAsync();

        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        _periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractDataProvider,
            Substitute.For<IWeekConfiguration>());

        _workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractDataProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task StaleCacheRow_HidesPromotedWorks_UntilRefreshed()
    {
        var clientIds = new List<Guid> { ClientId };

        var before = await _workRepository.GetPeriodHoursForClients(clientIds, NovemberStart, NovemberEnd);
        before[ClientId].Hours.ShouldBe(0m);

        await _periodHoursService.RefreshCachedPeriodHoursAsync(AcceptedFrom, AcceptedUntil, analyseToken: null);

        var after = await _workRepository.GetPeriodHoursForClients(clientIds, NovemberStart, NovemberEnd);
        after[ClientId].Hours.ShouldBe(PromotedWorkTime);
        after[ClientId].Surcharges.ShouldBe(PromotedSurcharges);
    }

    [Test]
    public async Task Refresh_LeavesNonOverlappingPeriodsAndOtherPlansUntouched()
    {
        await _periodHoursService.RefreshCachedPeriodHoursAsync(AcceptedFrom, AcceptedUntil, analyseToken: null);

        var october = await _context.ClientPeriodHours.SingleAsync(p => p.StartDate == OctoberStart && p.AnalyseToken == null);
        october.Hours.ShouldBe(StaleOctoberHours);

        var scenarioRow = await _context.ClientPeriodHours.SingleAsync(p => p.AnalyseToken == ScenarioToken);
        scenarioRow.Hours.ShouldBe(0m);
    }

    [Test]
    public async Task SoftDeletedCacheRow_IsNeverServed_ReadFallsBackToLiveSum()
    {
        var clientIds = new List<Guid> { ClientId };
        var november = await _context.ClientPeriodHours.SingleAsync(p => p.StartDate == NovemberStart && p.AnalyseToken == null);
        _context.ClientPeriodHours.Remove(november);
        await _context.SaveChangesAsync();

        var result = await _workRepository.GetPeriodHoursForClients(clientIds, NovemberStart, NovemberEnd);

        result[ClientId].Hours.ShouldBe(PromotedWorkTime);
    }

    private static ClientPeriodHours CacheRow(DateOnly start, DateOnly end, decimal hours, Guid? analyseToken) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        StartDate = start,
        EndDate = end,
        Hours = hours,
        Surcharges = 0m,
        AnalyseToken = analyseToken,
        CalculatedAt = DateTime.UtcNow,
    };
}
