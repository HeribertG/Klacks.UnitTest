// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reproduces the browser probe of 2026-11-08: an employee works eleven days in a row (10 to 20 December) and takes over
/// two night shifts (15 and 16 December) on top. The range check after a reload and the single check after the drag of a
/// night shift must report the same work-time findings: the full run of eleven days dated on its first day, the daily
/// work time of the day after a night and the weekly cap. Before the fix the single check counted only the ISO week
/// (14 December, 7 days) and the daily work time of the edited day only, and the range check counted a run from the
/// first day of its window. The single check sends again every run in the window the client retracts (the week's Monday
/// minus 14 days up to its Sunday) on its true start, and leaves out a run that ended before that window.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineRunAcrossWindowTests
{
    private static readonly DateOnly MonthStart = new(2026, 12, 1);
    private static readonly DateOnly MonthEnd = new(2026, 12, 31);
    private static readonly DateOnly RunStart = new(2026, 12, 10);
    private static readonly DateOnly RunEnd = new(2026, 12, 20);
    private static readonly DateOnly FirstNight = new(2026, 12, 15);
    private static readonly DateOnly SecondNight = new(2026, 12, 16);
    private static readonly DateOnly DayAfterNights = new(2026, 12, 17);
    private static readonly DateOnly WeekMonday = new(2026, 12, 14);
    private static readonly DateOnly RetractWindowStart = new(2026, 12, 7);

    private static readonly string[] WorkTimeKeys =
    [
        ScheduleValidationKeys.ConsecutiveDays,
        ScheduleValidationKeys.Overtime,
        ScheduleValidationKeys.WeeklyOvertime,
        ScheduleValidationKeys.MinRestDays,
    ];

    private ScheduleTimelineTestHost _host = null!;
    private Guid _employeeId;
    private Guid _colleagueShiftId;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _employeeId = await _host.AddEmployeeAsync("Bianchi", "Nadia");
        var shift = await _host.AddCustomerShiftAsync("Bahnhofstrasse 1", "8001", "Zürich");

        for (var day = RunStart; day <= RunEnd; day = day.AddDays(1))
        {
            await _host.AddWorkAsync(_employeeId, shift, day, new TimeOnly(7, 0), new TimeOnly(15, 0));
        }

        await _host.AddWorkAsync(_employeeId, shift, FirstNight, new TimeOnly(22, 0), new TimeOnly(6, 0));
        await _host.AddWorkAsync(_employeeId, shift, SecondNight, new TimeOnly(22, 0), new TimeOnly(6, 0));
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [Test]
    public async Task SingleCheckAfterTheNightDrag_ReportsTheSameWorkTimeFindingsAsTheRangeCheck()
    {
        _host.Service.QueueRangeCheck(MonthStart, MonthEnd, null);
        _host.Service.QueueCheck(_employeeId, SecondNight, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(2);

        var range = validations.Single(v => v.IsFullRefresh).Entries;
        var single = validations.Single(v => !v.IsFullRefresh).Entries;

        AssertProbeFindings(range);
        AssertProbeFindings(single);

        var rangeFindings = WorkTimeFindings(range);
        WorkTimeFindings(single).ShouldAllBe(finding => rangeFindings.Contains(finding));
    }

    [Test]
    public async Task SingleCheck_RunInsideTheRetractWindowBeforeTheWeek_IsSentAgainOnItsTrueStart()
    {
        var colleagueId = await AddColleagueRunAsync(MonthStart, MonthStart.AddDays(8));
        var editedDay = new DateOnly(2026, 12, 23);
        await AddColleagueWorkAsync(colleagueId, editedDay);

        _host.Service.QueueCheck(colleagueId, editedDay, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var run = validations.ShouldHaveSingleItem().Entries
            .Where(e => e.Comment == ScheduleValidationKeys.ConsecutiveDays)
            .ShouldHaveSingleItem();
        run.Date.ShouldBe(MonthStart);
        run.CommentParams!["actualDays"].ShouldBe("9");
    }

    [Test]
    public async Task SingleCheck_RunEndingOnTheFirstDayOfTheRetractWindow_IsSentAgain()
    {
        var colleagueId = await AddColleagueRunAsync(MonthStart, RetractWindowStart);
        var editedDay = new DateOnly(2026, 12, 23);
        await AddColleagueWorkAsync(colleagueId, editedDay);

        _host.Service.QueueCheck(colleagueId, editedDay, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var run = validations.ShouldHaveSingleItem().Entries
            .Where(e => e.Comment == ScheduleValidationKeys.ConsecutiveDays)
            .ShouldHaveSingleItem();
        run.Date.ShouldBe(MonthStart);
        run.CommentParams!["actualDays"].ShouldBe("7");
    }

    [Test]
    public async Task SingleCheck_RunEndingTheDayBeforeTheRetractWindow_IsNotReported()
    {
        var colleagueId = await AddColleagueRunAsync(RetractWindowStart.AddDays(-7), RetractWindowStart.AddDays(-1));
        var editedDay = new DateOnly(2026, 12, 23);
        await AddColleagueWorkAsync(colleagueId, editedDay);

        _host.Service.QueueCheck(colleagueId, editedDay, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        validations.ShouldHaveSingleItem().Entries.ShouldNotContain(e => e.Comment == ScheduleValidationKeys.ConsecutiveDays);
    }

    [Test]
    public async Task RangeCheckStartingInsideTheRun_CountsTheRunInFullAndDatesItOnTheWindowStart()
    {
        _host.Service.QueueRangeCheck(WeekMonday, MonthEnd, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var run = validations.ShouldHaveSingleItem().Entries
            .Where(e => e.Comment == ScheduleValidationKeys.ConsecutiveDays)
            .ShouldHaveSingleItem();
        run.Date.ShouldBe(WeekMonday);
        run.CommentParams!["actualDays"].ShouldBe("11");
    }

    [Test]
    public async Task RangeCheckEndingInsideTheRun_CountsTheRunInFull()
    {
        _host.Service.QueueRangeCheck(MonthStart, WeekMonday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        var run = validations.ShouldHaveSingleItem().Entries
            .Where(e => e.Comment == ScheduleValidationKeys.ConsecutiveDays)
            .ShouldHaveSingleItem();
        run.Date.ShouldBe(RunStart);
        run.CommentParams!["actualDays"].ShouldBe("11");
    }

    private async Task<Guid> AddColleagueRunAsync(DateOnly first, DateOnly last)
    {
        var colleagueId = await _host.AddEmployeeAsync("Beispiel", "Ben");
        _colleagueShiftId = await _host.AddCustomerShiftAsync("Bundesplatz 3", "3003", "Bern");
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            await AddColleagueWorkAsync(colleagueId, day);
        }

        return colleagueId;
    }

    private Task<Work> AddColleagueWorkAsync(Guid colleagueId, DateOnly day)
        => _host.AddWorkAsync(colleagueId, _colleagueShiftId, day, new TimeOnly(7, 0), new TimeOnly(15, 0));

    private static void AssertProbeFindings(List<ScheduleValidationNotificationDto> entries)
    {
        var run = entries.Where(e => e.Comment == ScheduleValidationKeys.ConsecutiveDays).ShouldHaveSingleItem();
        run.Date.ShouldBe(RunStart);
        run.CommentParams!["actualDays"].ShouldBe("11");

        entries.ShouldContain(e => e.Comment == ScheduleValidationKeys.Overtime && e.Date == DayAfterNights);
        entries.ShouldContain(e => e.Comment == ScheduleValidationKeys.WeeklyOvertime && e.Date == WeekMonday);
        entries.ShouldContain(e => e.Comment == ScheduleValidationKeys.MinRestDays && e.Date == WeekMonday);
        entries.ShouldContain(e => e.Comment == ScheduleValidationKeys.RestViolation && e.Date == SecondNight);
    }

    private static List<string> WorkTimeFindings(List<ScheduleValidationNotificationDto> entries)
        => entries
            .Where(e => WorkTimeKeys.Contains(e.Comment))
            .Select(e => $"{e.Date:yyyy-MM-dd}|{e.Comment}|{string.Join(",", e.CommentParams!.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))}")
            .ToList();
}
