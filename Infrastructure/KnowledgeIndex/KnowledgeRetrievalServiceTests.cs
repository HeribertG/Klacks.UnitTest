// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Constants;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Services;
using Klacks.Api.Infrastructure.KnowledgeIndex.Domain;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeRetrievalServiceTests
{
    private IEmbeddingProvider _embeddings = null!;
    private IRerankerProvider _reranker = null!;
    private IKnowledgeIndexRepository _repo = null!;
    private KnowledgeRetrievalService _service = null!;

    [SetUp]
    public void Setup()
    {
        _embeddings = Substitute.For<IEmbeddingProvider>();
        _reranker = Substitute.For<IRerankerProvider>();
        _repo = Substitute.For<IKnowledgeIndexRepository>();

        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        _service = new KnowledgeRetrievalService(_embeddings, _reranker, _repo);
    }

    [Test]
    public async Task RetrieveAsync_DropsEndpointWhenWrappingSkillIsInResult()
    {
        var skill = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "ListOpenShifts",
            ExposedEndpointKey = "GET /api/backend/shifts",
            Text = "ListOpenShifts. Returns open shifts."
        };
        var endpoint = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Endpoint,
            SourceId = "GET /api/backend/shifts",
            Text = "GET /api/backend/shifts. Lists shifts."
        };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([skill, endpoint]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.9 });

        var result = await _service.RetrieveAsync("open shifts", [], false, 5, CancellationToken.None);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Entry.SourceId.ShouldBe("ListOpenShifts");
    }

    [Test]
    public async Task RetrieveAsync_ScoreBelowCutoff_ReturnsEmpty()
    {
        var entry = new KnowledgeEntry
        {
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "SomeSkill",
            Text = "Some skill text."
        };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([entry]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { KnowledgeIndexConstants.DefaultScoreCutoff - 0.01 });

        var result = await _service.RetrieveAsync("query", [], false, 5, CancellationToken.None);

        result.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public async Task RetrieveAsync_AdminBypass_ForwardedToRepository()
    {
        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<KnowledgeEntry>());

        await _service.RetrieveAsync("query", [], isAdmin: true, 5, CancellationToken.None);

        await _repo.Received(1).FindNearestAsync(
            Arg.Any<float[]>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Is<bool>(bypass => bypass),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RetrieveAsync_WhitespaceQuery_ReturnsEmpty()
    {
        var result = await _service.RetrieveAsync("   ", [], false, 5, CancellationToken.None);

        result.IsEmpty.ShouldBeTrue();
        await _repo.DidNotReceive().FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RetrieveAsync_CandidatesRankedByScore()
    {
        var high = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "HighScore", Text = "High" };
        var low = new KnowledgeEntry { Kind = KnowledgeEntryKind.Skill, SourceId = "LowScore", Text = "Low" };

        _repo.FindNearestAsync(Arg.Any<float[]>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([high, low]);

        _reranker.ScoreAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new double[] { 0.5, 0.9 });

        var result = await _service.RetrieveAsync("query", [], false, 5, CancellationToken.None);

        result.Candidates.Count().ShouldBe(2);
        result.Candidates[0].Entry.SourceId.ShouldBe("LowScore");
        result.Candidates[1].Entry.SourceId.ShouldBe("HighScore");
    }
}
