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
            assignments.Add(new BitmapAssignment(
                agent.Id,
                startDate.AddDays(d),
                symbols[d],
                Guid.NewGuid(),
                [Guid.NewGuid()],
                false));
        }
        var input = new BitmapInput(
            [agent],
            startDate,
            startDate.AddDays(symbols.Count - 1),
            assignments);
        return BitmapBuilder.Build(input);
    }
}
