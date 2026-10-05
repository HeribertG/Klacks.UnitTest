// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The works the holiday diagnosis shows for a day come from the production timeline (ClientTimelineLoader +
/// WorkedCalendarDates), the same source the holiday-work warning reads: a night shift of the day before that runs
/// past midnight is listed and marked as starting the day before, a work ending at midnight is not, container
/// children (ParentWorkId) and scenario rows are ignored, and a work the person covers as replacement counts.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

[TestFixture]
public class ClientWorkCoverageReaderTests
{
    private static readonly DateOnly Holiday = new(2026, 8, 1);

    private DataBaseContext _context = null!;
    private ClientWorkCoverageReader _sut = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _clientId = Guid.NewGuid();
        var timeline = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);
        _sut = new ClientWorkCoverageReader(_context, timeline);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task NightShiftOfTheDayBefore_IsListedAndMarked()
    {
        await AddWorkAsync(Holiday.AddDays(-1), new TimeOnly(22, 0), new TimeOnly(6, 0));

        var works = await _sut.GetWorksTouchingAsync(_clientId, Holiday);

        works.Count.ShouldBe(1);
        works[0].StartsTheDayBefore.ShouldBeTrue();
        works[0].WorkDate.ShouldBe(Holiday.AddDays(-1));
    }

    [Test]
    public async Task WorkEndingAtMidnight_DoesNotTouchTheNextDay()
    {
        await AddWorkAsync(Holiday.AddDays(-1), new TimeOnly(16, 0), new TimeOnly(0, 0));

        (await _sut.GetWorksTouchingAsync(_clientId, Holiday)).ShouldBeEmpty();
    }

    [Test]
    public async Task DayWorkOnTheHoliday_IsListedNotMarked()
    {
        await AddWorkAsync(Holiday, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var works = await _sut.GetWorksTouchingAsync(_clientId, Holiday);

        works.Count.ShouldBe(1);
        works[0].StartsTheDayBefore.ShouldBeFalse();
    }

    [Test]
    public async Task ContainerChildrenAndScenarioRows_AreIgnored()
    {
        await AddWorkAsync(Holiday, new TimeOnly(8, 0), new TimeOnly(16, 0), parentWorkId: Guid.NewGuid());
        await AddWorkAsync(Holiday, new TimeOnly(8, 0), new TimeOnly(16, 0), analyseToken: Guid.NewGuid());

        (await _sut.GetWorksTouchingAsync(_clientId, Holiday)).ShouldBeEmpty();
    }

    [Test]
    public async Task WorkCoveredAsReplacement_CountsForTheReplacingPerson()
    {
        var originalOwner = Guid.NewGuid();
        var work = await AddWorkAsync(Holiday, new TimeOnly(8, 0), new TimeOnly(16, 0), clientId: originalOwner);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            ReplaceClientId = _clientId,
            Type = WorkChangeType.ReplacementWithin,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        });
        await _context.SaveChangesAsync();

        (await _sut.GetWorksTouchingAsync(_clientId, Holiday)).ShouldNotBeEmpty();
    }

    private async Task<Work> AddWorkAsync(
        DateOnly date, TimeOnly start, TimeOnly end, Guid? parentWorkId = null, Guid? analyseToken = null, Guid? clientId = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? _clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
            ParentWorkId = parentWorkId,
            AnalyseToken = analyseToken,
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work;
    }
}
