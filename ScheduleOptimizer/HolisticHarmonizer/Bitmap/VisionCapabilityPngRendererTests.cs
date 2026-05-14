// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.IO;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Bitmap;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Bitmap;

[TestFixture]
public class VisionCapabilityPngRendererTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Test]
    public void Render_ReturnsValidPng()
    {
        var bytes = VisionCapabilityPngRenderer.Render("EFH");

        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(PngSignature.Length);
        for (var i = 0; i < PngSignature.Length; i++)
        {
            bytes[i].ShouldBe(PngSignature[i]);
        }
    }

    [Test]
    public void Render_DecodedImage_HasExpectedCanvasSize()
    {
        var bytes = VisionCapabilityPngRenderer.Render("LNP");

        using var stream = new MemoryStream(bytes);
        using var decoded = SKBitmap.Decode(stream);
        decoded.ShouldNotBeNull();
        decoded.Width.ShouldBeGreaterThanOrEqualTo(100);
        decoded.Height.ShouldBeGreaterThanOrEqualTo(50);
    }

    [Test]
    public void Render_WhitespaceToken_Throws()
    {
        Should.Throw<ArgumentException>(() => VisionCapabilityPngRenderer.Render(" "));
    }

    [Test]
    public void Render_NullToken_Throws()
    {
        Should.Throw<ArgumentException>(() => VisionCapabilityPngRenderer.Render(null!));
    }
}
