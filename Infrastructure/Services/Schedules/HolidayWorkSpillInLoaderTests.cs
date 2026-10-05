// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for HolidayWorkSpillInLoader: only work of the day before a checked range that actually runs into the range
/// is reported, per client including a client who takes over the after-midnight part by replacement; other days,
/// day shifts, deleted works and other scenarios are ignored.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class HolidayWorkSpillInLoaderTests
{
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateOnly August1 = new(2026, 8, 1);

    private DataBaseContext _context = null!;
    private TimelineCalculationService _timeline = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _timeline = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()), NullLogger<TimelineCalculationService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task NightShiftOfTheDayBefore_ReportsTheFirstRangeDayWithTheClientName()
    {
        var clientId = SeedClient("Muster", "Anna");
        SeedWork(clientId, July31, new TimeOnly(22, 0), new TimeOnly(6, 0));
        await _context.SaveChangesAsync();

        var spillIn = await LoadAsync();

        spillIn.DatesByClient[clientId].ShouldBe([August1]);
        spillIn.ClientNames[clientId].ShouldBe("Muster Anna");
    }

    [Test]
    public async Task DayShiftOfTheDayBefore_IsNotReported()
    {
        var clientId = SeedClient("Muster", "Anna");
        SeedWork(clientId, July31, new TimeOnly(8, 0), new TimeOnly(16, 0));
        await _context.SaveChangesAsync();

        (await LoadAsync()).DatesByClient.ShouldBeEmpty();
    }

    [Test]
    public async Task NightShiftsOfOtherDaysDeletedWorksAndOtherScenarios_AreIgnored()
    {
        var clientId = SeedClient("Muster", "Anna");
        SeedWork(clientId, July31.AddDays(-1), new TimeOnly(22, 0), new TimeOnly(6, 0));
        SeedWork(clientId, July31, new TimeOnly(22, 0), new TimeOnly(6, 0), isDeleted: true);
        SeedWork(clientId, July31, new TimeOnly(22, 0), new TimeOnly(6, 0), analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        (await LoadAsync()).DatesByClient.ShouldBeEmpty();
    }

    [Test]
    public async Task ReplacementOfTheAfterMidnightPart_ReportsTheReplacingClient()
    {
        var ownerId = SeedClient("Muster", "Anna");
        var replacingId = SeedClient("Beispiel", "Ben");
        var work = SeedWork(ownerId, July31, new TimeOnly(22, 0), new TimeOnly(6, 0));
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementEnd,
            ChangeTime = 2m,
            ReplaceClientId = replacingId
        });
        await _context.SaveChangesAsync();

        var spillIn = await LoadAsync();

        spillIn.DatesByClient[replacingId].ShouldBe([August1]);
        spillIn.DatesByClient[ownerId].ShouldBe([August1]);
        spillIn.ClientNames[replacingId].ShouldBe("Beispiel Ben");
    }

    private Task<HolidayWorkSpillIn> LoadAsync() =>
        HolidayWorkSpillInLoader.LoadAsync(_context, _timeline, August1, null, CancellationToken.None);

    private Guid SeedClient(string name, string firstName)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client { Id = clientId, Name = name, FirstName = firstName });
        return clientId;
    }

    private Work SeedWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end, bool isDeleted = false, Guid? analyseToken = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
            IsDeleted = isDeleted,
            AnalyseToken = analyseToken
        };
        _context.Work.Add(work);
        return work;
    }
}
