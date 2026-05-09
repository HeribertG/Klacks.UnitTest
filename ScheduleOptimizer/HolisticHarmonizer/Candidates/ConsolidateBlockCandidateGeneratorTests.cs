// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Candidates;

[TestFixture]
public class ConsolidateBlockCandidateGeneratorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Generate_NoFragmentation_ReturnsEmpty()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 1, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));

        var sut = new ConsolidateBlockCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.ShouldBeEmpty();
    }

    [Test]
    public void Generate_FragmentedRowWithGapDay_ReturnsCandidatesForWorkingPartners()
    {
        // r0: E _ E   (gap on day 1)
        // r1: _ E _   (works on day 1 → swap candidate)
        // r2: _ _ _   (free on day 1 → no candidate)
        var bitmap = BuildBitmap(rows: 3, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(1, 1, Work(CellSymbol.Early));

        var sut = new ConsolidateBlockCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.Count.ShouldBe(1);
        result[0].RowA.ShouldBe(0);
        result[0].DayA.ShouldBe(1);
        result[0].RowB.ShouldBe(1);
        result[0].DayB.ShouldBe(1);
        result[0].Hint.ShouldContain("r00");
        result[0].ExpectedBenefit.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Generate_LockedTargetCell_IsSkipped()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Free, null, [], IsLocked: true));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(1, 1, Work(CellSymbol.Early));

        var sut = new ConsolidateBlockCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.ShouldBeEmpty();
    }

    [Test]
    public void Generate_LockedPartnerCell_IsSkipped()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(1, 1, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: true));

        var sut = new ConsolidateBlockCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.ShouldBeEmpty();
    }

    [Test]
    public void Generate_HintIncludesRowAndDay()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(1, 1, Work(CellSymbol.Late));

        var sut = new ConsolidateBlockCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result[0].Hint.ShouldContain("r00");
        result[0].Hint.ShouldContain("day 1");
    }

    private static Cell Work(CellSymbol symbol)
        => new(symbol, Guid.NewGuid(), new[] { Guid.NewGuid() }, IsLocked: false);

    private static HarmonyBitmap BuildBitmap(int rows, int days)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>()));
        }
        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }
}
