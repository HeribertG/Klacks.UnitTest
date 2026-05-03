// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

[TestFixture]
public class BitmapReplaceValidatorTests
{
    [Test]
    public void IsValid_SelfSwap_Rejected()
    {
        var bitmap = BuildBitmap(2, 3);
        var validator = new BitmapReplaceValidator();

        validator.IsValid(bitmap, new ReplaceMove(0, 0, 0)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_OutOfRangeRow_Rejected()
    {
        var bitmap = BuildBitmap(2, 3);
        var validator = new BitmapReplaceValidator();

        validator.IsValid(bitmap, new ReplaceMove(0, 5, 0)).ShouldBeFalse();
        validator.IsValid(bitmap, new ReplaceMove(-1, 0, 0)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_OutOfRangeDay_Rejected()
    {
        var bitmap = BuildBitmap(2, 3);
        var validator = new BitmapReplaceValidator();

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 5)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_LockedCell_Rejected()
    {
        var bitmap = BuildBitmap(2, 3);
        var lockedCell = new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], true);
        bitmap.SetCell(0, 1, lockedCell);
        var validator = new BitmapReplaceValidator();

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_NormalSwap_Accepted()
    {
        var bitmap = BuildBitmap(2, 3);
        var validator = new BitmapReplaceValidator();

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeTrue();
    }

    private static HarmonyBitmap BuildBitmap(int rows, int days)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>()));
        }
        var startDate = new DateOnly(2026, 1, 1);
        var input = new BitmapInput(agents, startDate, startDate.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }
}
