// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// K16 RestrictedTimeWindow hard veto on Wizard 3's cross-day swap branch. A cross-day swap relocates a
/// cell to a different calendar day, and the apply path re-anchors the persisted work to that new date
/// (time-of-day preserved), so the seasonal window classification can flip and a compliant slot can land
/// inside a forbidden window. The veto fires for BOTH relocated cells at their target day, is always hard,
/// and does not apply to same-day swaps (which never change a cell's day). Vectors mirror
/// Stage0RestrictedTimeWindowTests: block inside the window, outside season admitted, wrap season enforced,
/// non-restricted shift admitted.
/// </summary>

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

[TestFixture]
public class PlanMutationValidatorRestrictedTimeWindowTests
{
    private static readonly Guid RestrictedShift = Guid.NewGuid();
    private static readonly Guid OtherShift = Guid.NewGuid();

    private static readonly DateOnly InSeason = new(2026, 7, 1);      // inside Jun 15 .. Sep 15
    private static readonly DateOnly OutSeason = new(2026, 5, 1);     // outside Jun 15 .. Sep 15
    private static readonly DateOnly OutSeason2 = new(2026, 5, 2);
    private static readonly DateOnly WrapInSeason = new(2026, 1, 1);  // inside wrap Nov 15 .. Feb 15
    private static readonly DateOnly WrapOutSeason = new(2026, 7, 1); // outside wrap Nov 15 .. Feb 15
    private static readonly DateOnly IntermediateOutSeason = new(2026, 6, 14); // out of Jun 15 .. Sep 15 by one day
    private static readonly DateOnly TargetInSeason = new(2026, 6, 15);        // first in-season day, one day after the intermediate

    // Season Jun 15 .. Sep 15, daily forbidden window 12:30 .. 15:00.
    private static CoreRestrictedTimeWindow SummerWindow() =>
        new(6, 15, 9, 15, (12 * 60) + 30, 15 * 60, new HashSet<Guid> { RestrictedShift });

    // Year-boundary wrap season Nov 15 .. Feb 15, daily forbidden window 12:30 .. 15:00.
    private static CoreRestrictedTimeWindow WrapWindow() =>
        new(11, 15, 2, 15, (12 * 60) + 30, 15 * 60, new HashSet<Guid> { RestrictedShift });

    private static BitmapAgent Agent(string id) =>
        new(id, id, TargetHours: 0m, PreferredShiftSymbols: new HashSet<CellSymbol>());

    // A work cell whose 12:00 .. 16:00 span overlaps the daily forbidden window, anchored to its own day.
    private static Cell Work(Guid shiftId, CellSymbol symbol, DateOnly day) =>
        new(symbol, shiftId, [], false, day.ToDateTime(new TimeOnly(12, 0)), day.ToDateTime(new TimeOnly(16, 0)), 4m);

    private static PlanMutationValidator Validator(CoreRestrictedTimeWindow window) =>
        new(new DomainAwareReplaceValidator(availability: null), new[] { window });

    /// <summary>
    /// Cross-day swap of cell(0,0) &lt;-&gt; cell(1,1). cellA sits on day0, cellB on day1; after the swap cellA
    /// lands on day1 and cellB on day0.
    /// </summary>
    private static HarmonyBitmap CrossDayBitmap(Cell cellA, DateOnly day0, Cell cellB, DateOnly day1)
    {
        var rows = new List<BitmapAgent> { Agent("A"), Agent("B") };
        var days = new List<DateOnly> { day0, day1 };
        var cells = new Cell[2, 2];
        cells[0, 0] = cellA;
        cells[0, 1] = Cell.Free();
        cells[1, 0] = Cell.Free();
        cells[1, 1] = cellB;
        return new HarmonyBitmap(rows, days, cells);
    }

