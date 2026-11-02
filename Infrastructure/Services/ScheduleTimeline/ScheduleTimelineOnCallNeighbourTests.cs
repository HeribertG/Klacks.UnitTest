// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The live single check on the real pipeline: a night shift that reaches across midnight into an on-call duty of the
/// next day, and an on-call duty of the checked day that a night shift of the day before ends in, are reported as the
/// on-call overlap the range check reports, whichever of the two days is checked.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineOnCallNeighbourTests
{
    private const decimal ShiftHours = 8m;
    private const string WorkTimeRangeParam = "workTimeRange";
    private const string OnCallTimeRangeParam = "onCallTimeRange";
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private static readonly TimeOnly FullDayStart = new(0, 0);
    private static readonly TimeOnly FullDayEnd = new(23, 59);

    private ScheduleTimelineTestHost _host = null!;
    private Guid _clientId;
    private Guid _shiftId;
    private Guid _onCallId;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _clientId = await _host.AddEmployeeAsync("Muster", "Anna");
        _shiftId = await AddShiftAsync();
        _onCallId = await AddOnCallAsync();
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [TestCase(2026, 3, 2)]
    [TestCase(2026, 10, 31)]
    [TestCase(2026, 12, 31)]
    public async Task NightShiftIntoAnOnCallOfTheNextDay_IsReportedWhenTheShiftDayIsChecked(int year, int month, int day)
    {
        var shiftDay = new DateOnly(year, month, day);
        await _host.AddWorkAsync(_clientId, _shiftId, shiftDay, NightStart, NightEnd);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), null);

        var overlap = (await RunSingleCheckAsync(shiftDay)).ShouldHaveSingleItem();

        overlap.Date.ShouldBe(shiftDay);
        overlap.Type.ShouldBe(ScheduleValidationType.Warning);
    }

    [TestCase(2026, 3, 2)]
    [TestCase(2026, 10, 31)]
    [TestCase(2026, 12, 31)]
    public async Task NightShiftIntoAnOnCallOfTheNextDay_IsReportedWhenTheOnCallDayIsChecked(int year, int month, int day)
    {
        var shiftDay = new DateOnly(year, month, day);
        await _host.AddWorkAsync(_clientId, _shiftId, shiftDay, NightStart, NightEnd);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), null);

        var overlap = (await RunSingleCheckAsync(shiftDay.AddDays(1))).ShouldHaveSingleItem();

        overlap.Date.ShouldBe(shiftDay);
    }

    [Test]
    public async Task NightShiftIntoAnOnCall_SingleAndRangeCheckReportTheSameOverlap()
    {
        var shiftDay = new DateOnly(2026, 3, 3);
        await _host.AddWorkAsync(_clientId, _shiftId, shiftDay, NightStart, NightEnd);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), null);

        _host.Service.QueueCheck(_clientId, shiftDay, null);
        _host.Service.QueueRangeCheck(shiftDay, shiftDay.AddDays(1), null);
        await _host.StartAsync();
        var notifications = await _host.WaitForValidationsAsync(2);

        var single = OnCallEntries(notifications.Single(n => !n.IsFullRefresh)).ShouldHaveSingleItem();
        var range = OnCallEntries(notifications.Single(n => n.IsFullRefresh)).ShouldHaveSingleItem();
        single.Date.ShouldBe(range.Date);
        single.Type.ShouldBe(range.Type);
        single.CommentParams[WorkTimeRangeParam].ShouldBe(range.CommentParams[WorkTimeRangeParam]);
        single.CommentParams[OnCallTimeRangeParam].ShouldBe(range.CommentParams[OnCallTimeRangeParam]);
    }

    [Test]
    public async Task NightShiftAndAnOnCallTwoDaysLater_AreNotRelated()
    {
        var shiftDay = new DateOnly(2026, 3, 3);
        await _host.AddWorkAsync(_clientId, _shiftId, shiftDay, NightStart, NightEnd);
        await AddOnCallBreakAsync(shiftDay.AddDays(2), null);

        (await RunSingleCheckAsync(shiftDay)).ShouldBeEmpty();
    }

    [Test]
    public async Task NightShiftOfTheRealPlan_DoesNotMeetAnOnCallOfAScenarioOnTheNextDay()
    {
        var token = Guid.NewGuid();
        var shiftDay = new DateOnly(2026, 3, 3);
        await _host.AddWorkAsync(_clientId, _shiftId, shiftDay, NightStart, NightEnd);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), token);

        (await RunSingleCheckAsync(shiftDay.AddDays(1), token)).ShouldBeEmpty();
    }

    [Test]
    public async Task NightShiftTheClientReplacesOn_IsReportedWhenTheOnCallDayIsChecked()
    {
        var shiftDay = new DateOnly(2026, 3, 3);
        var owner = await _host.AddEmployeeAsync("Beispiel", "Ben");
        var work = await _host.AddWorkAsync(owner, _shiftId, shiftDay, NightStart, NightEnd);
        await AddReplacementAsync(work.Id, _clientId);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), null);

        var overlap = (await RunSingleCheckAsync(shiftDay.AddDays(1))).ShouldHaveSingleItem();

        overlap.Date.ShouldBe(shiftDay);
        overlap.ClientId.ShouldBe(_clientId);
    }

    [Test]
    public async Task NightShiftTheClientReplacesOn_IsReportedWhenTheShiftDayIsChecked()
    {
        var shiftDay = new DateOnly(2026, 3, 3);
        var owner = await _host.AddEmployeeAsync("Beispiel", "Ben");
        var work = await _host.AddWorkAsync(owner, _shiftId, shiftDay, NightStart, NightEnd);
        await AddReplacementAsync(work.Id, _clientId);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), null);

        (await RunSingleCheckAsync(shiftDay)).ShouldHaveSingleItem().Date.ShouldBe(shiftDay);
    }

    [Test]
    public async Task NightShiftTheClientReplacesOnOverAnAbsenceOfTheNextDay_IsNoUncoveredAbsence()
    {
        var shiftDay = new DateOnly(2026, 3, 3);
        var owner = await _host.AddEmployeeAsync("Beispiel", "Ben");
        var work = await _host.AddWorkAsync(owner, _shiftId, shiftDay, NightStart, NightEnd);
        await AddReplacementAsync(work.Id, _clientId);
        await AddOnCallBreakAsync(shiftDay.AddDays(1), null, await AddAbsenceAsync(isOnCall: false));

        _host.Service.QueueCheck(_clientId, shiftDay.AddDays(1), null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        _host.Validations.ShouldHaveSingleItem().Entries
            .ShouldNotContain(entry => entry.Comment == ScheduleValidationKeys.UncoveredAbsence);
    }

    [Test]
    public async Task OnCallOverlapOnTheCheckedDay_StillComesOnce()
    {
        var day = new DateOnly(2026, 3, 3);
        await _host.AddWorkAsync(_clientId, _shiftId, day, new TimeOnly(8, 0), new TimeOnly(16, 0));
        await AddOnCallBreakAsync(day, null);

        (await RunSingleCheckAsync(day)).ShouldHaveSingleItem().Date.ShouldBe(day);
    }

    private async Task<List<ScheduleValidationNotificationDto>> RunSingleCheckAsync(DateOnly date, Guid? analyseToken = null)
    {
        _host.Service.QueueCheck(_clientId, date, analyseToken);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);
        return OnCallEntries(_host.Validations.ShouldHaveSingleItem());
    }

    private static List<ScheduleValidationNotificationDto> OnCallEntries(ScheduleValidationListNotificationDto notification)
        => notification.Entries.Where(entry => entry.Comment == ScheduleValidationKeys.OnCallOverlap).ToList();

    private async Task<Guid> AddShiftAsync()
    {
        await using var context = _host.NewContext();
        var shift = new Shift { Id = Guid.NewGuid(), Name = "NT", Abbreviation = "NT" };
        context.Shift.Add(shift);
        await context.SaveChangesAsync();
        return shift.Id;
    }

    private Task<Guid> AddOnCallAsync() => AddAbsenceAsync(isOnCall: true);

    private async Task<Guid> AddAbsenceAsync(bool isOnCall)
    {
        await using var context = _host.NewContext();
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = isOnCall ? "Pikett" : "Krankheit", En = isOnCall ? "On call" : "Illness" },
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            IsOnCall = isOnCall,
        };
        context.Absence.Add(absence);
        await context.SaveChangesAsync();
        return absence.Id;
    }

    private async Task AddReplacementAsync(Guid workId, Guid substituteId)
    {
        await using var context = _host.NewContext();
        context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = WorkChangeType.ReplacementWithin,
            ChangeTime = ShiftHours,
            StartTime = NightStart,
            EndTime = NightEnd,
            ReplaceClientId = substituteId,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddOnCallBreakAsync(DateOnly date, Guid? analyseToken, Guid? absenceId = null)
    {
        await using var context = _host.NewContext();
        context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            AbsenceId = absenceId ?? _onCallId,
            CurrentDate = date,
            StartTime = FullDayStart,
            EndTime = FullDayEnd,
            WorkTime = ShiftHours,
            AnalyseToken = analyseToken,
        });
        await context.SaveChangesAsync();
    }
}
