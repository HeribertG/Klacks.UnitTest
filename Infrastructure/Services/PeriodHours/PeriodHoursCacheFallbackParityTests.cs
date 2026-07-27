// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Cross-path invariant for period hours: the two entry points that attribute a client's period
/// hours - <c>PeriodHoursService.GetPeriodHoursAsync</c> (the cache-populating core path, exercised here
/// through its private <c>CalculatePeriodHoursForClientsAsync</c> helper since no <c>ClientPeriodHours</c>
/// row exists) and <c>WorkRepository.GetPeriodHoursForClients</c> (the cache-miss fallback used when the
/// cache row is absent) - MUST weight work changes identically, otherwise a client's displayed hours
/// would silently depend on whether its period happened to be cached. Both entry points share the
/// same public shape (client IDs + date range in, <c>Dictionary&lt;Guid, PeriodHoursResource&gt;</c> out),
/// so their <c>Hours</c> values are compared directly against each other and against one hand-computed
/// expectation, which guards against a shared bug making both paths equally wrong.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
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
public class PeriodHoursCacheFallbackParityTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 3, 31);
    private static readonly DateOnly WorkDay = new(2026, 3, 10);

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
    public async Task PeriodHours_CachePathAndFallbackPath_ProduceIdenticalHours()
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = WorkDay,
            WorkTime = 5m,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(13, 0),
        };
        _context.Work.Add(work);

        // Plain correction. Both paths add its raw ChangeTime unweighted.
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = WorkChangeType.CorrectionStart,
            ChangeTime = 2m,
            AnalyseToken = null,
            StartTime = new TimeOnly(13, 0),
            EndTime = new TimeOnly(15, 0),
        });
        await _context.SaveChangesAsync();

        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        var periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractDataProvider,
            Substitute.For<IWeekConfiguration>());

        var workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractDataProvider);

        var clientIds = new List<Guid> { ClientId };

        // No ClientPeriodHours row exists for either path, so GetPeriodHoursAsync falls through to its
        // private CalculatePeriodHoursForClientsAsync (the cache-populating "core" calculation).
        var core = await periodHoursService.GetPeriodHoursAsync(clientIds, Start, End);

        // Same client/period, same absence of a cache row: GetPeriodHoursForClients falls through to its
        // BuildFallbackPeriodHours/CalculateWorkChangeAdjustments cache-miss branch.
        var fallback = await workRepository.GetPeriodHoursForClients(clientIds, Start, End);

        // 5 (work) + 2 (correction) = 7.
        const decimal expectedHours = 7m;

        core[ClientId].Hours.ShouldBe(expectedHours);
        fallback[ClientId].Hours.ShouldBe(expectedHours);
        core[ClientId].Hours.ShouldBe(fallback[ClientId].Hours);
    }
}
