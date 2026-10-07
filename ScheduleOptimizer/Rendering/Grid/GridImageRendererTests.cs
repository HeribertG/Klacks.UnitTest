// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Rendering.Grid;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace Klacks.UnitTest.ScheduleOptimizer.Rendering.Grid;

[TestFixture]
public class GridImageRendererTests
{
    private static readonly SKColor Yellow = new(0xFF, 0xD7, 0x00);
    private static readonly SKColor Magenta = new(0xE0, 0x00, 0xE0);

    [Test]
    public void Render_ImageSizeFollowsHeadersCellsAndScale()
    {
        var image = Image(rows: 2, columns: 3);

        using var normal = SKBitmap.Decode(new GridImageRenderer().Render(image));
        using var doubled = SKBitmap.Decode(new GridImageRenderer(new GridImageRenderOptions(Scale: 2f)).Render(image));

        normal.Width.ShouldBe(32 + (3 * 24));
        normal.Height.ShouldBe(32 + (2 * 24));
        doubled.Width.ShouldBe(normal.Width * 2);
        doubled.Height.ShouldBe(normal.Height * 2);
    }

    [Test]
    public void Render_FilledCell_PaintsItsColour()
    {
        var image = Image(rows: 1, columns: 1, cell: new GridImageCell(Yellow, null, null, false, false));

        using var bitmap = SKBitmap.Decode(new GridImageRenderer().Render(image));

        bitmap.GetPixel(32 + 5, 32 + 5).ShouldBe(Yellow);
    }

    [Test]
    public void Render_TintedColumnWithEmptyCell_ShowsTint()
    {
        var image = new GridImage(["R"], [new GridImageColumn("1", "M", Tinted: true)], new[,] { { GridImageCell.Empty } }, []);

        using var bitmap = SKBitmap.Decode(new GridImageRenderer().Render(image));

        bitmap.GetPixel(32 + 5, 32 + 5).ShouldBe(new SKColor(0xF5, 0xF5, 0xDC));
    }

    [Test]
    public void Render_Marker_RingsTheCell()
    {
        var image = Image(rows: 2, columns: 2) with { Markers = [new GridImageMarker(1, 1, "1")] };

        using var bitmap = SKBitmap.Decode(new GridImageRenderer().Render(image));

        bitmap.GetPixel(32 + 24 + 1, 32 + 24 + 12).ShouldBe(Magenta);
        bitmap.GetPixel(32 + 1, 32 + 12).ShouldNotBe(Magenta);
    }

    [Test]
    public void Render_CellDimensionMismatch_Throws()
    {
        var image = new GridImage(["A", "B"], [new GridImageColumn("1", "M", false)], new GridImageCell[1, 1], []);

        Should.Throw<ArgumentException>(() => new GridImageRenderer().Render(image));
    }

    private static GridImage Image(int rows, int columns, GridImageCell? cell = null)
    {
        var cells = new GridImageCell[rows, columns];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                cells[r, c] = cell ?? GridImageCell.Empty;
            }
        }

        return new GridImage(
            Enumerable.Range(0, rows).Select(r => $"R{r}").ToList(),
            Enumerable.Range(0, columns).Select(c => new GridImageColumn($"{c + 1}", "M", false)).ToList(),
            cells,
            []);
    }
}
