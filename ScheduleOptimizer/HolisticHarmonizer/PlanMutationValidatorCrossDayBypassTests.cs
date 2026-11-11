// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Grill 2026-06-19 defect H3, fixed 2026-11-10: PlanMutationValidator delegates same-day swaps to the full
/// DomainAwareReplaceValidator hard gate (MinPause/MaxConsecutiveDays/MaxWeeklyHours/keyword). Its cross-day branch used
/// to check only coverage-neutrality, qualification, day restrictions, rest days and planning rules, so a cross-day swap
/// pushing an agent over MaxWeeklyHours, under the minimum pause or over MaxConsecutiveDays was admitted. The branch now
/// judges the end state of the swap (applied to a copy, cells re-anchored on their target day) with the same per-row
/// rules, so both paths reject the same violations. The weekly-hours cells use the SAME ISO week (2026-01-05/06).
/// </summary>
[TestFixture]
public class PlanMutationValidatorCrossDayBypassTests
{
    private static readonly DateOnly Monday = new(2026, 1, 5);   // ISO week 2
    private static readonly DateOnly Tuesday = new(2026, 1, 6);  // ISO week 2

    private const string WeeklyHoursRule = "MaxWeeklyHours";
    private const string MinPauseRule = "MinPauseHours";
    private const string ConsecutiveDaysRule = "MaxConsecutiveDays";

    private static BitmapAgent Agent(string id, decimal maxWeekly) =>
        new(id, id, TargetHours: 0m, PreferredShiftSymbols: new HashSet<CellSymbol>(), MaxWeeklyHours: maxWeekly);

    private static Cell Work(decimal hours) =>
        new(CellSymbol.Early, Guid.NewGuid(), [], false, default, default, hours);

    /// <summary>
    /// Row 0 (agent A, MaxWeeklyHours=16): Mon 4h + Tue 9h = 13h.
    /// Row 1 (agent B, unconstrained): Mon 12h + Tue 9h.
    /// </summary>
    private static HarmonyBitmap BuildBitmap()
    {
        var rows = new List<BitmapAgent> { Agent("A", 16m), Agent("B", 100m) };
        var days = new List<DateOnly> { Monday, Tuesday };
        var cells = new Cell[2, 2];
        cells[0, 0] = Work(4m);   // A Mon
        cells[0, 1] = Work(9m);   // A Tue
        cells[1, 0] = Work(12m);  // B Mon
        cells[1, 1] = Work(9m);   // B Tue
        return new HarmonyBitmap(rows, days, cells);
    }

    private static PlanMutationValidator Validator() =>
        new(new DomainAwareReplaceValidator(availability: null));

    [Test]
    public void SameDaySwap_PushingAgentOverWeeklyCap_IsCorrectlyRejected()
    {
        // Same-day swap on Monday: A receives B's 12h cell -> A week = 12h + 9h(Tue) = 21h > 16h cap.
        var swap = new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 0, Reason: "test");

        var rejection = Validator().Validate(BuildBitmap(), swap);

