// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// Placeholder rule of the swap stages: a same-day swap may never hand a work a real person legally holds to a stand-in
/// for a free day; a work that breaks a rule where it stands may go to the stand-in, and every other direction stays
/// allowed.
/// </summary>
[TestFixture]
public class DomainAwareReplaceValidatorStandInTests
{
    private const int RealRow = 0;
    private const int StandInRow = 1;
    private const int SecondRealRow = 2;
    private const int Day = 0;

    private static readonly DateOnly Date = new(2026, 12, 10);

    [Test]
    public void Diagnose_RealPersonsWorkForAStandInsFreeDay_Rejected()
    {
        var bitmap = Bitmap();
        bitmap.SetCell(RealRow, Day, Work(CellSymbol.Early));

        Validator().Diagnose(bitmap, new ReplaceMove(RealRow, StandInRow, Day)).ShouldNotBeNull();
        Validator().Diagnose(bitmap, new ReplaceMove(StandInRow, RealRow, Day)).ShouldNotBeNull();
    }

    [Test]
    public void Diagnose_RealPersonsWorkThatBreaksARuleForAStandInsFreeDay_Allowed()
    {
        var bitmap = Bitmap();
        bitmap.SetCell(RealRow, Day, Work(CellSymbol.Early));
        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>
        {
            [("real", Date)] = new DayAvailability(WorksOnDay: false, HasFreeCommand: false, HasBreakBlocker: false),
        };
        var validator = new DomainAwareReplaceValidator(availability);

        validator.DiagnoseCellInPlace(bitmap, RealRow, Day).ShouldNotBeNull();
        validator.Diagnose(bitmap, new ReplaceMove(RealRow, StandInRow, Day)).ShouldBeNull();
    }

    [Test]
    public void Diagnose_StandInsWorkForARealPersonsFreeDay_Allowed()
    {
        var bitmap = Bitmap();
        bitmap.SetCell(StandInRow, Day, Work(CellSymbol.Early));

        Validator().Diagnose(bitmap, new ReplaceMove(RealRow, StandInRow, Day)).ShouldBeNull();
    }

    [Test]
    public void Diagnose_WorkForWorkBetweenARealPersonAndAStandIn_Allowed()
    {
        var bitmap = Bitmap();
        bitmap.SetCell(RealRow, Day, Work(CellSymbol.Early));
        bitmap.SetCell(StandInRow, Day, Work(CellSymbol.Late));

        Validator().Diagnose(bitmap, new ReplaceMove(RealRow, StandInRow, Day)).ShouldBeNull();
    }

    [Test]
    public void Diagnose_WorkForAFreeDayBetweenTwoRealPeople_Allowed()
    {
        var bitmap = Bitmap();
        bitmap.SetCell(RealRow, Day, Work(CellSymbol.Early));

        Validator().Diagnose(bitmap, new ReplaceMove(RealRow, SecondRealRow, Day)).ShouldBeNull();
    }

    private static DomainAwareReplaceValidator Validator() => new(null);

    private static HarmonyBitmap Bitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("real", "Real", 100m, new HashSet<CellSymbol>()),
            new("stand-in", "Stand-in", 0m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("real-2", "Real 2", 100m, new HashSet<CellSymbol>()),
        };
        return BitmapBuilder.Build(new BitmapInput(agents, Date, Date, []));
    }

    private static Cell Work(CellSymbol symbol) => new(
        symbol,
        Guid.NewGuid(),
        [Guid.NewGuid()],
        false,
        Date.ToDateTime(new TimeOnly(8, 0)),
        Date.ToDateTime(new TimeOnly(16, 0)),
        8m);
}
