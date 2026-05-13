// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WorkChangeEffectiveTimeService — verifies that effective Von/Bis
/// times mirror the offset logic of get_schedule_entries stored procedure.
/// </summary>
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WorkChangeEffectiveTimeServiceTests
{
    private DataBaseContext _context = null!;
    private WorkChangeEffectiveTimeService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        _sut = new WorkChangeEffectiveTimeService(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetEffectiveTimesAsync_SingleCorrectionEnd_StartEqualsWorkEnd()
    {
        var work = MakeWork(start: new TimeOnly(7, 0), end: new TimeOnly(15, 0));
        _context.Work.Add(work);
        var wc = MakeWorkChange(work.Id, WorkChangeType.CorrectionEnd, 0.333333333333m);
        _context.WorkChange.Add(wc);
        await _context.SaveChangesAsync();

        var (start, end) = await _sut.GetEffectiveTimesAsync(wc, work, null);

        start.ShouldBe(new TimeOnly(15, 0));
        end.ShouldBe(new TimeOnly(15, 20));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_TwoCorrectionEnds_SecondIsOffsetByFirst()
    {
        var work = MakeWork(start: new TimeOnly(7, 0), end: new TimeOnly(15, 0));
        _context.Work.Add(work);
        var wc1 = MakeWorkChange(work.Id, WorkChangeType.CorrectionEnd, 0.25m);
        var wc2 = MakeWorkChange(work.Id, WorkChangeType.CorrectionEnd, 0.333333333333m);
        wc1.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        wc2.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        _context.WorkChange.AddRange(wc1, wc2);
        await _context.SaveChangesAsync();

        var (start2, end2) = await _sut.GetEffectiveTimesAsync(wc2, work, null);

        start2.ShouldBe(new TimeOnly(15, 15));
        end2.ShouldBe(new TimeOnly(15, 35));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_CorrectionEndBeforeDebriefing_CorrectionEndHasHigherPriority()
    {
        var work = MakeWork(start: new TimeOnly(7, 0), end: new TimeOnly(15, 0));
        _context.Work.Add(work);
        var debriefing = MakeWorkChange(work.Id, WorkChangeType.Debriefing, 0.25m);
        var correction = MakeWorkChange(work.Id, WorkChangeType.CorrectionEnd, 0.25m);
        debriefing.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        correction.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        _context.WorkChange.AddRange(debriefing, correction);
        await _context.SaveChangesAsync();

        var (corrStart, corrEnd) = await _sut.GetEffectiveTimesAsync(correction, work, null);
        corrStart.ShouldBe(new TimeOnly(15, 0));
        corrEnd.ShouldBe(new TimeOnly(15, 15));

        var (debStart, debEnd) = await _sut.GetEffectiveTimesAsync(debriefing, work, null);
        debStart.ShouldBe(new TimeOnly(15, 15));
        debEnd.ShouldBe(new TimeOnly(15, 30));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_SingleCorrectionStart_EndEqualsWorkStart()
    {
        var work = MakeWork(start: new TimeOnly(7, 0), end: new TimeOnly(15, 0));
        _context.Work.Add(work);
        var wc = MakeWorkChange(work.Id, WorkChangeType.CorrectionStart, 0.25m);
        _context.WorkChange.Add(wc);
        await _context.SaveChangesAsync();

        var (start, end) = await _sut.GetEffectiveTimesAsync(wc, work, null);

        start.ShouldBe(new TimeOnly(6, 45));
        end.ShouldBe(new TimeOnly(7, 0));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_ReplacementEnd_EndEqualsWorkEnd()
    {
        var work = MakeWork(start: new TimeOnly(23, 0), end: new TimeOnly(7, 0));
        _context.Work.Add(work);
        var wc = MakeWorkChange(work.Id, WorkChangeType.ReplacementEnd, 0.25m);
        _context.WorkChange.Add(wc);
        await _context.SaveChangesAsync();

        var (start, end) = await _sut.GetEffectiveTimesAsync(wc, work, null);

        start.ShouldBe(new TimeOnly(6, 45));
        end.ShouldBe(new TimeOnly(7, 0));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_ReplacementStart_StartEqualsWorkStart()
    {
        var work = MakeWork(start: new TimeOnly(7, 0), end: new TimeOnly(15, 0));
        _context.Work.Add(work);
        var wc = MakeWorkChange(work.Id, WorkChangeType.ReplacementStart, 0.25m);
        _context.WorkChange.Add(wc);
        await _context.SaveChangesAsync();

        var (start, end) = await _sut.GetEffectiveTimesAsync(wc, work, null);

        start.ShouldBe(new TimeOnly(7, 0));
        end.ShouldBe(new TimeOnly(7, 15));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_TravelWithin_UsesStoredTimes()
    {
        var work = MakeWork(start: new TimeOnly(7, 0), end: new TimeOnly(15, 0));
        _context.Work.Add(work);
        var wc = new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = work.Id,
            Type = WorkChangeType.TravelWithin, ChangeTime = 0.5m,
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(9, 30),
        };
        _context.WorkChange.Add(wc);
        await _context.SaveChangesAsync();

        var (start, end) = await _sut.GetEffectiveTimesAsync(wc, work, null);

        start.ShouldBe(new TimeOnly(9, 0));
        end.ShouldBe(new TimeOnly(9, 30));
    }

    [Test]
    public async Task GetEffectiveTimesAsync_NightShift_CorrectionEnd_WrapsCorrectly()
    {
        var work = MakeWork(start: new TimeOnly(23, 0), end: new TimeOnly(7, 0));
        _context.Work.Add(work);
        var wc = MakeWorkChange(work.Id, WorkChangeType.CorrectionEnd, 0.25m);
        _context.WorkChange.Add(wc);
        await _context.SaveChangesAsync();

        var (start, end) = await _sut.GetEffectiveTimesAsync(wc, work, null);

        start.ShouldBe(new TimeOnly(7, 0));
        end.ShouldBe(new TimeOnly(7, 15));
    }

    private static Work MakeWork(TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), ClientId = Guid.NewGuid(),
        StartTime = start, EndTime = end,
        WorkTime = 8m, Surcharges = 0m,
        CurrentDate = DateOnly.FromDateTime(DateTime.Today),
    };

    private static WorkChange MakeWorkChange(Guid workId, WorkChangeType type, decimal changeTime) => new()
    {
        Id = Guid.NewGuid(), WorkId = workId, Type = type,
        ChangeTime = changeTime, Surcharges = 0m,
        StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MinValue,
    };
}
