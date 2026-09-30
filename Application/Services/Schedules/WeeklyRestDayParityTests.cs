// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// Shared-semantics fixture for the weekly rest days: every case runs through the schedule check
/// (Work rows -> TimelineCalculationService -> ClientTimeline -> ScheduleValidationBuilder.AddMinRestDays)
/// and through the Wizard-1 veto (<see cref="WeeklyRestDayGuard"/>, wall-clock tokens). The existing works
/// alone never violate; the wizard must refuse the slot exactly when adding it makes the check flag a
/// week. Each case runs with the wall-clock timeline (DstAware off) and with DstAware on in zones east and
/// west of UTC, in weeks that contain a daylight-saving switch, so the check's UTC blocks must be bucketed
/// on the company-local calendar. A spillover day counts as rest only with a free next day and a free block
/// of at least MinRestDays (rounded up) times 24 hours (owner rule 2026-09-30). Shifts are (day offset from
/// Monday, start hour, end hour); an end at or before the start ends on the following day.
/// </summary>
[TestFixture]
public sealed class WeeklyRestDayParityTests
{
    private const string AgentId = "A";

    private const string ClientName = "Parity";

    private const int CheckWeeksBefore = 1;

    private const int CheckDaysAfter = 20;

    private const double MinPauseHours = 11;

    private static readonly DateOnly PlainMonday = new(2026, 6, 1);

