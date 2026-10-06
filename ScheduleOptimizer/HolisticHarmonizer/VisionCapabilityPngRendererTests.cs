// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Bitmap;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// The vision check is only fair when the token is actually painted. Without a usable font (e.g. a container
/// without font packages) the image would be an empty yellow box and every model would fail the check.
/// </summary>
[TestFixture]
public class VisionCapabilityPngRendererTests
{
    private const byte DarkThreshold = 64;
    private const double MinInkShare = 0.03;
    private const int BorderMargin = 8;

    [Test]
    public void Render_PaintsVisibleTokenGlyphsInsideTheBox()
    {
        var png = VisionCapabilityPngRenderer.Render("KXN");

        using var bitmap = SKBitmap.Decode(png);
        bitmap.ShouldNotBeNull();

        var inner = 0;
        var dark = 0;
        for (var y = BorderMargin; y < bitmap.Height - BorderMargin; y++)
        {
            for (var x = BorderMargin; x < bitmap.Width - BorderMargin; x++)
            {
                inner++;
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < DarkThreshold && pixel.Green < DarkThreshold && pixel.Blue < DarkThreshold)
                {
                    dark++;
                }
            }
        }

        ((double)dark / inner).ShouldBeGreaterThan(MinInkShare);
    }

    [Test]
    public void Render_DifferentTokens_ProduceDifferentImages()
    {
        var first = VisionCapabilityPngRenderer.Render("KXN");
        var second = VisionCapabilityPngRenderer.Render("EFH");

        first.SequenceEqual(second).ShouldBeFalse();
    }
}
