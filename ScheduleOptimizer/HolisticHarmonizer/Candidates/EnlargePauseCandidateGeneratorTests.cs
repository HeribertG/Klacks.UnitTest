// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Candidates;

[TestFixture]
public class EnlargePauseCandidateGeneratorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Generate_NoPauses_ReturnsEmpty()
    {
        var bitmap = BuildBitmap(rows: 2, days: 4);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 1, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(0, 3, Work(CellSymbol.Early));

        var sut = new EnlargePauseCandidateGenerator();
        sut.Generate(bitmap).ShouldBeEmpty();
    }

    [Test]
    public void Generate_LongPauseAboveThreshold_IsSkipped()
    {
        // r0: E _ _ _ _ E   (pause length 4, above 2-day threshold)
        var bitmap = BuildBitmap(rows: 2, days: 6);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 5, Work(CellSymbol.Early));

        var sut = new EnlargePauseCandidateGenerator();
        sut.Generate(bitmap).ShouldBeEmpty();
    }

    [Test]
    public void Generate_OneDayPauseWithFreePartner_ReturnsCandidate()
    {
        // r0: E _ E   (pause length 1)
        // r1: _ _ _   (free on day 0 → swap r0/d0 ↔ r1/d0 widens pause)
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));

        var sut = new EnlargePauseCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.ShouldNotBeEmpty();
        result.Any(c => c.RowA == 0 && c.DayA == 0 && c.RowB == 1 && c.DayB == 0).ShouldBeTrue();
        result.Any(c => c.RowA == 0 && c.DayA == 2 && c.RowB == 1 && c.DayB == 2).ShouldBeTrue();
    }

    [Test]
    public void Generate_LockedEdgeWorkCell_IsSkipped()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: true));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));

        var sut = new EnlargePauseCandidateGenerator();
        var result = sut.Generate(bitmap).ToList();

        result.Any(c => c.DayA == 0).ShouldBeFalse();
        result.Any(c => c.DayA == 2).ShouldBeTrue();
    }

    [Test]
    public void Generate_NoFreePartner_ReturnsEmpty()
    {
        // Both rows working on every day → no free partner for the swap.
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(1, 0, Work(CellSymbol.Late));
        bitmap.SetCell(1, 1, Work(CellSymbol.Late));
        bitmap.SetCell(1, 2, Work(CellSymbol.Late));

        var sut = new EnlargePauseCandidateGenerator();
        sut.Generate(bitmap).ShouldBeEmpty();
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
