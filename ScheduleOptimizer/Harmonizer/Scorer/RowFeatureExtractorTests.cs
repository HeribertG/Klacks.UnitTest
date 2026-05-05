// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Scorer;

[TestFixture]
public class RowFeatureExtractorTests
{
    [Test]
    public void Extract_EmptyRow_ReturnsTrivialFeatures()
    {
        var bitmap = BuildRow(new[] { CellSymbol.Free, CellSymbol.Free, CellSymbol.Free });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(0);
        features.BlockSizeUniformity.ShouldBe(1.0);
        features.RestUniformity.ShouldBe(1.0);
        features.BlockHomogeneity.ShouldBe(1.0);
        features.TransitionCompliance.ShouldBe(1.0);
    }

    [Test]
    public void Extract_SingleBlock_AllFeaturesAtOne()
    {
        var bitmap = BuildRow(new[] { CellSymbol.Early, CellSymbol.Early, CellSymbol.Early, CellSymbol.Free });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(1);
        features.BlockSizeUniformity.ShouldBe(1.0);
        features.RestUniformity.ShouldBe(1.0);
        features.BlockHomogeneity.ShouldBe(1.0);
        features.TransitionCompliance.ShouldBe(1.0);
    }

    [Test]
    public void Extract_HeterogeneousBlock_DropsHomogeneity()
    {
        var bitmap = BuildRow(new[] { CellSymbol.Early, CellSymbol.Late, CellSymbol.Free });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(1);
        features.BlockHomogeneity.ShouldBe(0.0);
    }

    [Test]
    public void Extract_ForwardTransitions_FullyCompliant()
    {
        var bitmap = BuildRow(new[]
        {
            CellSymbol.Early, CellSymbol.Free,
            CellSymbol.Late, CellSymbol.Free,
            CellSymbol.Night, CellSymbol.Free,
        });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(3);
        features.TransitionCompliance.ShouldBe(1.0);
    }

    [Test]
    public void Extract_BackwardTransition_DropsCompliance()
    {
        var bitmap = BuildRow(new[]
        {
            CellSymbol.Night, CellSymbol.Free,
            CellSymbol.Late, CellSymbol.Free,
            CellSymbol.Early,
        });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(3);
        features.TransitionCompliance.ShouldBe(0.0);
    }

    [Test]
    public void Extract_VariedBlockSizes_DropsBlockSizeUniformity()
    {
        var bitmap = BuildRow(new[]
        {
            CellSymbol.Early, CellSymbol.Free,
            CellSymbol.Early, CellSymbol.Early, CellSymbol.Early, CellSymbol.Early, CellSymbol.Early,
        });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(2);
        features.BlockSizeUniformity.ShouldBeLessThan(0.5);
    }

    [Test]
    public void Extract_BreakIsTreatedLikeFree_ForBlockScanning()
    {
        var bitmap = BuildRow(new[] { CellSymbol.Early, CellSymbol.Early, CellSymbol.Break, CellSymbol.Late, CellSymbol.Late });

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.WorkBlockCount.ShouldBe(2);
    }

    [Test]
    public void Extract_BreakHoursContributeToTargetHoursDeviation()
    {
        var startDate = new DateOnly(2026, 1, 1);
        var agent = new BitmapAgent("agent-0", "A", TargetHours: 24m, new HashSet<CellSymbol>());
        var input = new BitmapInput([agent], startDate, startDate.AddDays(2), []);
        var bitmap = BitmapBuilder.Build(input);

        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            startDate.ToDateTime(new TimeOnly(7, 0)), startDate.ToDateTime(new TimeOnly(15, 0)), 8m));
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Break, null, [Guid.NewGuid()], true, default, default, 8m));
        bitmap.SetCell(0, 2, new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false,
            startDate.AddDays(2).ToDateTime(new TimeOnly(14, 0)), startDate.AddDays(2).ToDateTime(new TimeOnly(22, 0)), 8m));

        var features = RowFeatureExtractor.Extract(bitmap, 0);

        features.TargetHoursDeviation.ShouldBe(0.0);
    }

    private static HarmonyBitmap BuildRow(IReadOnlyList<CellSymbol> symbols)
    {
        var startDate = new DateOnly(2026, 1, 1);
        var agent = new BitmapAgent("agent-0", "A", 100m, new HashSet<CellSymbol>());
        var assignments = new List<BitmapAssignment>();
        for (var d = 0; d < symbols.Count; d++)
        {
            if (symbols[d] == CellSymbol.Free)
            {
                continue;
            }
            var isBreak = symbols[d] == CellSymbol.Break;
            assignments.Add(new BitmapAssignment(
                agent.Id,
                startDate.AddDays(d),
                symbols[d],
                isBreak ? Guid.Empty : Guid.NewGuid(),
                [Guid.NewGuid()],
                isBreak));
        }
        var input = new BitmapInput(
            [agent],
            startDate,
            startDate.AddDays(symbols.Count - 1),
            assignments);
        return BitmapBuilder.Build(input);
    }
}
