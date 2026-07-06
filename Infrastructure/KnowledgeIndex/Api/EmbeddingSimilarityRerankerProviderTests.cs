// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmbeddingSimilarityRerankerProvider — verifies the cosine-similarity math against
/// known vectors (identical, orthogonal, opposite, zero-vector) and that it delegates to the injected
/// IEmbeddingProvider rather than embedding candidates itself.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Infrastructure.Api;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Api;

[TestFixture]
public class EmbeddingSimilarityRerankerProviderTests
{
    private IEmbeddingProvider _embeddingProvider = null!;
    private EmbeddingSimilarityRerankerProvider _reranker = null!;

    [SetUp]
    public void SetUp()
    {
        _embeddingProvider = Substitute.For<IEmbeddingProvider>();
        _reranker = new EmbeddingSimilarityRerankerProvider(_embeddingProvider);
    }

    [Test]
    public async Task ScoreAsync_IdenticalVectors_ScoresOne()
    {
        _embeddingProvider.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new float[] { 1, 0 } });

        var scores = await _reranker.ScoreAsync("q", ["candidate"], CancellationToken.None);

        scores[0].ShouldBe(1.0, 0.0001);
    }

    [Test]
    public async Task ScoreAsync_OrthogonalVectors_ScoresZero()
    {
        _embeddingProvider.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new float[] { 0, 1 } });

        var scores = await _reranker.ScoreAsync("q", ["candidate"], CancellationToken.None);

        scores[0].ShouldBe(0.0, 0.0001);
    }

    [Test]
    public async Task ScoreAsync_OppositeVectors_ScoresMinusOne()
    {
        _embeddingProvider.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new float[] { -1, 0 } });

        var scores = await _reranker.ScoreAsync("q", ["candidate"], CancellationToken.None);

        scores[0].ShouldBe(-1.0, 0.0001);
    }

    [Test]
    public async Task ScoreAsync_ZeroVector_ScoresZero_NoDivideByZeroThrow()
    {
        _embeddingProvider.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(new float[] { 0, 0 });
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new float[] { 1, 1 } });

        var scores = await _reranker.ScoreAsync("q", ["candidate"], CancellationToken.None);

        scores[0].ShouldBe(0.0);
    }

    [Test]
    public async Task ScoreAsync_MultipleCandidates_ScoresEachIndependently()
    {
        _embeddingProvider.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new float[] { 1, 0 }, new float[] { 0, 1 } });

        var scores = await _reranker.ScoreAsync("q", ["match", "no-match"], CancellationToken.None);

        scores[0].ShouldBe(1.0, 0.0001);
        scores[1].ShouldBe(0.0, 0.0001);
    }

    [Test]
    public async Task ScoreAsync_EmptyCandidates_ReturnsEmpty_DoesNotCallEmbeddingProvider()
    {
        var scores = await _reranker.ScoreAsync("q", [], CancellationToken.None);

        scores.ShouldBeEmpty();
        await _embeddingProvider.DidNotReceive().EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
