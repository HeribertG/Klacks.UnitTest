// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
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

        embedding.Count().ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
        var norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
        norm.ShouldBe(1.0, 0.01);
    }

    [Test]
    public async Task EmbedAsync_SameInput_ProducesDeterministicOutput()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var a = await provider.EmbedAsync("test query", CancellationToken.None);
        var b = await provider.EmbedAsync("test query", CancellationToken.None);

        a.ShouldBeEquivalentTo(b);
    }

    [Test]
    public async Task EmbedBatchAsync_MultipleTexts_ReturnsBatchOfCorrectSize()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var texts = new[] { "first text", "second text", "third text" };
        var embeddings = await provider.EmbedBatchAsync(texts, CancellationToken.None);

        embeddings.Count().ShouldBe(3);
        foreach (var emb in embeddings)
        {
            emb.Count().ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
        }
    }

    [Test]
    public async Task EmbedBatchAsync_BatchSpanningMultipleChunks_ReturnsAllVectorsNormalized()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var count = KnowledgeIndexConstants.EmbeddingBatchSize + 5;
        var texts = Enumerable.Range(0, count).Select(i => $"open shift number {i}").ToArray();

        var embeddings = await provider.EmbedBatchAsync(texts, CancellationToken.None);

        embeddings.Length.ShouldBe(count);
        foreach (var emb in embeddings)
        {
            emb.Length.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
            var norm = Math.Sqrt(emb.Sum(x => (double)x * x));
            norm.ShouldBe(1.0, 0.01);
        }
    }

    [Test]
    public async Task EmbedAsync_DifferentTexts_ProduceDifferentVectors()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxEmbeddingProvider(loader, CacheDir);

        var a = await provider.EmbedAsync("show open shifts", CancellationToken.None);
        var b = await provider.EmbedAsync("delete a user account", CancellationToken.None);

        a.ShouldNotBe(b);
    }
}
