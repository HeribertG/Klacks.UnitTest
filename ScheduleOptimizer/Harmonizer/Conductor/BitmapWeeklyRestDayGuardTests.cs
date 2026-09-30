// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// The bitmap wizards (Wizard 2 same-day replace, Wizard 4, Holistic Harmonizer same-day and cross-day
/// swaps) apply the same weekly rest-day rule as the schedule check and Wizard 1. Row 1 is the receiving
/// agent with MinRestDays 2; row 0 is an unconstrained partner. 2026-06-01 is a Monday.
/// </summary>
[TestFixture]
public sealed class BitmapWeeklyRestDayGuardTests
{
    private const int RestDays = 2;

    private const string RestDayReason = "MinRestDays";

    private static readonly DateOnly Monday = new(2026, 6, 1);

    [Test]
    public void SameDayReplace_RejectsTheSixthWorkDayOfACalendarWeek()
    {
        var bitmap = Bitmap(Monday, 14);
        EarlyDays(bitmap, 1, 0, 1, 2, 3, 4);
        EarlyDays(bitmap, 0, 5);

        var diagnosis = new DomainAwareReplaceValidator(null).Diagnose(bitmap, new ReplaceMove(0, 1, 5));

        diagnosis.ShouldNotBeNull();
        diagnosis.ShouldContain(RestDayReason);
    }

    [Test]
    public void SameDayReplace_AcceptsFiveNightsBecauseTheSaturdaySpilloverIsRest()
    {
        var bitmap = Bitmap(Monday, 14);
        NightDays(bitmap, 1, 0, 1, 2, 3);
        NightDays(bitmap, 0, 4);

        new DomainAwareReplaceValidator(null).Diagnose(bitmap, new ReplaceMove(0, 1, 4)).ShouldBeNull();
    }

    [Test]
    public void SameDayReplace_CountsTheBoundaryWorksBeforeTheBitmap()
    {
        var wednesday = Monday.AddDays(2);
        var bitmap = Bitmap(wednesday, 14);
        EarlyDays(bitmap, 1, 0, 1, 2);
        EarlyDays(bitmap, 0, 3);
        var boundary = new List<BitmapAssignment> { BoundaryEarly(Monday), BoundaryEarly(Monday.AddDays(1)) };

        new DomainAwareReplaceValidator(null).Diagnose(bitmap, new ReplaceMove(0, 1, 3)).ShouldBeNull();
        new DomainAwareReplaceValidator(null, boundary).Diagnose(bitmap, new ReplaceMove(0, 1, 3))
            .ShouldNotBeNull().ShouldContain(RestDayReason);
    }

    /// <summary>
    /// Row 1 works early Tuesday to Friday and early on Sunday. A cross-day swap hands it a night on Friday:
    /// its Saturday spillover is followed by the Sunday shift, so Saturday becomes a work day and the week
    /// keeps only Monday free.
    /// </summary>
    [Test]
    public void CrossDaySwap_IsJudgedWithTheCellReanchoredOnItsTargetDay()
    {
        var bitmap = Bitmap(Monday, 14);
        EarlyDays(bitmap, 1, 1, 2, 3, 4, 6);
        NightDays(bitmap, 0, 9);
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(1, 4, 0, 9, "test"));

        rejection.ShouldNotBeNull();
        rejection.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
        rejection.Detail.ShouldContain(RestDayReason);
    }

    /// <summary>
    /// A cross-day swap of two cells of the SAME row is judged on its combined effect: row 1 moves its
    /// Friday early shift to the next Wednesday and the next Wednesday night onto Friday.
    /// </summary>
    [Test]
    public void CrossDaySwapWithinOneRow_IsJudgedOnTheCombinedEffect()
    {
        var bitmap = Bitmap(Monday, 14);
        EarlyDays(bitmap, 1, 1, 2, 3, 4, 6);
        NightDays(bitmap, 1, 9);
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(1, 4, 1, 9, "test"));

        rejection.ShouldNotBeNull();
        rejection.Detail.ShouldContain(RestDayReason);
    }

    private static HarmonyBitmap Bitmap(DateOnly start, int days)
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "agent-0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "agent-1", 100m, new HashSet<CellSymbol>(), MinRestDays: RestDays),
        };
        return BitmapBuilder.Build(new BitmapInput(agents, start, start.AddDays(days - 1), []));
    }

    private static void EarlyDays(HarmonyBitmap bitmap, int row, params int[] days)
    {
        foreach (var day in days)
        {
            var date = bitmap.Days[day];
            bitmap.SetCell(row, day, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
                date.ToDateTime(new TimeOnly(6, 0)), date.ToDateTime(new TimeOnly(14, 0)), 8m));
        }
    }

    private static void NightDays(HarmonyBitmap bitmap, int row, params int[] days)
    {
        foreach (var day in days)
        {
            var date = bitmap.Days[day];
            bitmap.SetCell(row, day, new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], false,
                date.ToDateTime(new TimeOnly(22, 0)), date.AddDays(1).ToDateTime(new TimeOnly(6, 0)), 8m));
        }
    }

    private static BitmapAssignment BoundaryEarly(DateOnly date) => new(
        "agent-1", date, CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], true,
        date.ToDateTime(new TimeOnly(6, 0)), date.ToDateTime(new TimeOnly(14, 0)), 8m);
}
