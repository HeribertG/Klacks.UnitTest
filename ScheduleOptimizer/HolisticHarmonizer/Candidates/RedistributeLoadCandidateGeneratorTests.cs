// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Candidates;

[TestFixture]
public class RedistributeLoadCandidateGeneratorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Generate_AllRowsAtTarget_ReturnsEmpty()
    {
        var bitmap = BuildBitmap(rows: 2, days: 2, target: 0m);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early, hours: 0m));

        var sut = new RedistributeLoadCandidateGenerator();
        sut.Generate(bitmap).ShouldBeEmpty();
    }

    [Test]
    public void Generate_OverAndUnderPair_ReturnsSameDaySwap()
    {
        // target=10, r0 worked 16h (over), r1 worked 0h (under) → suggest moving one day from r0 to r1.
        var bitmap = BuildBitmap(rows: 2, days: 2, target: 10m);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early, hours: 8m));
        bitmap.SetCell(0, 1, Work(CellSymbol.Early, hours: 8m));

        var sut = new RedistributeLoadCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.ShouldNotBeEmpty();
        result.All(c => c.RowA == 0 && c.RowB == 1).ShouldBeTrue();
        result.All(c => c.DayA == c.DayB).ShouldBeTrue();
    }

    [Test]
    public void Generate_NoOverRow_ReturnsEmpty()
    {
        var bitmap = BuildBitmap(rows: 2, days: 2, target: 100m);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early, hours: 8m));

        var sut = new RedistributeLoadCandidateGenerator();
        sut.Generate(bitmap).ShouldBeEmpty();
    }

    [Test]
    public void Generate_LockedSourceCell_IsSkipped()
    {
        // r0 over-target (16h vs 10), r1 under-target (0h vs 10). r0/d0 locked → only d1 remains.
        var bitmap = BuildBitmap(rows: 2, days: 2, target: 10m);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: true, Hours: 8m));
        bitmap.SetCell(0, 1, Work(CellSymbol.Early, hours: 8m));

        var sut = new RedistributeLoadCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.Any(c => c.DayA == 0).ShouldBeFalse();
        result.Any(c => c.DayA == 1).ShouldBeTrue();
    }

    [Test]
    public void Generate_HintMentionsBothDeviations()
    {
        var bitmap = BuildBitmap(rows: 2, days: 2, target: 10m);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early, hours: 8m));
        bitmap.SetCell(0, 1, Work(CellSymbol.Early, hours: 8m));

        var sut = new RedistributeLoadCandidateGenerator();
        var first = sut.Generate(bitmap).First();

        first.Hint.ShouldContain("over");
        first.Hint.ShouldContain("under");
        first.ExpectedBenefit.ShouldBeGreaterThan(0);
    }

    private static Cell Work(CellSymbol symbol, decimal hours)
        => new(symbol, Guid.NewGuid(), new[] { Guid.NewGuid() }, IsLocked: false, Hours: hours);

    private static HarmonyBitmap BuildBitmap(int rows, int days, decimal target)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", target, new HashSet<CellSymbol>()));
        }
        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }
}
