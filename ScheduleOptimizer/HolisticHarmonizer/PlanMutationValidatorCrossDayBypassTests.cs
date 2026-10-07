// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Grill 2026-06-19 defect H3: PlanMutationValidator delegates same-day swaps to the full
/// DomainAwareReplaceValidator hard gate (MinPause/MaxConsecutiveDays/MaxWeeklyHours/keyword), but its
/// cross-day branch checks only coverage-neutrality + qualification — it never calls Diagnose. So a
/// cross-day swap that pushes an agent over MaxWeeklyHours is admitted, while the equivalent same-day
/// swap is correctly rejected. This is a CHARACTERIZATION test: it asserts the current (defective)
/// behaviour so the bypass is pinned. When H3 is fixed (cross-day delegates to Diagnose), the cross-day
/// assertion must be flipped to expect a rejection. Both cells use the SAME ISO week (2026-01-05/06).
/// </summary>
[TestFixture]
public class PlanMutationValidatorCrossDayBypassTests
{
    private static readonly DateOnly Monday = new(2026, 1, 5);   // ISO week 2
    private static readonly DateOnly Tuesday = new(2026, 1, 6);  // ISO week 2

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
        rejection!.Detail.ShouldContain("MaxWeeklyHours");
    }

    [Test]
    public void CrossDaySwap_PushingAgentOverWeeklyCap_IsAdmitted_DocumentingTheBypass()
    {
        // Cross-day work<->work swap: (A, Mon 4h) <-> (B, Tue 9h). Coverage-neutral (both work).
        // After the swap A would hold Mon 9h + Tue 9h = 18h > 16h cap — the SAME violation the same-day
        // case above rejects. The cross-day branch never calls Diagnose, so it is admitted (returns null).
        var swap = new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test");

        var rejection = Validator().Validate(BuildBitmap(), swap);

        rejection.ShouldBeNull("DEFECT H3 (grill 2026-06-19): the cross-day branch admits a swap that " +
            "pushes agent A to 18h over the 16h weekly cap because it only checks coverage + qualification, " +
            "never the per-row hard constraints the same-day path enforces. Flip this assertion when H3 is fixed.");
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
