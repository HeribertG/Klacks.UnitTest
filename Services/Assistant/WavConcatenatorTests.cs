// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies that WavConcatenator joins WAV pieces of the same format into one valid RIFF/WAVE file (sizes rewritten,
/// samples in order), passes a single piece through unchanged and rejects pieces of different formats.
/// </summary>

using System.Buffers.Binary;
using System.Text;
using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class WavConcatenatorTests
{
    private const int SampleRate = 44100;

    internal static byte[] Wav(byte[] samples, int sampleRate = SampleRate)
    {
        using var stream = new MemoryStream();
        void Ascii(string id) => stream.Write(Encoding.ASCII.GetBytes(id));
        void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
        void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); stream.Write(b); }

        Ascii("RIFF");
        U32((uint)(36 + samples.Length));
        Ascii("WAVE");
        Ascii("fmt ");
        U32(16);
        U16(1);
        U16(1);
        U32((uint)sampleRate);
        U32((uint)(sampleRate * 2));
        U16(2);
        U16(16);
        Ascii("data");
        U32((uint)samples.Length);
        stream.Write(samples);
        return stream.ToArray();
    }

    [Test]
    public void Concatenate_SinglePiece_ReturnsItUnchanged()
    {
        var piece = Wav([1, 2, 3, 4]);

        WavConcatenator.Concatenate([piece]).ShouldBeSameAs(piece);
    }

    [Test]
    public void Concatenate_TwoPieces_AppendsSamplesAndRewritesSizes()
    {
        var joined = WavConcatenator.Concatenate([Wav([1, 2, 3, 4]), Wav([5, 6])]);

        Encoding.ASCII.GetString(joined, 0, 4).ShouldBe("RIFF");
        Encoding.ASCII.GetString(joined, 8, 4).ShouldBe("WAVE");
        BinaryPrimitives.ReadUInt32LittleEndian(joined.AsSpan(4, 4)).ShouldBe((uint)(joined.Length - 8));
        Encoding.ASCII.GetString(joined, 36, 4).ShouldBe("data");
        BinaryPrimitives.ReadUInt32LittleEndian(joined.AsSpan(40, 4)).ShouldBe(6u);
        BinaryPrimitives.ReadUInt32LittleEndian(joined.AsSpan(24, 4)).ShouldBe((uint)SampleRate);
        joined.AsSpan(44).ToArray().ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6 });
        joined.ShouldBe(Wav([1, 2, 3, 4, 5, 6]));
    }

    [Test]
    public void Concatenate_DifferentFormats_Throws()
    {
        Should.Throw<InvalidDataException>(() =>
            WavConcatenator.Concatenate([Wav([1, 2]), Wav([3, 4], sampleRate: 22050)]));
    }

    [Test]
    public void Concatenate_PieceWithoutRiffHeader_Throws()
    {
        Should.Throw<InvalidDataException>(() =>
            WavConcatenator.Concatenate([Wav([1, 2]), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]]));
    }
}
