// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Wizard 3 holds the daily cap of Wizard 1 on both of its paths: the same-day swap delegates to the domain validator,
/// the cross-day swap judges its end state with the same in-place check. Agent A may hold at most 10 h a day, agent B is
/// unconstrained; B holds a 12-hour duty.
/// </summary>
[TestFixture]
public class PlanMutationValidatorDailyHoursTests
{
    private const decimal DailyCap = 10m;
    private const decimal ShortDutyHours = 8m;
    private const decimal LongDutyHours = 12m;

    private static readonly DateOnly Monday = new(2026, 1, 5);
    private static readonly DateOnly Tuesday = new(2026, 1, 6);

    [Test]
    public void SameDaySwap_HandingALongDutyToACappedAgent_IsRejected()
    {
        var swap = new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 0, Reason: "test");

        var rejection = Validator().Validate(BuildBitmap(), swap);

        rejection.ShouldNotBeNull();
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(Stage0RuleNames.MaxDailyHours);
    }

    [Test]
    public void CrossDaySwap_HandingALongDutyToACappedAgent_IsRejected()
    {
        var swap = new PlanCellSwap(RowA: 0, DayA: 1, RowB: 1, DayB: 0, Reason: "test");

        var rejection = Validator().Validate(BuildBitmap(), swap);

        rejection.ShouldNotBeNull();
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(Stage0RuleNames.MaxDailyHours);
    }

    [Test]
    public void CrossDaySwap_OfShortDuties_IsAdmitted()
    {
        var swap = new PlanCellSwap(RowA: 0, DayA: 0, RowB: 1, DayB: 1, Reason: "test");

        Validator().Validate(BuildBitmap(), swap).ShouldBeNull();
    }

    /// <summary>Row 0 (A, cap 10 h): Mon 8 h, Tue 8 h. Row 1 (B, no cap): Mon 12 h, Tue 8 h.</summary>
    private static HarmonyBitmap BuildBitmap()
    {
        var rows = new List<BitmapAgent>
        {
            new("A", "A", 0m, new HashSet<CellSymbol>(), MaxDailyHours: DailyCap),
            new("B", "B", 0m, new HashSet<CellSymbol>()),
        };
        var cells = new Cell[2, 2];
        cells[0, 0] = Work(ShortDutyHours);
        cells[0, 1] = Work(ShortDutyHours);
        cells[1, 0] = Work(LongDutyHours);
        cells[1, 1] = Work(ShortDutyHours);
        return new HarmonyBitmap(rows, [Monday, Tuesday], cells);
    }

    private static Cell Work(decimal hours) => new(CellSymbol.Early, Guid.NewGuid(), [], false, default, default, hours);

    private static PlanMutationValidator Validator() => new(new DomainAwareReplaceValidator(availability: null));
}
