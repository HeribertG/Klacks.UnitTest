// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the lead and trail of a planned work row: they become briefing and debriefing work changes, so the schedule
/// timeline places the occupied time around the not-yet-written work (also across midnight) and a neighbouring work or
/// absence that only the lead or the trail touches shows up as a collision.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class PlannedWorkRowPaddingTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 8, 1);

    private TimelineCalculationService _timeline = null!;

    [SetUp]
    public void SetUp()
    {
        _timeline = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            NullLogger<TimelineCalculationService>.Instance);
    }

    [Test]
    public void NoLeadAndNoTrail_YieldNoWorkChanges()
    {
        var row = new PlannedWorkRow(ClientId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        PlannedWorkRowPadding.ToWorkChanges(row, Guid.NewGuid()).ShouldBeEmpty();
    }

    [Test]
    public void LeadBecomesABriefing_AndTrailBecomesADebriefing_OfTheGivenWork()
    {
        var workId = Guid.NewGuid();
        var row = new PlannedWorkRow(ClientId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), null, 1.5m, 0.5m, 2m);

        var changes = PlannedWorkRowPadding.ToWorkChanges(row, workId);

        changes.Count.ShouldBe(2);
        changes.ShouldAllBe(c => c.WorkId == workId);
        changes.Single(c => c.Type == WorkChangeType.Briefing).ChangeTime.ShouldBe(1.5m);
        changes.Single(c => c.Type == WorkChangeType.Debriefing).ChangeTime.ShouldBe(0.5m);
    }

    [Test]
    public void TheTrailOfANightShift_CollidesWithAnAbsenceOfTheNextMorning()
    {
        var withoutTrail = CollisionsFor(new PlannedWorkRow(ClientId, Day, new TimeOnly(22, 0), new TimeOnly(6, 0)), NextMorningBreak());
        var withTrail = CollisionsFor(
            new PlannedWorkRow(ClientId, Day, new TimeOnly(22, 0), new TimeOnly(6, 0), null, 0m, 2m), NextMorningBreak());

        withoutTrail.ShouldBeEmpty();
        withTrail.ShouldNotBeEmpty();
    }

    [Test]
    public void TheLeadOfAWorkStartingAfterMidnight_CollidesWithAnAbsenceOfTheEveningBefore()
    {
        var previous = Day.AddDays(-1);
        var evening = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = previous,
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(0, 0),
        };

        var withoutLead = CollisionsFor(new PlannedWorkRow(ClientId, Day, new TimeOnly(0, 30), new TimeOnly(8, 0)), evening);
        var withLead = CollisionsFor(
            new PlannedWorkRow(ClientId, Day, new TimeOnly(0, 30), new TimeOnly(8, 0), null, 1m), evening);

        withoutLead.ShouldBeEmpty();
        withLead.ShouldNotBeEmpty();
    }

    private Break NextMorningBreak() => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        CurrentDate = Day.AddDays(1),
        StartTime = new TimeOnly(6, 30),
        EndTime = new TimeOnly(18, 0),
    };

    private List<(ScheduleBlock A, ScheduleBlock B)> CollisionsFor(PlannedWorkRow row, Break absence)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = row.ClientId,
            CurrentDate = row.Date,
            StartTime = row.StartTime,
            EndTime = row.EndTime,
            ShiftId = Guid.NewGuid(),
        };
        var blocks = _timeline.CalculateScheduleBlocks(
            [work], PlannedWorkRowPadding.ToWorkChanges(row, work.Id).ToList(), [absence]);

        var timeline = new ClientTimeline(ClientId);
        timeline.AddBlocks(blocks);
        timeline.SortBlocks();
        return timeline.GetCollisions();
    }
}
