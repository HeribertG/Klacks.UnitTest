// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for ClientTimelineLoader against an in-memory database with the real TimelineCalculationService.
/// Covers the load filters (window, client, soft delete, sub works, scenario token, replacements) and the
/// cross-day rest-violation scenario the live single check depends on: a late shift on Monday followed by
/// an early shift on Tuesday is only visible when the loaded window spans both days.
/// </summary>
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ClientTimelineLoaderTests
{
    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);

    private DataBaseContext _context = null!;
    private TimelineCalculationService _calculation = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _calculation = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);
        _clientId = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task LoadAsync_OnlyLoadsTheClientsTopLevelRealWorksInsideTheWindow()
    {
        var inside = AddWork(_clientId, Tuesday, 7, 15);
        AddWork(_clientId, Monday, 7, 15);
        AddWork(Guid.NewGuid(), Tuesday, 7, 15);
        AddWork(_clientId, Tuesday, 16, 18, work => work.IsDeleted = true);
        AddWork(_clientId, Tuesday, 16, 18, work => work.ParentWorkId = inside.Id);
        AddWork(_clientId, Tuesday, 16, 18, work => work.AnalyseToken = Guid.NewGuid());
        await _context.SaveChangesAsync();

        var timeline = await Load(Tuesday, Tuesday);

        timeline.ClientId.ShouldBe(_clientId);
        timeline.Blocks.Count.ShouldBe(1);
        timeline.Blocks[0].SourceId.ShouldBe(inside.Id);
    }

    [Test]
    public async Task LoadAsync_IncludesTheReplacedPartOfAnotherClientsWork()
    {
        var otherClientWork = AddWork(Guid.NewGuid(), Tuesday, 7, 15);
        var replacement = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = otherClientWork.Id,
            Type = WorkChangeType.ReplacementEnd,
            ChangeTime = 3m,
            ReplaceClientId = _clientId,
        };
        _context.WorkChange.Add(replacement);
        await _context.SaveChangesAsync();

        var timeline = await Load(Tuesday, Tuesday);

        timeline.Blocks.Count.ShouldBe(1);
        timeline.Blocks[0].BlockType.ShouldBe(ScheduleBlockType.Replacement);
        timeline.Blocks[0].ClientId.ShouldBe(_clientId);
    }

    [Test]
    public async Task LoadAsync_ReturnsBlocksSortedByStart()
    {
        AddWork(_clientId, Wednesday, 7, 15);
        AddWork(_clientId, Monday, 7, 15);
        AddWork(_clientId, Tuesday, 7, 15);
        await _context.SaveChangesAsync();

        var timeline = await Load(Monday, Wednesday);

        timeline.Blocks.Select(b => b.OwnerDate).ShouldBe([Monday, Tuesday, Wednesday]);
    }

    [Test]
    public async Task RestCheckForTuesday_WindowAroundTheDay_FindsTheLateMondayEarlyTuesdayPair()
    {
        AddWork(_clientId, Monday, 15, 22);
        AddWork(_clientId, Tuesday, 7, 15);
        await _context.SaveChangesAsync();
        var entries = new List<ScheduleValidationNotificationDto>();

        var timeline = await Load(Tuesday.AddDays(-2), Tuesday.AddDays(2));
        ScheduleValidationBuilder.AddRestViolations(entries, timeline, "Test", Policy(), Tuesday.AddDays(-1), Tuesday);

        entries.Count.ShouldBe(1);
        entries[0].Date.ShouldBe(Monday);
        entries[0].ClientId.ShouldBe(_clientId);
    }

    [Test]
    public async Task RestCheckForTuesday_OneDayWindow_CannotSeeThePair()
    {
        AddWork(_clientId, Monday, 15, 22);
        AddWork(_clientId, Tuesday, 7, 15);
        await _context.SaveChangesAsync();
        var entries = new List<ScheduleValidationNotificationDto>();

        var timeline = await Load(Tuesday, Tuesday);
        ScheduleValidationBuilder.AddRestViolations(entries, timeline, "Test", Policy());

        entries.ShouldBeEmpty();
    }

    private Task<ClientTimeline> Load(DateOnly from, DateOnly to)
        => ClientTimelineLoader.LoadAsync(_context, _calculation, _clientId, from, to, null, CancellationToken.None);

    private Work AddWork(Guid clientId, DateOnly date, int startHour, int endHour, Action<Work>? configure = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = new TimeOnly(startHour, 0),
            EndTime = new TimeOnly(endHour, 0),
            WorkTime = endHour - startHour,
        };
        configure?.Invoke(work);
        _context.Work.Add(work);
        return work;
    }

    private static SchedulingPolicy Policy()
        => new(
            MinRestHours: TimeSpan.FromHours(11),
            MaxDailyHours: TimeSpan.FromHours(10),
            MaxConsecutiveDays: 6,
            MaxWeeklyHours: TimeSpan.FromHours(50),
            MinRestDays: 2);
}
