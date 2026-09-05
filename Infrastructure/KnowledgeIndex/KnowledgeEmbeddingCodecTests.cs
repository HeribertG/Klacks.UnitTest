// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Services;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeEmbeddingCodecTests
{
    [Test]
    public void EncodeDecodeVector_RoundTripsExactly()
    {
        var vector = new[]
        {
            0f, 1f, -1f, 0.5f, -0.5f, 3.4028235e38f, -3.4028235e38f, 1.4e-45f, -1.4e-45f, 0.123456789f
        };

        var decoded = KnowledgeEmbeddingCodec.DecodeVector(KnowledgeEmbeddingCodec.EncodeVector(vector));

        decoded.ShouldBe(vector);
    }

    [Test]
    public void EncodeVector_IsLittleEndianFloat32()
    {
        var encoded = KnowledgeEmbeddingCodec.EncodeVector(new[] { 1f });

        Convert.FromBase64String(encoded).ShouldBe(new byte[] { 0x00, 0x00, 0x80, 0x3F });
    }

    [Test]
    public void EncodeVector_EmptyVector_ProducesEmptyString()
    {
        KnowledgeEmbeddingCodec.EncodeVector(Array.Empty<float>()).ShouldBe(string.Empty);
    }

    [Test]
    public void DecodeVector_EmptyString_ProducesEmptyVector()
    {
        KnowledgeEmbeddingCodec.DecodeVector(string.Empty).ShouldBeEmpty();
    }

    [Test]
    public void DecodeVector_InvalidBase64_Throws()
    {
        Should.Throw<FormatException>(() => KnowledgeEmbeddingCodec.DecodeVector("not base64 !!"));
    }

    [Test]
    public void DecodeVector_LengthNotMultipleOfFloatSize_Throws()
    {
        var truncated = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        Should.Throw<FormatException>(() => KnowledgeEmbeddingCodec.DecodeVector(truncated));
    }

    [Test]
    public void ToHex_ProducesLowercase()
    {
        KnowledgeEmbeddingCodec.ToHex(new byte[] { 0xAB, 0x0F, 0xFF }).ShouldBe("ab0fff");
    }

    [Test]
    public void ToHex_EmptyArray_ProducesEmptyString()
    {
        KnowledgeEmbeddingCodec.ToHex(Array.Empty<byte>()).ShouldBe(string.Empty);
    }

    [Test]
    public void FromHex_RoundTripsBytes()
    {
        var bytes = new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0x10 };

        KnowledgeEmbeddingCodec.FromHex(KnowledgeEmbeddingCodec.ToHex(bytes)).ShouldBe(bytes);
    }

    [Test]
    public void FromHex_AcceptsUppercase()
    {
        KnowledgeEmbeddingCodec.FromHex("AB0FFF").ShouldBe(new byte[] { 0xAB, 0x0F, 0xFF });
    }

    [Test]
    public void FromHex_EmptyString_ProducesEmptyArray()
    {
        KnowledgeEmbeddingCodec.FromHex(string.Empty).ShouldBeEmpty();
    }

    [Test]
    public void FromHex_InvalidCharacters_Throws()
    {
        Should.Throw<FormatException>(() => KnowledgeEmbeddingCodec.FromHex("zz"));
    }

    [Test]
    public void FromHex_OddLength_Throws()
    {
        Should.Throw<FormatException>(() => KnowledgeEmbeddingCodec.FromHex("abc"));
    }
}