    private static readonly (string ZoneId, DateOnly Monday)[] Zones =
    [
        (string.Empty, PlainMonday),
        ("Pacific/Auckland", new DateOnly(2026, 3, 30)),
        ("Pacific/Auckland", new DateOnly(2026, 9, 21)),
        ("America/Los_Angeles", new DateOnly(2026, 3, 2)),
        ("America/Los_Angeles", new DateOnly(2026, 10, 26)),
    ];

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (var (zoneId, monday) in Zones)
        {
            foreach (var scenario in Scenarios())
            {
                var zoneLabel = zoneId.Length == 0 ? "WallClock" : zoneId.Replace('/', '_');
                yield return new TestCaseData(
                        zoneId, monday, scenario.MinRestDays, scenario.Existing, scenario.Slot, scenario.ExpectedVeto)
                    .SetName($"{scenario.Name}_{zoneLabel}_{monday:yyyyMMdd}");
            }
        }
    }

    private static IEnumerable<(string Name, decimal MinRestDays, (int Day, int Start, int End)[] Existing, (int Day, int Start, int End) Slot, bool ExpectedVeto)> Scenarios()
    {
        yield return ("SixDayBlockInsideOneWeek", 2m, Early(0, 1, 2, 3, 4), EarlyShift(5), true);
        yield return ("SixDayBlockAcrossTwoWeeks", 2m, Early(3, 4, 5, 6, 7), EarlyShift(8), false);
        yield return ("SpilloverWithFreeNextDayAndFullFreeBlockIsRest", 2m, [NightShift(0), .. Early(3, 4, 5)], EarlyShift(6), false);
        yield return ("SpilloverFollowedByAShiftTheNextDayIsWork", 2m, [NightShift(0), .. Early(2, 3, 4)], EarlyShift(5), true);
        yield return ("SpilloverWithTooShortFreeBlockIsWork", 2m, [NightShift(0), (3, 4, 12), .. Early(4, 5)], EarlyShift(6), true);
        yield return ("SpilloverDayWithAShiftStartingThatDayIsWork", 2m, [NightShift(0), (1, 16, 20), .. Early(2, 3, 4)], EarlyShift(5), true);
        yield return ("FiveNightsMondayToFridayWithUnknownNextShift", 2m, Nights(0, 1, 2, 3), NightShift(4), false);
        yield return ("FiveNightsThenFreeUntilMondayNight", 2m, [.. Nights(0, 1, 2, 3), NightShift(7)], NightShift(4), false);
        yield return ("SpilloverBeforeASundayEarlyShiftIsWork", 2m, [.. Nights(0, 1, 2), EarlyShift(6)], NightShift(4), true);
        yield return ("SundaySpilloverBeforeAMondayShiftIsWork", 2m, [.. Early(1, 2, 3, 4), NightShift(5)], EarlyShift(7), true);
        yield return ("SpilloverAtWeekBoundaryFollowedByTuesdayShiftIsWork", 2m, Early(8, 9, 10, 11, 12), NightShift(6), true);
        yield return ("SpilloverAtWeekBoundaryWithFreeTuesdayIsRest", 2m, Early(9, 10, 11, 12, 13), NightShift(6), false);
        yield return ("FractionalMinimumOneRestDay", 1.5m, Early(0, 1, 2, 3, 4), EarlyShift(5), true);
        yield return ("FractionalMinimumTwoRestDays", 1.5m, Early(0, 1, 2, 3), EarlyShift(4), false);
        yield return ("OneRestDayRequired", 1m, Early(0, 1, 2, 3, 4), EarlyShift(5), false);
    }

    [TestCaseSource(nameof(Cases))]
    public void WizardVetoesExactlyWhatTheScheduleCheckFlags(
        string zoneId,
        DateOnly monday,
        decimal minRestDays,
        (int Day, int Start, int End)[] existing,
        (int Day, int Start, int End) slot,
        bool expectedVeto)
    {
        var policy = Policy(minRestDays);
        CheckFlags(zoneId, monday, policy, existing).ShouldBeFalse();
        var flagged = CheckFlags(zoneId, monday, policy, [.. existing, slot]);

        var (slotStart, slotEnd) = Interval(monday, slot);
        var vetoed = WeeklyRestDayGuard.Violates(
            Agent(minRestDays),
            monday.AddDays(slot.Day),
            existing.Select(shift => Token(monday, shift)).ToList(),
            Context(monday),
            slotStart,
            slotEnd);

        flagged.ShouldBe(expectedVeto);
        vetoed.ShouldBe(flagged);
    }

    [Test]
    public void BreakOnlyDay_IsRestForTheCheckAndForTheWizard()
    {
        var saturday = PlainMonday.AddDays(5);
        var existing = Early(0, 1, 2, 3);
        var slot = EarlyShift(4);
        var timeline = Timeline(string.Empty, PlainMonday, [.. existing, slot]);
        timeline.AddBlock(new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Break, timeline.ClientId,
            saturday.ToDateTime(new TimeOnly(6, 0)), saturday.ToDateTime(new TimeOnly(14, 0))));
        timeline.SortBlocks();
        var entries = new List<ScheduleValidationNotificationDto>();

        ScheduleValidationBuilder.AddMinRestDays(
            entries, timeline, ClientName, CheckFrom(PlainMonday), CheckUntil(PlainMonday), Policy(2m));

        var context = new CoreWizardContext
        {
            PeriodFrom = PlainMonday,
            PeriodUntil = CheckUntil(PlainMonday),
            BreakBlockers = [new CoreBreakBlocker(AgentId, saturday, saturday, "Vacation")],
        };
        var (slotStart, slotEnd) = Interval(PlainMonday, slot);
        var vetoed = WeeklyRestDayGuard.Violates(
            Agent(2m), PlainMonday.AddDays(slot.Day),
            existing.Select(shift => Token(PlainMonday, shift)).ToList(), context, slotStart, slotEnd);

        entries.ShouldBeEmpty();
        vetoed.ShouldBeFalse();
    }

    private static bool CheckFlags(string zoneId, DateOnly monday, SchedulingPolicy policy, (int Day, int Start, int End)[] shifts)
    {
        var entries = new List<ScheduleValidationNotificationDto>();
        ScheduleValidationBuilder.AddMinRestDays(
            entries, Timeline(zoneId, monday, shifts), ClientName, CheckFrom(monday), CheckUntil(monday), policy);
        return entries.Count > 0;
    }

    private static ClientTimeline Timeline(string zoneId, DateOnly monday, (int Day, int Start, int End)[] shifts)
    {
        var clientId = Guid.NewGuid();
        var options = zoneId.Length == 0
            ? new ScheduleTimeOptions()
            : new ScheduleTimeOptions { DstAware = true, TimeZoneId = zoneId };
        var calculator = new TimelineCalculationService(
            Options.Create(options), NullLogger<TimelineCalculationService>.Instance);
        var works = shifts.Select(shift => new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = monday.AddDays(shift.Day),
            StartTime = new TimeOnly(shift.Start, 0),
            EndTime = new TimeOnly(shift.End, 0),
            ShiftId = Guid.NewGuid(),
        }).ToList();

        var timeline = new ClientTimeline(clientId);
        timeline.AddBlocks(calculator.CalculateScheduleBlocks(works, [], []));
        timeline.SortBlocks();
        return timeline;
    }

    private static DateOnly CheckFrom(DateOnly monday) => monday.AddDays(-7 * CheckWeeksBefore);

    private static DateOnly CheckUntil(DateOnly monday) => monday.AddDays(CheckDaysAfter);

    private static SchedulingPolicy Policy(decimal minRestDays) => new(
        MinRestHours: TimeSpan.FromHours(MinPauseHours),
        MaxDailyHours: TimeSpan.FromHours(10),
        MaxConsecutiveDays: 6,
        MaxWeeklyHours: TimeSpan.FromHours(50),
        MinRestDays: minRestDays);

    private static (int Day, int Start, int End)[] Early(params int[] days)
        => days.Select(EarlyShift).ToArray();

    private static (int Day, int Start, int End)[] Nights(params int[] days)
        => days.Select(NightShift).ToArray();

    private static (int Day, int Start, int End) EarlyShift(int day) => (day, 6, 14);

    private static (int Day, int Start, int End) NightShift(int day) => (day, 22, 6);

    private static (DateTime Start, DateTime End) Interval(DateOnly monday, (int Day, int Start, int End) shift)
    {
        var date = monday.AddDays(shift.Day);
        var start = date.ToDateTime(new TimeOnly(shift.Start, 0));
        var end = shift.End <= shift.Start
            ? date.AddDays(1).ToDateTime(new TimeOnly(shift.End, 0))
            : date.ToDateTime(new TimeOnly(shift.End, 0));
        return (start, end);
    }

    private static CoreToken Token(DateOnly monday, (int Day, int Start, int End) shift)
    {
        var (start, end) = Interval(monday, shift);
        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: 0,
            Date: monday.AddDays(shift.Day),
            TotalHours: 8,
            StartAt: start,
            EndAt: end,
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: Guid.Empty,
            AgentId: AgentId);
    }

    private static CoreAgent Agent(decimal minRestDays) => new(
        Id: AgentId,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: MinPauseHours,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        MinRestDays = (int)Math.Ceiling(minRestDays),
    };

    private static CoreWizardContext Context(DateOnly monday) => new()
    {
        PeriodFrom = monday,
        PeriodUntil = CheckUntil(monday),
    };
}
