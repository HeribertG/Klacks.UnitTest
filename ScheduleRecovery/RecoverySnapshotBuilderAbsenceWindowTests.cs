// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for the real time spans <see cref="RecoverySnapshotBuilder.BuildWorks"/> keeps of the blocking absences: a
/// service that only reaches a neighbouring day is blocked by the span it overlaps, so a blocking break keeps its times, an
/// on-call duty keeps none, and a break stored as 00:00 to 00:00 or 00:00 to 23:59 spans the whole day.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderAbsenceWindowTests
{
    private static readonly Guid Agent = new("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid OnCallAbsence = new("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid SickAbsence = new("00000000-0000-0000-0000-0000000000e2");
    private static readonly DateTime Date = new(2026, 6, 4);
    private static readonly DateOnly Day = new(2026, 6, 4);

    private static ScheduleCell BreakCell(Guid absenceId, TimeSpan start, TimeSpan end) => new()
    {
        Id = Guid.NewGuid(),
        EntryType = (int)ScheduleEntryType.Break,
        ClientId = Agent,
        EntryDate = Date,
        StartTime = start,
        EndTime = end,
        EntryId = absenceId,
        SourceId = Guid.NewGuid(),
    };

    [Test]
    public void A_blocking_break_keeps_its_real_span()
    {
        RecoverySnapshotBuilder.BuildWorks(
            [BreakCell(SickAbsence, new TimeSpan(14, 0, 0), new TimeSpan(18, 0, 0))],
            new HashSet<Guid> { OnCallAbsence },
            out var breakDays);

        var window = breakDays.BlockingWindows[new CellKey(Agent, Day)].ShouldHaveSingleItem();
        window.Start.ShouldBe(Date.AddHours(14));
        window.End.ShouldBe(Date.AddHours(18));
    }

    [Test]
    public void A_full_day_break_stored_with_equal_times_spans_the_whole_day()
    {
        RecoverySnapshotBuilder.BuildWorks(
            [BreakCell(SickAbsence, TimeSpan.Zero, TimeSpan.Zero)],
            new HashSet<Guid> { OnCallAbsence },
            out var breakDays);

        var window = breakDays.BlockingWindows[new CellKey(Agent, Day)].ShouldHaveSingleItem();
        window.Start.ShouldBe(Date);
        window.End.ShouldBe(Date.AddDays(1));
    }

    [Test]
    public void Only_a_sealed_day_is_marked_sealed_in_the_availability()
    {
        var breakDays = new RecoveryBreakDays([(Agent, Day), (Agent, Day.AddDays(1))], []);
        breakDays.Sealed.Add((Agent, Day.AddDays(1)));
        var contracts = new Dictionary<Guid, EffectiveContractData>
        {
            [Agent] = new EffectiveContractData { HasActiveContract = false },
        };

        var availability = RecoverySnapshotBuilder.BuildAvailability(
            [Agent], contracts, breakDays,
            new Dictionary<(Guid AgentId, DateOnly Date), ScheduleCommandKeyword>(), Day, Day.AddDays(1));

        availability[new CellKey(Agent, Day)].IsSealed.ShouldBeFalse();
        availability[new CellKey(Agent, Day)].HasBreakBlocker.ShouldBeTrue();
        availability[new CellKey(Agent, Day.AddDays(1))].IsSealed.ShouldBeTrue();
    }

    [Test]
    public void An_on_call_duty_keeps_no_span()
    {
        RecoverySnapshotBuilder.BuildWorks(
            [BreakCell(OnCallAbsence, TimeSpan.Zero, new TimeSpan(23, 59, 0))],
            new HashSet<Guid> { OnCallAbsence },
            out var breakDays);

        breakDays.BlockingWindows.ShouldBeEmpty();
        breakDays.OnCall.ShouldContain((Agent, Day));
    }
}
