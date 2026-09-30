// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Domain.Models.Schedules;

/// <summary>
/// With DstAware on, TimelineCalculationService stores UTC blocks but keeps the company-local wall-clock
/// times in LocalStart/LocalEnd; the rest-day count buckets days on that local calendar. In a zone east of
/// UTC an early shift after local midnight lies on the PREVIOUS day in UTC, so bucketing on UTC would move
/// the work day. The cases sit on the Pacific/Auckland daylight-saving switches of 2026 (5 April end,
/// 27 September start).
/// </summary>
[TestFixture]
public sealed class ScheduleBlockLocalCalendarTests
{
    private const string EastOfUtcZone = "Pacific/Auckland";

    private const decimal MinimumRestDays = 2m;

    [TestCase(2026, 4, 5)]
    [TestCase(2026, 9, 27)]
    public void DstAwareBlock_IsBucketedOnTheLocalCalendarDay(int year, int month, int day)
    {
        var switchDay = new DateOnly(year, month, day);
        var calculator = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions { DstAware = true, TimeZoneId = EastOfUtcZone }),
            NullLogger<TimelineCalculationService>.Instance);
        var clientId = Guid.NewGuid();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = switchDay,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
            ShiftId = Guid.NewGuid(),
        };

        var block = calculator.CalculateScheduleBlocks([work], [], []).ShouldHaveSingleItem();
        var timeline = new ClientTimeline(clientId);
        timeline.AddBlock(block);

        DateOnly.FromDateTime(block.Start).ShouldBe(switchDay.AddDays(-1));
        block.CalendarStart.ShouldBe(switchDay.ToDateTime(new TimeOnly(6, 0)));
        block.CalendarEnd.ShouldBe(switchDay.ToDateTime(new TimeOnly(14, 0)));
        timeline.IsWorkDay(switchDay, MinimumRestDays).ShouldBeTrue();
        timeline.IsWorkDay(switchDay.AddDays(-1), MinimumRestDays).ShouldBeFalse();
    }
}