        rejection.ShouldNotBeNull("the same-day path delegates to DomainAwareReplaceValidator, which must " +
            "reject a swap pushing agent A to 21h over the 16h weekly cap");
        rejection!.Detail.ShouldContain(WeeklyHoursRule);
    }

    [Test]
    public void CrossDaySwap_PushingAgentOverWeeklyCap_IsRejected()
    {
        // Cross-day work<->work swap: (A, Mon 4h) <-> (B, Tue 9h). Coverage-neutral (both work).
        // After the swap A would hold Mon 9h + Tue 9h = 18h > 16h cap — the SAME violation the same-day
        // case above rejects.
        var swap = new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test");

        var rejection = Validator().Validate(BuildBitmap(), swap);

        rejection.ShouldNotBeNull("a cross-day swap pushing agent A to 18h over the 16h weekly cap breaks a hard rule");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(WeeklyHoursRule);
    }

    /// <summary>
    /// A works late on Monday and Tuesday (14:00-22:00), B early on Thursday (06:00-14:00). The cross-day swap hands A
    /// B's early on Tuesday: re-anchored on Tuesday it starts at 06:00, 8 h after A's Monday late - below the 11 h minimum.
    /// Without re-anchoring the moved cell would still start on Thursday and the pause would look like 56 h.
    /// </summary>
    [Test]
    public void CrossDay_MinPauseViolation_IsRejected()
    {
        var days = new List<DateOnly> { Monday, Monday.AddDays(1), Monday.AddDays(2), Monday.AddDays(3) };
        var cells = new Cell[2, 4];
        cells[0, 0] = Timed(CellSymbol.Late, days[0], new TimeOnly(14, 0), new TimeOnly(22, 0));
        cells[0, 1] = Timed(CellSymbol.Late, days[1], new TimeOnly(14, 0), new TimeOnly(22, 0));
        cells[0, 2] = Cell.Free();
        cells[0, 3] = Cell.Free();
        cells[1, 0] = Cell.Free();
        cells[1, 1] = Cell.Free();
        cells[1, 2] = Cell.Free();
        cells[1, 3] = Timed(CellSymbol.Early, days[3], new TimeOnly(6, 0), new TimeOnly(14, 0));
        var agentA = Agent("A", 0m) with { MinPauseHours = 11m };
        var bitmap = new HarmonyBitmap([agentA, Agent("B", 0m)], days, cells);

        var rejection = Validator().Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 1, RowB: 1, DayB: 3, Reason: "test"));

        rejection.ShouldNotBeNull("A would start at 06:00 on Tuesday, only 8 h after Monday's late ends at 22:00");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(MinPauseRule);
    }

    /// <summary>
    /// A works early on day 0 and a block of three earlies on days 2-4 (cap 3). The cross-day swap hands A a night on day
    /// 0: it spills into the morning of day 1 and bridges the free day, so the run becomes five days.
    /// </summary>
    [Test]
    public void CrossDay_NightBeforeBlock_ExceedsMaxConsecutive_IsRejected()
    {
        var days = Enumerable.Range(0, 7).Select(offset => Monday.AddDays(offset)).ToList();
        var cells = new Cell[2, 7];
        for (var day = 0; day < days.Count; day++)
        {
            cells[0, day] = Cell.Free();
            cells[1, day] = Cell.Free();
        }

        foreach (var day in new[] { 0, 2, 3, 4 })
        {
            cells[0, day] = Timed(CellSymbol.Early, days[day], new TimeOnly(6, 0), new TimeOnly(14, 0));
        }

        cells[1, 5] = Timed(CellSymbol.Night, days[5], new TimeOnly(22, 0), new TimeOnly(6, 0));
        var agentA = Agent("A", 0m) with { MaxConsecutiveDays = 3 };
        var bitmap = new HarmonyBitmap([agentA, Agent("B", 0m)], days, cells);

        var rejection = Validator().Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 5, Reason: "test"));

        rejection.ShouldNotBeNull("the night on day 0 occupies day 1 and joins the block of days 2-4: five days, cap three");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(ConsecutiveDaysRule);
    }

    /// <summary>Work cell on <paramref name="day"/>; an end at or before the start lies on the following day.</summary>
    private static Cell Timed(CellSymbol symbol, DateOnly day, TimeOnly start, TimeOnly end)
    {
        var startAt = day.ToDateTime(start);
        var endAt = end <= start ? day.AddDays(1).ToDateTime(end) : day.ToDateTime(end);
        return new Cell(symbol, Guid.NewGuid(), [Guid.NewGuid()], false, startAt, endAt, 8m);
    }

    private static Cell Shift(CellSymbol symbol, Guid? shiftId = null) =>
        new(symbol, shiftId ?? Guid.NewGuid(), [], false, default, default, 8m);

    private static HarmonyBitmap TwoByTwo(BitmapAgent agentA, Cell aMon, Cell aTue, Cell bMon, Cell bTue)
    {
        var cells = new Cell[2, 2];
        cells[0, 0] = aMon;
        cells[0, 1] = aTue;
        cells[1, 0] = bMon;
        cells[1, 1] = bTue;
        return new HarmonyBitmap([agentA, Agent("B", 100m)], [Monday, Tuesday], cells);
    }

    private static PlanMutationValidator ValidatorWith(string agentId, DateOnly date, DayAvailability availability) =>
        new(new DomainAwareReplaceValidator(new Dictionary<(string, DateOnly), DayAvailability>
        {
            [(agentId, date)] = availability,
        }));

    [Test]
    public void CrossDaySwap_BringingALateShiftOntoAnOnlyEarlyDay_IsRejected()
    {
        var bitmap = TwoByTwo(Agent("A", 100m), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Late));
        var validator = ValidatorWith("A", Monday, new DayAvailability(true, false, false, RequiredSymbol: CellSymbol.Early));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test"));

        rejection.ShouldNotBeNull("agent A may only work early on Monday; a cross-day swap must not hand A the late shift");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void CrossDaySwap_BringingANightShiftOntoANoNightDay_IsRejected()
    {
        var bitmap = TwoByTwo(Agent("A", 100m), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Night));
        var validator = ValidatorWith("A", Monday, new DayAvailability(true, false, false, ForbiddenSymbol: CellSymbol.Night));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test"));

        rejection.ShouldNotBeNull("agent A must not work nights on Monday; a cross-day swap must not hand A the night shift");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void CrossDaySwap_BringingAFreeDayCommandIntoWork_IsRejected()
    {
        var bitmap = TwoByTwo(Agent("A", 100m), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Late));
        var validator = ValidatorWith("A", Monday, new DayAvailability(true, true, false));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test"));

        rejection.ShouldNotBeNull("a FREE day must not receive work through a cross-day swap");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void CrossDaySwap_BringingABlacklistedShift_IsRejected()
    {
        var blacklisted = Guid.NewGuid();
        var agentA = Agent("A", 100m) with { BlacklistedShiftIds = new HashSet<Guid> { blacklisted } };
        var bitmap = TwoByTwo(agentA, Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Late, blacklisted));
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(availability: null));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test"));

        rejection.ShouldNotBeNull("agent A has blacklisted the shift; a cross-day swap must not hand it to A");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void CrossDaySwap_WithinOneRow_OntoAnOnlyEarlyDay_IsRejected()
    {
        var bitmap = TwoByTwo(Agent("A", 100m), Shift(CellSymbol.Early), Shift(CellSymbol.Late), Shift(CellSymbol.Early), Shift(CellSymbol.Early));
        var validator = ValidatorWith("A", Monday, new DayAvailability(true, false, false, RequiredSymbol: CellSymbol.Early));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 0, DayB: 1, Reason: "test"));

        rejection.ShouldNotBeNull("moving A's own late shift onto A's OnlyEarly Monday violates the directive too");
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void CrossDaySwap_RespectingTheDirective_IsAccepted()
    {
        var bitmap = TwoByTwo(Agent("A", 100m), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Early), Shift(CellSymbol.Early));
        var validator = ValidatorWith("A", Monday, new DayAvailability(true, false, false, RequiredSymbol: CellSymbol.Early));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test"));

        rejection.ShouldBeNull("an early shift on an OnlyEarly day is allowed");
    }
}
