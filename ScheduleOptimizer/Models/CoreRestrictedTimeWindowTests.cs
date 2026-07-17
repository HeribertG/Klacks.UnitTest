// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Drift guard for the duplicated K16 season/daily-window arithmetic. The SAME vector table is driven
/// through both CoreRestrictedTimeWindow.WouldBlock (optimizer side) and the internal
/// RestrictedTimeWindowEvaluator.WindowBlocks (API side) and asserted to agree with each other and with
/// the expected result - so the two hand-maintained copies cannot silently diverge. Also covers the
/// shift-id membership gate of the instance Blocks method.
/// </summary>

using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreRestrictedTimeWindowTests
{
    private const int Midday1230 = (12 * 60) + 30;
    private const int Midday1500 = 15 * 60;
    private const int Noon1200 = 12 * 60;
    private const int Afternoon1400 = 14 * 60;
    private const int Dawn0500 = 5 * 60;
    private const int Morning0700 = 7 * 60;

    private static DateTime D(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0);

    private sealed record Vector(
        string Name,
        int FromMonth, int FromDay, int ToMonth, int ToDay,
        int DailyStartMinutes, int DailyEndMinutes,
        DateTime SlotStart, DateTime SlotEnd,
        bool Expected);

    private static readonly Vector[] Vectors =
    [
        // Midday ban 06-15 .. 09-15, 12:30-15:00
        new("summer in-season overlap", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 7, 1, 12, 0), D(2026, 7, 1, 16, 0), true),
        new("summer out-of-season", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 10, 1, 12, 0), D(2026, 10, 1, 16, 0), false),
        new("summer start day inclusive", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 6, 15, 12, 0), D(2026, 6, 15, 16, 0), true),
        new("summer end day inclusive", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 9, 15, 12, 0), D(2026, 9, 15, 16, 0), true),
        new("summer day before season", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 6, 14, 12, 0), D(2026, 6, 14, 16, 0), false),
        new("summer day after season", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 9, 16, 12, 0), D(2026, 9, 16, 16, 0), false),
        new("ends exactly at window start", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 7, 1, 8, 0), D(2026, 7, 1, 12, 30), false),
        new("starts exactly at window end", 6, 15, 9, 15, Midday1230, Midday1500, D(2026, 7, 1, 15, 0), D(2026, 7, 1, 18, 0), false),

        // Wrap season 11-15 .. 02-15, 12:00-14:00
        new("wrap december", 11, 15, 2, 15, Noon1200, Afternoon1400, D(2026, 12, 20, 12, 0), D(2026, 12, 20, 16, 0), true),
        new("wrap january", 11, 15, 2, 15, Noon1200, Afternoon1400, D(2027, 1, 10, 12, 0), D(2027, 1, 10, 16, 0), true),
        new("wrap june outside", 11, 15, 2, 15, Noon1200, Afternoon1400, D(2026, 6, 20, 12, 0), D(2026, 6, 20, 16, 0), false),
        new("wrap start inclusive", 11, 15, 2, 15, Noon1200, Afternoon1400, D(2026, 11, 15, 12, 0), D(2026, 11, 15, 13, 0), true),
        new("wrap end inclusive", 11, 15, 2, 15, Noon1200, Afternoon1400, D(2027, 2, 15, 12, 30), D(2027, 2, 15, 13, 0), true),
        new("wrap day after end", 11, 15, 2, 15, Noon1200, Afternoon1400, D(2027, 2, 16, 12, 30), D(2027, 2, 16, 13, 0), false),

        // Early-morning window 05:00-07:00 all year, cross-midnight spillover
        new("cross-midnight into next-day window", 1, 1, 12, 31, Dawn0500, Morning0700, D(2026, 7, 1, 22, 0), D(2026, 7, 2, 7, 0), true),
        new("day shift misses early window", 1, 1, 12, 31, Dawn0500, Morning0700, D(2026, 7, 1, 8, 0), D(2026, 7, 1, 16, 0), false),
    ];

    [Test]
    public void WouldBlock_And_WindowBlocks_AgreeWithEachOtherAndExpected()
    {
        foreach (var v in Vectors)
        {
            var core = CoreRestrictedTimeWindow.WouldBlock(
                v.FromMonth, v.FromDay, v.ToMonth, v.ToDay, v.DailyStartMinutes, v.DailyEndMinutes, v.SlotStart, v.SlotEnd);
            var api = RestrictedTimeWindowEvaluator.WindowBlocks(
                v.FromMonth, v.FromDay, v.ToMonth, v.ToDay, v.DailyStartMinutes, v.DailyEndMinutes, v.SlotStart, v.SlotEnd);

            core.ShouldBe(v.Expected, $"CoreRestrictedTimeWindow.WouldBlock disagrees for '{v.Name}'");
            api.ShouldBe(v.Expected, $"RestrictedTimeWindowEvaluator.WindowBlocks disagrees for '{v.Name}'");
            core.ShouldBe(api, $"optimizer and API copies drifted for '{v.Name}'");
        }
    }

    [Test]
    public void Blocks_OnlyWhenShiftIdIsInRestrictedSet()
    {
        var restricted = Guid.NewGuid();
        var other = Guid.NewGuid();
        var window = new CoreRestrictedTimeWindow(6, 15, 9, 15, Midday1230, Midday1500, new HashSet<Guid> { restricted });
        var slotStart = D(2026, 7, 1, 12, 0);
        var slotEnd = D(2026, 7, 1, 16, 0);

        window.Blocks(slotStart, slotEnd, restricted).ShouldBeTrue();
        window.Blocks(slotStart, slotEnd, other).ShouldBeFalse();
    }
}
