// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The source-id filter keeps entries out of the cross-encoder: the recipe engine passes only the recipes its guards
/// would accept at all, so the recipe pass stops scoring the whole catalogue (~1 s per turn, eval 2026-10-24). An
/// empty filter returns nothing without embedding the query.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeRetrievalServiceSourceIdFilterTests
{
    private IEmbeddingProvider _embeddings = null!;
    private IRerankerProvider _reranker = null!;
    private KnowledgeRetrievalService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _embeddings = Substitute.For<IEmbeddingProvider>();
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new[] { 1f, 0f, 0f });
        _reranker = Substitute.For<IRerankerProvider>();
        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(1).Select(_ => 0.9).ToArray());

        var repository = Substitute.For<IKnowledgeIndexRepository>();
        repository.FindNearestAsync(
                Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new[] { "onboard-employee", "create-group", "setup-consultation" }
                .Select(name => new KnowledgeEntry
                {
                    Kind = KnowledgeEntryKind.Recipe, SourceId = name, Text = name, Embedding = [1f, 0f, 0f]
                })
                .ToList());

        _service = new KnowledgeRetrievalService(_embeddings, _reranker, repository);
    }

    [Test]
    public async Task OnlyTheAllowedEntries_AreScored_CaseInsensitive()
    {
        var result = await RetrieveAsync(["ONBOARD-EMPLOYEE", "setup-consultation"]);

        result.Candidates.Select(c => c.Entry.SourceId).ShouldBe(["onboard-employee", "setup-consultation"], ignoreOrder: true);
        await _reranker.Received(1).ScoreAsync(
            Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(texts => texts.Count == 2), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEmptyFilter_ReturnsNothing_WithoutAnyModelCall()
    {
        var result = await RetrieveAsync([]);

        result.Candidates.ShouldBeEmpty();
        await _embeddings.DidNotReceiveWithAnyArgs().EmbedQueryAsync(default!, default);
        await _reranker.DidNotReceiveWithAnyArgs().ScoreAsync(default!, default!, default);
    }

    [Test]
    public async Task AFilterWithoutOverlap_SkipsTheReranker()
    {
        var result = await RetrieveAsync(["unknown-recipe"]);

        result.Candidates.ShouldBeEmpty();
        await _reranker.DidNotReceiveWithAnyArgs().ScoreAsync(default!, default!, default);
    }

    [Test]
    public async Task NoFilter_ScoresEveryEntry()
    {
        var result = await RetrieveAsync(null);

        result.Candidates.Count.ShouldBe(3);
    }

    private Task<RetrievalResult> RetrieveAsync(IReadOnlyCollection<string>? filter) =>
        _service.RetrieveAsync(
            "neuer Mitarbeiter", [], false, 3, currentRoute: null, CancellationToken.None,
            KnowledgeEntryKind.Recipe, filter);
}
