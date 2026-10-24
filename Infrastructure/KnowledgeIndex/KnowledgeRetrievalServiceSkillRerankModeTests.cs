// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SkillRerankOptions decides whether a skill pass pays for the cross-encoder (~1.6 s per turn, eval 2026-10-24).
/// Always reranks (the shipped default), Never keeps the vector order with cosine scores, Adaptive reranks only when
/// the cosine gap between the first two candidates is below the threshold. Recipe passes are always reranked: their
/// match threshold is calibrated on the cross-encoder's scale.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeRetrievalServiceSkillRerankModeTests
{
    private const double Threshold = 0.03;

    // Cosines of the candidates against the query (1, 0, 0): a clear field and a tight one.
    private static readonly double[] ClearField = [0.99, 0.80, 0.70];
    private static readonly double[] TightField = [0.990, 0.985, 0.70];

    private IEmbeddingProvider _embeddings = null!;
    private IRerankerProvider _reranker = null!;
    private IKnowledgeIndexRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _embeddings = Substitute.For<IEmbeddingProvider>();
        _reranker = Substitute.For<IRerankerProvider>();
        _repo = Substitute.For<IKnowledgeIndexRepository>();

        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new[] { 1f, 0f, 0f });

        // The fake cross-encoder reverses the vector order, so a reranked pass is recognisable by its first entry.
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(1).Select((_, i) => 0.1 + i * 0.3).ToArray());
    }

    [Test]
    public async Task Always_ReranksEverySkillPass()
    {
        var result = await RetrieveAsync(SkillRerankMode.Always, ClearField);

        await _reranker.ReceivedWithAnyArgs(1).ScoreAsync(default!, default!, default);
        result.Candidates[0].Entry.SourceId.ShouldBe("s2");
    }

    [Test]
    public async Task Never_KeepsTheVectorOrder_WithCosineScores()
    {
        var result = await RetrieveAsync(SkillRerankMode.Never, TightField);

        await _reranker.DidNotReceiveWithAnyArgs().ScoreAsync(default!, default!, default);
        result.Candidates.Select(c => c.Entry.SourceId).ShouldBe(["s0", "s1", "s2"]);
        result.Candidates[0].Score.ShouldBe(0.99, tolerance: 1e-4);
    }

    [Test]
    public async Task Adaptive_ClearMargin_KeepsTheVectorOrder()
    {
        var result = await RetrieveAsync(SkillRerankMode.Adaptive, ClearField);

        await _reranker.DidNotReceiveWithAnyArgs().ScoreAsync(default!, default!, default);
        result.Candidates[0].Entry.SourceId.ShouldBe("s0");
    }

    [Test]
    public async Task Adaptive_TightMargin_Reranks()
    {
        var result = await RetrieveAsync(SkillRerankMode.Adaptive, TightField);

        await _reranker.ReceivedWithAnyArgs(1).ScoreAsync(default!, default!, default);
        result.Candidates[0].Entry.SourceId.ShouldBe("s2");
    }

    [Test]
    public async Task RecipePass_IsRerankedEvenInNeverMode()
    {
        await RetrieveAsync(SkillRerankMode.Never, ClearField, KnowledgeEntryKind.Recipe);

        await _reranker.ReceivedWithAnyArgs(1).ScoreAsync(default!, default!, default);
    }

    [TestCase("adaptive", SkillRerankMode.Adaptive)]
    [TestCase("NEVER", SkillRerankMode.Never)]
    [TestCase("bogus", SkillRerankMode.Always)]
    [TestCase("7", SkillRerankMode.Always)]
    [TestCase(null, SkillRerankMode.Always)]
    public void FromConfiguration_UnknownValuesFallBackToAlways(string? configured, SkillRerankMode expected)
    {
        SkillRerankOptions.FromConfiguration(configured, Threshold).Mode.ShouldBe(expected);
    }

    private Task<RetrievalResult> RetrieveAsync(
        SkillRerankMode mode, double[] cosines, KnowledgeEntryKind kind = KnowledgeEntryKind.Skill)
    {
        var pool = cosines
            .Select((cosine, i) => new KnowledgeEntry
            {
                Kind = kind,
                SourceId = "s" + i,
                Text = "t" + i,
                Embedding = [(float)cosine, (float)Math.Sqrt(1 - cosine * cosine), 0f]
            })
            .ToList();
        _repo.FindNearestAsync(
                Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(pool);

        var service = new KnowledgeRetrievalService(
            _embeddings, _reranker, _repo, skillRerank: new SkillRerankOptions(mode, Threshold));

        return service.RetrieveAsync("q", [], true, 20, currentRoute: null, CancellationToken.None, kind);
    }
}
