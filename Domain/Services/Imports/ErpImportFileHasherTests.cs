// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportFileHasher: SHA-256 of the raw bytes as 64 lower-case hex characters (anchored
/// on the FIPS 180-2 test vectors), deterministic for equal bytes, different for different bytes, and the
/// copy-and-hash path used by the object storage returns the very same value as the hash-only path while
/// copying every byte, also across several copy buffers.
/// </summary>

using System.Text;
using Klacks.Api.Domain.Services.Imports;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Imports;

[TestFixture]
public class ErpImportFileHasherTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const int Sha256HexLength = 64;
    private const int MultiBufferSize = 250_000;
    private const int RandomSeed = 4711;

    [Test]
    public void ComputeSha256Hex_KnownVector_ReturnsLowerCaseHex()
    {
        var hash = ErpImportFileHasher.ComputeSha256Hex(ToStream("abc"));

        hash.ShouldBe(AbcSha256);
    }

    [Test]
    public void ComputeSha256Hex_EmptyContent_ReturnsEmptyVector()
    {
        ErpImportFileHasher.ComputeSha256Hex(new MemoryStream()).ShouldBe(EmptySha256);
    }

    [Test]
    public void ComputeSha256Hex_SameBytesTwice_ReturnsSameHash()
    {
        var bytes = RandomBytes(MultiBufferSize);

        var first = ErpImportFileHasher.ComputeSha256Hex(new MemoryStream(bytes));
        var second = ErpImportFileHasher.ComputeSha256Hex(new MemoryStream(bytes.ToArray()));

        first.ShouldBe(second);
        first.Length.ShouldBe(Sha256HexLength);
        first.ShouldBe(first.ToLowerInvariant());
    }

    [Test]
    public void ComputeSha256Hex_OneByteDiffers_ReturnsDifferentHash()
    {
        var bytes = RandomBytes(MultiBufferSize);
        var changed = bytes.ToArray();
        changed[^1] ^= 0x01;

        ErpImportFileHasher.ComputeSha256Hex(new MemoryStream(bytes))
            .ShouldNotBe(ErpImportFileHasher.ComputeSha256Hex(new MemoryStream(changed)));
    }

    [Test]
    public async Task CopyAndHashAsync_KnownVector_CopiesBytesAndReturnsSizeAndHash()
    {
        using var destination = new MemoryStream();

        var info = await ErpImportFileHasher.CopyAndHashAsync(ToStream("abc"), destination);

        info.SizeBytes.ShouldBe(3);
        info.Sha256.ShouldBe(AbcSha256);
        Encoding.UTF8.GetString(destination.ToArray()).ShouldBe("abc");
    }

    [Test]
    public async Task CopyAndHashAsync_ContentLargerThanOneBuffer_MatchesHashOnlyPathAndCopiesEveryByte()
    {
        var bytes = RandomBytes(MultiBufferSize);
        using var destination = new MemoryStream();

        var info = await ErpImportFileHasher.CopyAndHashAsync(new MemoryStream(bytes), destination);

        info.SizeBytes.ShouldBe(MultiBufferSize);
        info.Sha256.ShouldBe(ErpImportFileHasher.ComputeSha256Hex(new MemoryStream(bytes)));
        destination.ToArray().ShouldBe(bytes);
    }

    [Test]
    public async Task CopyAndHashAsync_BoundedAndContentFits_CopiesAndHashesLikeTheUnboundedPath()
    {
        using var destination = new MemoryStream();

        var info = await ErpImportFileHasher.CopyAndHashAsync(ToStream("abc"), destination, maxBytes: 3);

        info.ShouldNotBeNull();
        info.SizeBytes.ShouldBe(3);
        info.Sha256.ShouldBe(AbcSha256);
        destination.ToArray().ShouldBe(Encoding.UTF8.GetBytes("abc"));
    }

    [Test]
    public async Task CopyAndHashAsync_BoundedAndContentLonger_ReturnsNullWithoutCopyingBeyondTheBound()
    {
        var content = RandomBytes(MultiBufferSize);
        using var destination = new MemoryStream();

        var info = await ErpImportFileHasher.CopyAndHashAsync(new MemoryStream(content), destination, maxBytes: MultiBufferSize - 1);

        info.ShouldBeNull();
        destination.Length.ShouldBeLessThanOrEqualTo(MultiBufferSize - 1);
    }

    private static MemoryStream ToStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(RandomSeed).NextBytes(bytes);
        return bytes;
    }
}
