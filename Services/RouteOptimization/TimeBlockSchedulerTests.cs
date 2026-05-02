// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for TimeBlockScheduler: midnight-crossing blocks, effective budget, arrival times, and block placement.
/// </summary>

using Shouldly;
using Klacks.Api.Domain.Services.RouteOptimization;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class TimeBlockSchedulerTests
{
    private const double SecondsPerDay = 86400.0;

    private static TimeBlock CreateUnmovableBlock(string startTime, string endTime, string name = "Break")
    {
        var start = TimeOnly.Parse(startTime);
        var end = TimeOnly.Parse(endTime);
        var startSeconds = start.ToTimeSpan().TotalSeconds;
        var endSeconds = end.ToTimeSpan().TotalSeconds;
        var duration = endSeconds > startSeconds
            ? TimeSpan.FromSeconds(endSeconds - startSeconds)
            : TimeSpan.FromSeconds(endSeconds + SecondsPerDay - startSeconds);

        return new TimeBlock(Guid.NewGuid(), name, start, end, duration, false);
    }

    private static TimeBlock CreateMovableBlock(int durationMinutes, string name = "Flexible Break")
    {
        return new TimeBlock(Guid.NewGuid(), name, null, null, TimeSpan.FromMinutes(durationMinutes), true);
    }

    private static DistanceMatrix CreateSimpleDistanceMatrix(int locationCount, double travelTimeSeconds = 600)
    {
        var locations = new List<Location>();
        for (int i = 0; i < locationCount; i++)
        {
            locations.Add(new Location
            {
                Name = $"Location {i}",
                Address = $"Address {i}",
                Latitude = 46.9 + i * 0.01,
                Longitude = 7.4 + i * 0.01,
                ShiftId = i == 0 ? Guid.Empty : Guid.NewGuid(),
                WorkTime = TimeSpan.FromMinutes(30),
                BriefingTime = TimeSpan.FromMinutes(5),
                DebriefingTime = TimeSpan.FromMinutes(5),
            });
        }

        var dist = new double[locationCount, locationCount];
        var dur = new double[locationCount, locationCount];
        for (int i = 0; i < locationCount; i++)
        {
            for (int j = 0; j < locationCount; j++)
            {
                if (i != j)
                {
                    dist[i, j] = 5.0;
                    dur[i, j] = travelTimeSeconds;
                }
            }
        }

        return new DistanceMatrix(locations, dist, dur);
    }

    #region SkipOverUnmovableBlocks

    [Test]
    public void SkipOverUnmovableBlocks_NormalBlock_SkipsCorrectly()
    {
        var block = CreateUnmovableBlock("12:00", "13:00");
        var blocks = new List<TimeBlock> { block };

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(
            TimeOnly.Parse("12:30").ToTimeSpan().TotalSeconds, blocks);

        result.ShouldBe(TimeOnly.Parse("13:00").ToTimeSpan().TotalSeconds);
    }

    [Test]
    public void SkipOverUnmovableBlocks_NormalBlock_TimeBeforeBlock_NoSkip()
    {
        var block = CreateUnmovableBlock("12:00", "13:00");
        var blocks = new List<TimeBlock> { block };
        var timeBefore = TimeOnly.Parse("11:30").ToTimeSpan().TotalSeconds;

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(timeBefore, blocks);

        result.ShouldBe(timeBefore);
    }

    [Test]
    public void SkipOverUnmovableBlocks_NormalBlock_TimeAfterBlock_NoSkip()
    {
        var block = CreateUnmovableBlock("12:00", "13:00");
        var blocks = new List<TimeBlock> { block };
        var timeAfter = TimeOnly.Parse("13:30").ToTimeSpan().TotalSeconds;

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(timeAfter, blocks);

        result.ShouldBe(timeAfter);
    }

    [Test]
    public void SkipOverUnmovableBlocks_MidnightBlock_SkipsWhenInsideAfterMidnight()
    {
        var block = CreateUnmovableBlock("23:00", "01:00");
        var blocks = new List<TimeBlock> { block };
        var timeInsideBlock = TimeOnly.Parse("23:30").ToTimeSpan().TotalSeconds;

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(timeInsideBlock, blocks);

        var expectedEnd = TimeOnly.Parse("01:00").ToTimeSpan().TotalSeconds + SecondsPerDay;
        result.ShouldBe(expectedEnd);
    }

    [Test]
    public void SkipOverUnmovableBlocks_MidnightBlock_SkipsAtExactStart()
    {
        var block = CreateUnmovableBlock("23:00", "01:00");
        var blocks = new List<TimeBlock> { block };
        var timeAtStart = TimeOnly.Parse("23:00").ToTimeSpan().TotalSeconds;

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(timeAtStart, blocks);

        var expectedEnd = TimeOnly.Parse("01:00").ToTimeSpan().TotalSeconds + SecondsPerDay;
        result.ShouldBe(expectedEnd);
    }

    [Test]
    public void SkipOverUnmovableBlocks_MidnightBlock_NoSkipBeforeStart()
    {
        var block = CreateUnmovableBlock("23:00", "01:00");
        var blocks = new List<TimeBlock> { block };
        var timeBefore = TimeOnly.Parse("22:00").ToTimeSpan().TotalSeconds;

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(timeBefore, blocks);

        result.ShouldBe(timeBefore);
    }

    [Test]
    public void SkipOverUnmovableBlocks_MultipleBlocks_WithMidnightCrossing()
    {
        var normalBlock = CreateUnmovableBlock("12:00", "12:30");
        var midnightBlock = CreateUnmovableBlock("23:30", "00:30");
        var blocks = new List<TimeBlock> { normalBlock, midnightBlock };

        var timeInNormal = TimeOnly.Parse("12:15").ToTimeSpan().TotalSeconds;
        var resultNormal = TimeBlockScheduler.SkipOverUnmovableBlocks(timeInNormal, blocks);
        resultNormal.ShouldBe(TimeOnly.Parse("12:30").ToTimeSpan().TotalSeconds);

        var timeInMidnight = TimeOnly.Parse("23:45").ToTimeSpan().TotalSeconds;
        var resultMidnight = TimeBlockScheduler.SkipOverUnmovableBlocks(timeInMidnight, blocks);
        var expectedMidnightEnd = TimeOnly.Parse("00:30").ToTimeSpan().TotalSeconds + SecondsPerDay;
        resultMidnight.ShouldBe(expectedMidnightEnd);
    }

    [Test]
    public void SkipOverUnmovableBlocks_EmptyList_ReturnsOriginalTime()
    {
        var time = TimeOnly.Parse("12:00").ToTimeSpan().TotalSeconds;

        var result = TimeBlockScheduler.SkipOverUnmovableBlocks(time, new List<TimeBlock>());

        result.ShouldBe(time);
    }

    #endregion

    #region CalculateEffectiveBudget

    [Test]
    public void CalculateEffectiveBudget_SubtractsAllBlockDurations()
    {
        var blocks = new List<TimeBlock>
        {
            CreateUnmovableBlock("12:00", "13:00"),
            CreateMovableBlock(30),
        };

        var budget = TimeSpan.FromHours(8).TotalSeconds;
        var result = TimeBlockScheduler.CalculateEffectiveBudget(budget, blocks);

        result.ShouldBe(budget - TimeSpan.FromMinutes(90).TotalSeconds);
    }

    [Test]
    public void CalculateEffectiveBudget_MidnightBlock_CorrectDuration()
    {
        var midnightBlock = CreateUnmovableBlock("23:00", "01:00");

        var budget = TimeSpan.FromHours(10).TotalSeconds;
        var result = TimeBlockScheduler.CalculateEffectiveBudget(budget, new List<TimeBlock> { midnightBlock });

        result.ShouldBe(budget - TimeSpan.FromHours(2).TotalSeconds);
    }

    [Test]
    public void CalculateEffectiveBudget_BlocksExceedBudget_ReturnsZero()
    {
        var block = CreateUnmovableBlock("12:00", "13:00");
        var budget = TimeSpan.FromMinutes(30).TotalSeconds;

        var result = TimeBlockScheduler.CalculateEffectiveBudget(budget, new List<TimeBlock> { block });

        result.ShouldBe(0);
    }

    #endregion

    #region PlaceUnmovableBlocks

    [Test]
    public void PlaceUnmovableBlocks_NormalBlock_CorrectStartAndEnd()
    {
        var block = CreateUnmovableBlock("12:00", "13:00");
        var dm = CreateSimpleDistanceMatrix(3);

        var placed = TimeBlockScheduler.PlaceUnmovableBlocks(
            new List<TimeBlock> { block },
            new List<int> { 1, 2 },
            dm, 0,
            TimeOnly.Parse("08:00").ToTimeSpan().TotalSeconds);

        placed.Count().ShouldBe(1);
        placed[0].StartTimeSeconds.ShouldBe(TimeOnly.Parse("12:00").ToTimeSpan().TotalSeconds);
        placed[0].EndTimeSeconds.ShouldBe(TimeOnly.Parse("13:00").ToTimeSpan().TotalSeconds);
    }

    [Test]
    public void PlaceUnmovableBlocks_MidnightBlock_EndIsGreaterThanStart()
    {
        var block = CreateUnmovableBlock("23:00", "01:00");
        var dm = CreateSimpleDistanceMatrix(3);

        var placed = TimeBlockScheduler.PlaceUnmovableBlocks(
            new List<TimeBlock> { block },
            new List<int> { 1, 2 },
            dm, 0,
            TimeOnly.Parse("22:00").ToTimeSpan().TotalSeconds);

        placed.Count().ShouldBe(1);
        placed[0].StartTimeSeconds.ShouldBe(TimeOnly.Parse("23:00").ToTimeSpan().TotalSeconds);
        placed[0].EndTimeSeconds.ShouldBe(TimeOnly.Parse("01:00").ToTimeSpan().TotalSeconds + SecondsPerDay);
        placed[0].EndTimeSeconds.ShouldBeGreaterThan(placed[0].StartTimeSeconds);
    }

    [Test]
    public void PlaceUnmovableBlocks_MidnightBlock_DurationIsCorrect()
    {
        var block = CreateUnmovableBlock("23:30", "00:30");
        var dm = CreateSimpleDistanceMatrix(3);

        var placed = TimeBlockScheduler.PlaceUnmovableBlocks(
            new List<TimeBlock> { block },
            new List<int> { 1, 2 },
            dm, 0,
            TimeOnly.Parse("22:00").ToTimeSpan().TotalSeconds);

        var duration = placed[0].EndTimeSeconds - placed[0].StartTimeSeconds;
        duration.ShouldBe(TimeSpan.FromHours(1).TotalSeconds);
    }

    #endregion

    #region CalculateArrivalTimesWithBlocks

    [Test]
    public void CalculateArrivalTimesWithBlocks_NormalBlock_SkipsOverBlock()
    {
        var block = CreateUnmovableBlock("12:00", "13:00");
        var dm = CreateSimpleDistanceMatrix(3, travelTimeSeconds: 300);
        var containerStart = TimeOnly.Parse("11:00").ToTimeSpan().TotalSeconds;

        var arrivals = TimeBlockScheduler.CalculateArrivalTimesWithBlocks(
            dm, new List<int> { 1, 2 }, 0, containerStart,
            new List<TimeBlock> { block });

        arrivals.Count().ShouldBe(2);
        arrivals[0].ShouldBe(containerStart + 300);
    }

    [Test]
    public void CalculateArrivalTimesWithBlocks_MidnightBlock_SkipsOverBlock()
    {
        var block = CreateUnmovableBlock("23:00", "01:00");
        var dm = CreateSimpleDistanceMatrix(3, travelTimeSeconds: 300);
        var containerStart = TimeOnly.Parse("22:00").ToTimeSpan().TotalSeconds;

        var arrivals = TimeBlockScheduler.CalculateArrivalTimesWithBlocks(
            dm, new List<int> { 1, 2 }, 0, containerStart,
            new List<TimeBlock> { block });

        arrivals.Count().ShouldBe(2);

        var firstArrival = arrivals[0];
        firstArrival.ShouldBe(containerStart + 300);

        var afterFirstStop = firstArrival + dm.Locations[1].TotalOnSiteTime.TotalSeconds;
        var midnightBlockEnd = TimeOnly.Parse("01:00").ToTimeSpan().TotalSeconds + SecondsPerDay;
        if (afterFirstStop >= TimeOnly.Parse("23:00").ToTimeSpan().TotalSeconds)
        {
            arrivals[1].ShouldBeGreaterThanOrEqualTo(midnightBlockEnd + 300);
        }
    }

    [Test]
    public void CalculateArrivalTimesWithBlocks_NoBlocks_NormalCalculation()
    {
        var dm = CreateSimpleDistanceMatrix(3, travelTimeSeconds: 600);
        var containerStart = TimeOnly.Parse("08:00").ToTimeSpan().TotalSeconds;

        var arrivals = TimeBlockScheduler.CalculateArrivalTimesWithBlocks(
            dm, new List<int> { 1, 2 }, 0, containerStart,
            new List<TimeBlock>());

        arrivals.Count().ShouldBe(2);
        arrivals[0].ShouldBe(containerStart + 600);
        arrivals[1].ShouldBe(containerStart + 600 + dm.Locations[1].TotalOnSiteTime.TotalSeconds + 600);
    }

    #endregion
}
