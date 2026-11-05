// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// The Harmonizer family respects the shift-work flag (owner decision 2026-11-05): an agent without shift work never
/// RECEIVES a late or night duty by a swap. Only the receiving side is checked, so a late duty an agent without shift
/// work already holds may be handed to a shift worker.
/// </summary>
[TestFixture]
public class DomainAwareReplaceValidatorShiftWorkTests
{
    private const string ShiftWorkReasonFragment = "does not perform shift work";
    private const int ShiftWorkerRow = 0;
    private const int EarlyOnlyRow = 1;
    private const int Day = 1;
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void EarlyOnlyAgent_ReceivingLate_Rejected()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: EarlyOnlyRow);
        bitmap.SetCell(ShiftWorkerRow, Day, Work(CellSymbol.Late, 15, 23));
        var validator = new DomainAwareReplaceValidator(null);

        var reason = validator.Diagnose(bitmap, new ReplaceMove(ShiftWorkerRow, EarlyOnlyRow, Day));

        reason.ShouldNotBeNull();
        reason.ShouldContain(ShiftWorkReasonFragment);
    }

    [Test]
    public void EarlyOnlyAgent_ReceivingNight_Rejected()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: EarlyOnlyRow);
        bitmap.SetCell(ShiftWorkerRow, Day, Work(CellSymbol.Night, 23, 7));
        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(ShiftWorkerRow, EarlyOnlyRow, Day)).ShouldBeFalse();
    }

    [Test]
    public void EarlyOnlyAgent_ReceivingEarly_Allowed()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: EarlyOnlyRow);
        bitmap.SetCell(ShiftWorkerRow, Day, Work(CellSymbol.Early, 7, 15));
        var validator = new DomainAwareReplaceValidator(null);

        validator.Diagnose(bitmap, new ReplaceMove(ShiftWorkerRow, EarlyOnlyRow, Day)).ShouldBeNull();
    }

    [Test]
    public void ShiftWorker_ReceivingLate_Allowed()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: null);
        bitmap.SetCell(ShiftWorkerRow, Day, Work(CellSymbol.Late, 15, 23));
        var validator = new DomainAwareReplaceValidator(null);

        validator.Diagnose(bitmap, new ReplaceMove(ShiftWorkerRow, EarlyOnlyRow, Day)).ShouldBeNull();
    }

    [Test]
    public void EarlyOnlyAgent_GivingAwayAnExistingLate_Allowed()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: EarlyOnlyRow);
        bitmap.SetCell(EarlyOnlyRow, Day, Work(CellSymbol.Late, 15, 23));
        var validator = new DomainAwareReplaceValidator(null);

        validator.Diagnose(bitmap, new ReplaceMove(ShiftWorkerRow, EarlyOnlyRow, Day)).ShouldBeNull();
    }

    [Test]
    public void DiagnoseDayRestrictions_EarlyOnlyAgentAndLateCell_Rejected()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: EarlyOnlyRow);
        var validator = new DomainAwareReplaceValidator(null);

        var reason = validator.DiagnoseDayRestrictions(bitmap.Rows[EarlyOnlyRow], Work(CellSymbol.Late, 15, 23), Day0.AddDays(Day), "rowB");

        reason.ShouldNotBeNull();
        reason.ShouldContain(ShiftWorkReasonFragment);
    }

    [Test]
    public void DiagnoseCellInPlace_LateHeldByEarlyOnlyAgent_Rejected()
    {
        var bitmap = BuildBitmap(earlyOnlyRow: EarlyOnlyRow);
        bitmap.SetCell(EarlyOnlyRow, Day, Work(CellSymbol.Late, 15, 23, locked: true));
        var validator = new DomainAwareReplaceValidator(null);

        var reason = validator.DiagnoseCellInPlace(bitmap, EarlyOnlyRow, Day);

        reason.ShouldNotBeNull();
        reason.ShouldContain(ShiftWorkReasonFragment);
    }

    private static Cell Work(CellSymbol symbol, int startHour, int endHour, bool locked = false)
    {
        var date = Day0.AddDays(Day);
        var start = date.ToDateTime(new TimeOnly(startHour, 0));
        var end = endHour > startHour ? date.ToDateTime(new TimeOnly(endHour, 0)) : date.AddDays(1).ToDateTime(new TimeOnly(endHour, 0));
        return new Cell(symbol, Guid.NewGuid(), [Guid.NewGuid()], locked, start, end, 8m);
    }

    private static HarmonyBitmap BuildBitmap(int? earlyOnlyRow)
    {
        const int rows = 2;
        const int days = 3;
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>(), PerformsShiftWork: r != earlyOnlyRow));
        }

        return BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []));
    }
}
