// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Constants;
using Klacks.Api.Infrastructure.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
[Category("SlowModelLoad")]
public class OnnxEmbeddingProviderTests
{
    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.EmbeddingModelName);

    [Test]
    public async Task EmbedAsync_ReturnsNormalizedVectorOfCorrectDimension()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var embedding = await provider.EmbedAsync("show open shifts", CancellationToken.None);

        embedding.Should().HaveCount(KnowledgeIndexConstants.EmbeddingDimension);
        var norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
        norm.Should().BeApproximately(1.0, 0.01);
    }

    [Test]
    public async Task EmbedAsync_SameInput_ProducesDeterministicOutput()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var a = await provider.EmbedAsync("test query", CancellationToken.None);
        var b = await provider.EmbedAsync("test query", CancellationToken.None);

        a.Should().BeEquivalentTo(b);
    }

    [Test]
    public async Task EmbedBatchAsync_MultipleTexts_ReturnsBatchOfCorrectSize()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var texts = new[] { "first text", "second text", "third text" };
        var embeddings = await provider.EmbedBatchAsync(texts, CancellationToken.None);

        embeddings.Should().HaveCount(3);
        foreach (var emb in embeddings)
        {
            emb.Should().HaveCount(KnowledgeIndexConstants.EmbeddingDimension);
        }
    }

    [Test]
    public async Task EmbedAsync_DifferentTexts_ProduceDifferentVectors()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var a = await provider.EmbedAsync("show open shifts", CancellationToken.None);
        var b = await provider.EmbedAsync("delete a user account", CancellationToken.None);

        a.Should().NotBeEquivalentTo(b);
    }
}
