// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for HolidayWorkTimelineCheck: the single-day check sees a night shift from the previous evening, the range
/// candidates cover every worked day from the range start on plus the spill-in days, and clients that only appear
/// through spill-in are evaluated exactly once while clients already checked in the range loop are skipped.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class HolidayWorkTimelineCheckTests
{
    private static readonly DateOnly July31 = new(2026, 7, 31);
    private static readonly DateOnly August1 = new(2026, 8, 1);
    private static readonly DateOnly August2 = new(2026, 8, 2);
    private static readonly Guid ClientId = Guid.NewGuid();

    [Test]
    public void WorksOnDate_NightShiftFromThePreviousEvening_CountsForTheCheckedDay()
    {
        var timeline = Timeline(Block(ScheduleBlockType.Work, July31, new TimeOnly(22, 0), August1, new TimeOnly(6, 0)));

        HolidayWorkTimelineCheck.WorksOnDate(timeline, August1).ShouldBeTrue();
    }

    [Test]
    public void WorksOnDate_OnlyAnAbsence_IsNotWork()
    {
        var timeline = Timeline(Block(ScheduleBlockType.Break, August1, new TimeOnly(8, 0), August1, new TimeOnly(16, 0)));

        HolidayWorkTimelineCheck.WorksOnDate(timeline, August1).ShouldBeFalse();
    }

    [Test]
    public void RangeCandidates_IncludesTheMorningAfterTheLastDayAndTheSpillInDays()
    {
        var timeline = Timeline(
            Block(ScheduleBlockType.Work, July31, new TimeOnly(8, 0), July31, new TimeOnly(16, 0)),
            Block(ScheduleBlockType.Work, August1, new TimeOnly(22, 0), August2, new TimeOnly(6, 0)));
        var spillIn = new HolidayWorkSpillIn(
            new Dictionary<Guid, List<DateOnly>> { [ClientId] = [August1] },
            new Dictionary<Guid, string>());

        HolidayWorkTimelineCheck.RangeCandidates(timeline, August1, spillIn).ShouldBe([August1, August2]);
    }

    [Test]
    public async Task EvaluateSpillInOnlyAsync_EvaluatesOnlyClientsWithoutWorkInTheRange()
    {
        var spillInOnlyClient = Guid.NewGuid();
        var evaluator = Substitute.For<IHolidayWorkEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call => new List<ScheduleValidationNotificationDto> { new() { ClientId = call.ArgAt<Guid>(0), Date = August1 } });
        var spillIn = new HolidayWorkSpillIn(
            new Dictionary<Guid, List<DateOnly>> { [ClientId] = [August1], [spillInOnlyClient] = [August1] },
            new Dictionary<Guid, string> { [spillInOnlyClient] = "Beispiel Ben" });

        var entries = await HolidayWorkTimelineCheck.EvaluateSpillInOnlyAsync(evaluator, spillIn, [ClientId], CancellationToken.None);

        entries.ShouldHaveSingleItem().ClientId.ShouldBe(spillInOnlyClient);
        await evaluator.Received(1).EvaluateAsync(
            spillInOnlyClient, "Beispiel Ben",
            Arg.Is<IReadOnlyCollection<DateOnly>>(dates => dates.SequenceEqual(new[] { August1 })),
            Arg.Any<CancellationToken>());
        await evaluator.DidNotReceive().EvaluateAsync(
            ClientId, Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>());
    }

    private static ClientTimeline Timeline(params ScheduleBlock[] blocks)
    {
        var timeline = new ClientTimeline(ClientId);
        timeline.AddBlocks(blocks);
        timeline.SortBlocks();
        return timeline;
    }

    private static ScheduleBlock Block(ScheduleBlockType type, DateOnly startDate, TimeOnly start, DateOnly endDate, TimeOnly end) =>
        new(Guid.NewGuid(), type, ClientId, startDate.ToDateTime(start), endDate.ToDateTime(end));
}
