// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The live schedule check on the real pipeline: a work on a day with an absence is reported as one uncovered-absence
/// Error instead of a time collision and only while part of the overlap has no replacement. The planner sees person,
/// day, shift and absence type; on-call absences and work-on-work collisions keep their own findings.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineUncoveredAbsenceTests
{
    private const decimal ShiftHours = 8m;
    private const decimal HalfShiftHours = 4m;
    private const string ShiftsParam = UncoveredAbsenceEntries.ShiftsParamKey;
    private const string EarlyShift = "FD";
    private const string LateShift = "SD";

    private static readonly DateOnly Tuesday = new(2026, 3, 3);
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftMiddle = new(12, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);
    private static readonly TimeOnly LateStart = new(17, 0);
    private static readonly TimeOnly LateEnd = new(21, 0);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);
    private static readonly TimeOnly FullDayStart = new(0, 0);
    private static readonly TimeOnly FullDayEnd = new(23, 59);
    private static readonly TimeOnly NoTimesRecorded = new(0, 0);

    private ScheduleTimelineTestHost _host = null!;
    private Guid _absentClientId;
    private Guid _replacementClientId;
    private Guid _earlyShiftId;
    private Guid _lateShiftId;
    private Guid _sickAbsenceId;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _absentClientId = await _host.AddEmployeeAsync("Muster", "Anna");
        _replacementClientId = await _host.AddEmployeeAsync("Beispiel", "Ben");
        _earlyShiftId = await AddShiftAsync(EarlyShift);
        _lateShiftId = await AddShiftAsync(LateShift);
        _sickAbsenceId = await AddAbsenceAsync(isOnCall: false);
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [Test]
    public async Task FullDayAbsenceOverWork_NamesPersonDayShiftAndAbsence_InsteadOfACollision()
    {
        await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        var finding = findings.ShouldHaveSingleItem();
        finding.Type.ShouldBe(ScheduleValidationType.Error);
        finding.ClientId.ShouldBe(_absentClientId);
        finding.ClientName.ShouldBe("Muster Anna");
        finding.Date.ShouldBe(Tuesday);
        finding.CommentParams[ShiftsParam].ShouldBe(EarlyShift);
        finding.CommentParams[LocalizedCommentParamKeys.Absence].ShouldBe("Krankheit/Unfall");
        finding.CommentParams[LocalizedCommentParamKeys.AbsenceMultiLanguage].ShouldContain("Illness/Accident");
    }

    [Test]
    public async Task DurationModeAbsenceOverWork_IsReported()
    {
        await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, NoTimesRecorded, NoTimesRecorded);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldHaveSingleItem();
    }

    [Test]
    public async Task FullReplacementStart_LeavesNoFinding()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementStart, ShiftHours, default, default);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldBeEmpty();
    }

    [Test]
    public async Task FullReplacementWithin_LeavesNoFinding()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, ShiftHours, ShiftStart, ShiftEnd);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldBeEmpty();
    }

    [Test]
    public async Task ReplacementWithinOverTheNightShift_LeavesNoFinding()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, NightStart, NightEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, ShiftHours, NightStart, NightEnd);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldBeEmpty();
    }

    [Test]
    public async Task TwoReplacementsThatTogetherCoverTheShift_LeaveNoFinding()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, HalfShiftHours, ShiftStart, ShiftMiddle);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, HalfShiftHours, ShiftMiddle, ShiftEnd);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldBeEmpty();
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task PartialReplacement_LeavesTheFindingForTheRest(WorkChangeType type)
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, type, HalfShiftHours, ShiftStart, ShiftMiddle);

        var (_, findings) = await RunSingleCheckAsync();

        findings.ShouldHaveSingleItem();
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    public async Task ZeroHourReplacement_DoesNotCover(WorkChangeType type)
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, type, 0m, default, default);

        var (_, findings) = await RunSingleCheckAsync();

        findings.ShouldHaveSingleItem();
    }

    [Test]
    public async Task ReplacementWithinWithEqualBounds_DoesNotCover()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, ShiftHours, ShiftStart, ShiftStart);

        var (_, findings) = await RunSingleCheckAsync();

        findings.ShouldHaveSingleItem();
    }

    [Test]
    public async Task ChangeWithoutReplacingClient_DoesNotCover()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, ShiftHours, ShiftStart, ShiftEnd, replaceClientId: null);

        var (_, findings) = await RunSingleCheckAsync();

        findings.ShouldHaveSingleItem();
    }

    [Test]
    public async Task HalfDayAbsenceWhoseOverlapIsReplaced_LeavesNoFinding()
    {
        var work = await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, ShiftStart, ShiftMiddle);
        await AddReplacementAsync(work.Id, WorkChangeType.ReplacementWithin, HalfShiftHours, ShiftStart, ShiftMiddle);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldBeEmpty();
    }

    [Test]
    public async Task TwoUncoveredShiftsOnOneDay_AreOneEntryNamingBoth()
    {
        await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await _host.AddWorkAsync(_absentClientId, _lateShiftId, Tuesday, LateStart, LateEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);

        var (_, findings) = await RunSingleCheckAsync();

        findings.ShouldHaveSingleItem().CommentParams[ShiftsParam].ShouldBe($"{EarlyShift}, {LateShift}");
    }

    [Test]
    public async Task OnCallAbsence_KeepsItsOnCallOverlap()
    {
        await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(await AddAbsenceAsync(isOnCall: true), FullDayStart, FullDayEnd);

        var (collisions, findings) = await RunSingleCheckAsync();

        collisions.Collisions.ShouldBeEmpty();
        findings.ShouldBeEmpty();
        _host.Validations.ShouldHaveSingleItem().Entries
            .ShouldContain(entry => entry.Comment == ScheduleValidationKeys.OnCallOverlap);
    }

    [Test]
    public async Task TwoOverlappingWorks_StayACollisionNextToTheFinding()
    {
        await _host.AddWorkAsync(_absentClientId, _earlyShiftId, Tuesday, ShiftStart, ShiftEnd);
        await _host.AddWorkAsync(_absentClientId, _lateShiftId, Tuesday, ShiftMiddle, LateEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd);

        var (collisions, findings) = await RunSingleCheckAsync();

        var collision = collisions.Collisions.ShouldHaveSingleItem();
        collision.BlockType1.ShouldBe(nameof(ScheduleBlockType.Work));
        collision.BlockType2.ShouldBe(nameof(ScheduleBlockType.Work));
        findings.ShouldHaveSingleItem();
    }

    [Test]
    public async Task RangeCheck_ReportsTheFindingOfTheScenarioOnly()
    {
        var token = Guid.NewGuid();
        await AddScenarioWorkAsync(token);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd, token);
        await _host.AddWorkAsync(_replacementClientId, _lateShiftId, Tuesday, ShiftStart, ShiftEnd);
        await AddBreakAsync(_sickAbsenceId, FullDayStart, FullDayEnd, clientId: _replacementClientId);

        _host.Service.QueueRangeCheck(Tuesday, Tuesday, token);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        var notification = _host.Validations.ShouldHaveSingleItem();
        notification.AnalyseToken.ShouldBe(token);
        notification.Entries.Where(entry => entry.Comment == ScheduleValidationKeys.UncoveredAbsence)
            .ShouldHaveSingleItem().ClientId.ShouldBe(_absentClientId);
        _host.Collisions.ShouldHaveSingleItem().Collisions.ShouldBeEmpty();
    }

    private async Task<(CollisionListNotificationDto Collisions, List<ScheduleValidationNotificationDto> Findings)> RunSingleCheckAsync()
    {
        _host.Service.QueueCheck(_absentClientId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);
        var findings = _host.Validations.ShouldHaveSingleItem().Entries
            .Where(entry => entry.Comment == ScheduleValidationKeys.UncoveredAbsence)
            .ToList();
        return (_host.Collisions.ShouldHaveSingleItem(), findings);
    }

    private async Task<Guid> AddShiftAsync(string abbreviation)
    {
        await using var context = _host.NewContext();
        var shift = new Shift { Id = Guid.NewGuid(), Name = abbreviation, Abbreviation = abbreviation };
        context.Shift.Add(shift);
        await context.SaveChangesAsync();
        return shift.Id;
    }

    private async Task<Guid> AddAbsenceAsync(bool isOnCall)
    {
        await using var context = _host.NewContext();
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = "Krankheit/Unfall", En = "Illness/Accident" },
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            IsOnCall = isOnCall,
        };
        context.Absence.Add(absence);
        await context.SaveChangesAsync();
        return absence.Id;
    }

    private async Task AddScenarioWorkAsync(Guid token)
    {
        await using var context = _host.NewContext();
        context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _absentClientId,
            ShiftId = _earlyShiftId,
            CurrentDate = Tuesday,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = ShiftHours,
            AnalyseToken = token,
        });
        await context.SaveChangesAsync();
    }

    private async Task AddBreakAsync(
        Guid absenceId, TimeOnly start, TimeOnly end, Guid? analyseToken = null, Guid? clientId = null)
    {
        await using var context = _host.NewContext();
        context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? _absentClientId,
            AbsenceId = absenceId,
            CurrentDate = Tuesday,
            StartTime = start,
            EndTime = end,
            WorkTime = ShiftHours,
            AnalyseToken = analyseToken,
        });
        await context.SaveChangesAsync();
    }

    private Task AddReplacementAsync(Guid workId, WorkChangeType type, decimal changeTime, TimeOnly start, TimeOnly end)
        => AddReplacementAsync(workId, type, changeTime, start, end, _replacementClientId);

    private async Task AddReplacementAsync(
        Guid workId, WorkChangeType type, decimal changeTime, TimeOnly start, TimeOnly end, Guid? replaceClientId)
    {
        await using var context = _host.NewContext();
        context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = type,
            ChangeTime = changeTime,
            StartTime = start,
            EndTime = end,
            ReplaceClientId = replaceClientId,
        });
        await context.SaveChangesAsync();
    }
}
