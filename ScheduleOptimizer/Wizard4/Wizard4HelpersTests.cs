// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Objective;
using Klacks.ScheduleOptimizer.Wizard4;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Wizard4;

[TestFixture]
public class ScenarioScoreSerializerTests
{
    [Test]
    public void Serialize_EmitsVersionedEngineTaggedJson()
    {
        var result = new ObjectiveResult(
            new GateResult(MandatoryQualMissing: 0, Legality: 1, UnderSupply: 2, OverSupply: 0),
            Scalar: 0.75,
            new ObjectiveSubScores(Fehler: 0.9, Stundenabgleich: 0.8, Praeferenzen: 1.0),
            new ObjectiveDiagnostics(WorstStundenabgleich: 0.6, WorstPraeferenzen: 1.0, MaxBlacklistFraction: 0.0),
            ChurnRatio: 0.1);

        var json = ScenarioScoreSerializer.Serialize(result);

        json.ShouldContain("\"v\":1");
        json.ShouldContain("\"engine\":\"composite\"");
        json.ShouldContain("\"scalar\":0.75");
        json.ShouldContain("\"legality\":1");
        json.ShouldContain("\"underSupply\":2");
        json.ShouldContain("\"praeferenzen\":1");
        json.ShouldContain("\"churnRatio\":0.1");
    }
}

[TestFixture]
public class BitmapChurnTests
{
    private static readonly DateOnly D = new(2026, 4, 20);

    private static Cell Work(Guid shiftId) => new(CellSymbol.Early, shiftId, [], false,
        D.ToDateTime(new TimeOnly(8, 0)), D.ToDateTime(new TimeOnly(16, 0)), 8m);

    private static HarmonyBitmap Single(Cell cell)
    {
        var cells = new Cell[1, 1];
        cells[0, 0] = cell;
        return new HarmonyBitmap([new BitmapAgent("A", "A", 8m, new HashSet<CellSymbol>())], [D], cells);
    }

    [Test]
    public void Ratio_IsZero_ForIdenticalPlans()
    {
        var shift = Guid.NewGuid();
        BitmapChurn.Ratio(Single(Work(shift)), Single(Work(shift))).ShouldBe(0.0);
    }

    [Test]
    public void Ratio_IsOne_WhenTheOnlyWorkingCellIsRemoved()
    {
        var shift = Guid.NewGuid();
        BitmapChurn.Ratio(Single(Work(shift)), Single(Cell.Free())).ShouldBe(1.0);
    }

    [Test]
    public void Ratio_CountsAShiftSwapAsChurn()
    {
        BitmapChurn.Ratio(Single(Work(Guid.NewGuid())), Single(Work(Guid.NewGuid()))).ShouldBe(1.0);
    }

    [Test]
    public void Ratio_IsOne_ForMismatchedShapes()
    {
        var shift = Guid.NewGuid();
        var twoDay = new Cell[1, 2];
        twoDay[0, 0] = Work(shift);
        twoDay[0, 1] = Cell.Free();
        var other = new HarmonyBitmap([new BitmapAgent("A", "A", 8m, new HashSet<CellSymbol>())], [D, D.AddDays(1)], twoDay);
        BitmapChurn.Ratio(Single(Work(shift)), other).ShouldBe(1.0);
    }
}
