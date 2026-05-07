// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.IO;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Bitmap;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;
using HarmonyBitmapType = Klacks.ScheduleOptimizer.Harmonizer.Bitmap.HarmonyBitmap;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Bitmap;

[TestFixture]
public class HarmonyBitmapPngRendererTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Render_EmptyBitmap_ProducesValidPng()
    {
        var bitmap = BuildBitmap(rowCount: 0, dayCount: 0);
        var renderer = new HarmonyBitmapPngRenderer();

        var bytes = renderer.Render(bitmap);

        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(PngSignature.Length);
        for (var i = 0; i < PngSignature.Length; i++)
        {
            bytes[i].ShouldBe(PngSignature[i]);
        }
    }

    [Test]
    public void Render_NonEmptyBitmap_ImageDimensionsMatchExpected()
    {
        var options = HarmonyBitmapPngRenderOptions.Default;
        var bitmap = BuildBitmap(rowCount: 3, dayCount: 7);
        var renderer = new HarmonyBitmapPngRenderer(options);

        var bytes = renderer.Render(bitmap);

        var expectedWidth = options.HeaderLeft + (bitmap.DayCount * options.CellSize);
        var expectedHeight = options.HeaderTop + (bitmap.RowCount * options.CellSize);

        using var ms = new MemoryStream(bytes);
        using var decoded = SKBitmap.Decode(ms);
        decoded.ShouldNotBeNull();
        decoded.Width.ShouldBe(expectedWidth);
        decoded.Height.ShouldBe(expectedHeight);
    }

    [Test]
    public void Render_BreakCellPresent_ProducesPng()
    {
        var bitmap = BuildBitmap(rowCount: 2, dayCount: 5, configure: cells =>
        {
            cells[0, 2] = new Cell(CellSymbol.Break, null, [Guid.NewGuid()], true, default, default, 8m);
        });
        var renderer = new HarmonyBitmapPngRenderer();

        var bytes = renderer.Render(bitmap);

        AssertHasPngSignature(bytes);
    }

    [Test]
    public void Render_AllSymbolKinds_ProducesPng()
    {
        var bitmap = BuildBitmap(rowCount: 2, dayCount: 6, configure: cells =>
        {
            cells[0, 0] = Cell.Free();
            cells[0, 1] = new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false, default, default, 8m);
            cells[0, 2] = new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false, default, default, 8m);
            cells[0, 3] = new Cell(CellSymbol.Night, Guid.NewGuid(), [Guid.NewGuid()], true, default, default, 8m);
            cells[0, 4] = new Cell(CellSymbol.Other, Guid.NewGuid(), [Guid.NewGuid()], true, default, default, 8m);
            cells[0, 5] = new Cell(CellSymbol.Break, null, [Guid.NewGuid()], true, default, default, 8m);

            cells[1, 0] = new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], true, default, default, 8m);
            cells[1, 1] = new Cell(CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], true, default, default, 8m);
        });
        var renderer = new HarmonyBitmapPngRenderer();

        var bytes = renderer.Render(bitmap);

        AssertHasPngSignature(bytes);
        using var ms = new MemoryStream(bytes);
        using var decoded = SKBitmap.Decode(ms);
        decoded.ShouldNotBeNull();
        decoded.Width.ShouldBeGreaterThan(0);
        decoded.Height.ShouldBeGreaterThan(0);
    }

    private static void AssertHasPngSignature(byte[] bytes)
    {
        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(PngSignature.Length);
        for (var i = 0; i < PngSignature.Length; i++)
        {
            bytes[i].ShouldBe(PngSignature[i]);
        }
    }

    private static HarmonyBitmapType BuildBitmap(int rowCount, int dayCount, Action<Cell[,]>? configure = null)
    {
        var rows = new BitmapAgent[rowCount];
        for (var r = 0; r < rowCount; r++)
        {
            rows[r] = new BitmapAgent(
                Id: $"agent-{r}",
                DisplayName: $"Agent {r}",
                TargetHours: 0m,
                PreferredShiftSymbols: new HashSet<CellSymbol>());
        }

        var days = new DateOnly[dayCount];
        for (var d = 0; d < dayCount; d++)
        {
            days[d] = Day0.AddDays(d);
        }

        var cells = new Cell[rowCount, dayCount];
        for (var r = 0; r < rowCount; r++)
        {
            for (var d = 0; d < dayCount; d++)
            {
                cells[r, d] = Cell.Free();
            }
        }
        configure?.Invoke(cells);

        return new HarmonyBitmapType(rows, days, cells);
    }
}