    private static readonly PlanCellSwap CrossDaySwap = new(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test");

    [Test]
    public void CrossDaySwap_RestrictedCellLandsInWindowAtTargetDay_IsRejected()
    {
        // cellA (restricted) moves from an out-of-season day to an in-season day -> lands in the window.
        var bitmap = CrossDayBitmap(
            Work(RestrictedShift, CellSymbol.Early, OutSeason), OutSeason,
            Work(OtherShift, CellSymbol.Late, InSeason), InSeason);

        var rejection = Validator(SummerWindow()).Validate(bitmap, CrossDaySwap);

        rejection.ShouldNotBeNull();
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain("RestrictedTimeWindow");
    }

    [Test]
    public void CrossDaySwap_RestrictedCellOnOtherSideLandsInWindow_IsRejected()
    {
        // Mirror of the above: this time cellB (restricted) is the relocated cell that lands in the window,
        // exercising the second of the two checked sides. cellB moves from out-of-season day1 to in-season day0.
        var bitmap = CrossDayBitmap(
            Work(OtherShift, CellSymbol.Late, InSeason), InSeason,
            Work(RestrictedShift, CellSymbol.Early, OutSeason), OutSeason);

        var rejection = Validator(SummerWindow()).Validate(bitmap, CrossDaySwap);

        rejection.ShouldNotBeNull();
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain("RestrictedTimeWindow");
    }

    [Test]
    public void CrossDaySwap_RestrictedCellStaysOutsideSeason_IsAdmitted()
    {
        // Both days are out of season, so relocating the restricted cell never lands it in the window.
        var bitmap = CrossDayBitmap(
            Work(RestrictedShift, CellSymbol.Early, OutSeason), OutSeason,
            Work(OtherShift, CellSymbol.Late, OutSeason2), OutSeason2);

        var rejection = Validator(SummerWindow()).Validate(bitmap, CrossDaySwap);

        rejection.ShouldBeNull();
    }

    [Test]
    public void CrossDaySwap_RestrictedCellLandsInWrapSeason_IsRejected()
    {
        // Year-boundary wrap season (Nov 15 .. Feb 15): the restricted cell moves onto 1 Jan -> in season.
        var bitmap = CrossDayBitmap(
            Work(RestrictedShift, CellSymbol.Early, WrapOutSeason), WrapOutSeason,
            Work(OtherShift, CellSymbol.Late, WrapInSeason), WrapInSeason);

        var rejection = Validator(WrapWindow()).Validate(bitmap, CrossDaySwap);

        rejection.ShouldNotBeNull();
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain("RestrictedTimeWindow");
    }

    [Test]
    public void CrossDaySwap_NonRestrictedShiftLandsInWindow_IsAdmitted()
    {
        // Both cells are on the untagged shift. cellA lands in-season inside the daily window by time, but the
        // shift is not in the rule's scope, so it is not vetoed.
        var bitmap = CrossDayBitmap(
            Work(OtherShift, CellSymbol.Early, OutSeason), OutSeason,
            Work(OtherShift, CellSymbol.Late, InSeason), InSeason);

        var rejection = Validator(SummerWindow()).Validate(bitmap, CrossDaySwap);

        rejection.ShouldBeNull();
    }

    [Test]
    public void Validate_SecondCrossDayMoveOfSameCell_EvaluatesAgainstActualTargetDay_Rejected()
    {
        // Two-step scenario proving the K16 veto re-anchors to the ACTUAL target day, not to a whole-day delta
        // off the cell's stale StartAt. The restricted cell is first moved cross-day from its out-of-season home
        // day (day0 = 1 May) onto an out-of-season intermediate day (day1 = 14 Jun) via Apply, which is a pure
        // position swap and never re-anchors StartAt. It is then moved a SECOND time onto day2 (15 Jun, in season,
        // overlapping the daily window). Because StartAt still points at day0, the old delta logic
        // (StartAt + (day2 - day1)) computed 2 May -> out of season -> wrongly admitted (test RED). The correct
        // logic anchors the slot on day2 itself -> in season -> rejected (test GREEN).
        var rows = new List<BitmapAgent> { Agent("A"), Agent("B") };
        var days = new List<DateOnly> { OutSeason, IntermediateOutSeason, TargetInSeason };
        var cells = new Cell[2, 3];
        cells[0, 0] = Work(RestrictedShift, CellSymbol.Early, OutSeason);       // restricted cell, anchored to day0
        cells[0, 1] = Cell.Free();
        cells[0, 2] = Work(OtherShift, CellSymbol.Night, TargetInSeason);       // step-2 partner on day2
        cells[1, 0] = Cell.Free();
        cells[1, 1] = Work(OtherShift, CellSymbol.Late, IntermediateOutSeason); // step-1 partner on day1
        cells[1, 2] = Cell.Free();
        var bitmap = new HarmonyBitmap(rows, days, cells);

        var validator = Validator(SummerWindow());

        // Step 1: move the restricted cell cross-day from (0,0) to (1,1). Both cells are work (coverage-neutral);
        // Apply is a raw position swap, so StartAt stays anchored to day0.
        PlanMutationValidator.Apply(bitmap, new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "step1"));
        bitmap.GetCell(1, 1).ShiftRefId.ShouldBe(RestrictedShift);

        // Step 2: move the SAME restricted cell cross-day from (1,1) to (0,2) -> it lands on day2 (in season).
        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 1, DayA: 1, RowB: 0, DayB: 2, Reason: "step2"));

        rejection.ShouldNotBeNull();
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain("RestrictedTimeWindow");
        rejection.Detail.ShouldContain("2026-06-15");
    }

    [Test]
    public void SameDaySwap_RestrictedCellsInWindow_IsNotCheckedForRestrictedWindow_AndIsAdmitted()
    {
        // Same-day swap on an in-season day with a restricted cell whose span overlaps the daily window. The
        // K16 veto is deliberately NOT applied to same-day swaps (a same-day swap never changes a cell's day),
        // so this must be admitted even though the equivalent cross-day relocation is vetoed. Non-vacuous:
        // if the veto were wrongly applied here, both restricted-in-window cells would trip it.
        var rows = new List<BitmapAgent> { Agent("A"), Agent("B") };
        var days = new List<DateOnly> { InSeason };
        var cells = new Cell[2, 1];
        cells[0, 0] = Work(RestrictedShift, CellSymbol.Early, InSeason);
        cells[1, 0] = Work(OtherShift, CellSymbol.Late, InSeason);
        var bitmap = new HarmonyBitmap(rows, days, cells);

        var swap = new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 0, Reason: "same-day");
        var rejection = Validator(SummerWindow()).Validate(bitmap, swap);

        rejection.ShouldBeNull();
    }
}
