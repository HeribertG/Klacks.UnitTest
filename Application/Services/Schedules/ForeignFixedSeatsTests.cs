// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// One held work takes exactly one seat of its (shift, date), never more than the slot has; with nothing held the input is
/// returned untouched. Warm-start assignments survive only on slots the context still offers.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ForeignFixedSeatsTests
{
    private static readonly Guid Early = Guid.NewGuid();
    private static readonly Guid Late = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 3, 10);

    [Test]
    public void Remove_NothingHeld_ReturnsTheSameList()
    {
        var shifts = new List<CoreShift> { Seat(Early, "2026-03-10") };

        ForeignFixedSeats.Remove(shifts, []).ShouldBeSameAs(shifts);
    }

    [Test]
    public void Remove_HeldSeatOnAnotherSlot_ReturnsTheSameList()
    {
        var shifts = new List<CoreShift> { Seat(Early, "2026-03-10") };

        ForeignFixedSeats.Remove(shifts, [(Late, Day), (Early, Day.AddDays(1))]).ShouldBeSameAs(shifts);
    }

    [Test]
    public void Remove_OneHeldOfTwoSeats_LeavesOne()
    {
        var shifts = new List<CoreShift> { Seat(Early, "2026-03-10"), Seat(Early, "2026-03-10"), Seat(Late, "2026-03-10") };

        var result = ForeignFixedSeats.Remove(shifts, [(Early, Day)]);

        result.Count(s => s.Id == Early.ToString()).ShouldBe(1);
        result.Count(s => s.Id == Late.ToString()).ShouldBe(1);
    }

    [Test]
    public void Remove_MoreHeldThanSeats_ClampsAtZero()
    {
        var shifts = new List<CoreShift> { Seat(Early, "2026-03-10"), Seat(Late, "2026-03-10") };

        var result = ForeignFixedSeats.Remove(shifts, [(Early, Day), (Early, Day), (Early, Day)]);

        result.ShouldHaveSingleItem().Id.ShouldBe(Late.ToString());
    }

    [Test]
    public void Remove_UnparseableSeat_IsKept()
    {
        var broken = new CoreShift("not-a-guid", "x", "2026-03-10", "06:00", "14:00", 8, 1, 0);
        var shifts = new List<CoreShift> { broken, Seat(Early, "2026-03-10") };

        ForeignFixedSeats.Remove(shifts, [(Early, Day)]).ShouldBe([broken]);
    }

    [Test]
    public void KeepWarmStartsOnOfferedSlots_KeepsOnlyAssignmentsOnOfferedSlots()
    {
        var offered = Assignment(Early, Day);
        var sameShiftDayNotOffered = Assignment(Early, Day.AddDays(1));
        var shiftNotOffered = Assignment(Late, Day);

        var result = OfferedSlots.KeepWarmStartsOnOfferedSlots(
            [offered, sameShiftDayNotOffered, shiftNotOffered], [Seat(Early, "2026-03-10")]);

        result.ShouldBe([offered]);
    }

    [Test]
    public void KeepWarmStartsOnOfferedSlots_PartlyHeldSlotStillOffered_KeepsEveryAssignment()
    {
        var first = Assignment(Early, Day);
        var second = Assignment(Early, Day) with { AgentId = "other" };
        var shifts = ForeignFixedSeats.Remove([Seat(Early, "2026-03-10"), Seat(Early, "2026-03-10")], [(Early, Day)]);

        OfferedSlots.KeepWarmStartsOnOfferedSlots([first, second], shifts).ShouldBe([first, second]);
    }

    [Test]
    public void KeepWarmStartsOnOfferedSlots_NoAssignment_ReturnsTheSameList()
    {
        var assignments = new List<CoreWarmStartAssignment>();

        OfferedSlots.KeepWarmStartsOnOfferedSlots(assignments, [Seat(Early, "2026-03-10")]).ShouldBeSameAs(assignments);
    }

    private static CoreShift Seat(Guid shiftId, string date)
        => new(shiftId.ToString(), "Shift", date, "06:00", "14:00", 8, 1, 0);

    private static CoreWarmStartAssignment Assignment(Guid shiftId, DateOnly date)
        => new("agent", date, shiftId, date.ToDateTime(new TimeOnly(6, 0)), date.ToDateTime(new TimeOnly(14, 0)), 8m);
}
