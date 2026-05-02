// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Constants;
using Klacks.Api.Infrastructure.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
[Category("SlowModelLoad")]
public class OnnxRerankerProviderTests
{
    private static string CacheDir =>
        Path.Combine(Path.GetTempPath(), "klacks-test-models", KnowledgeIndexConstants.RerankerModelName);

    [Test]
    public async Task ScoreAsync_RanksExactMatchAboveUnrelated()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxRerankerProvider(loader, CacheDir);

        var scores = await provider.ScoreAsync(
            "list open shifts",
            ["Returns all open shifts for a client.", "Deletes a user account."],
            CancellationToken.None);

        scores.Count().ShouldBe(2);
        scores[0].ShouldBeGreaterThan(scores[1]);
    }

    [Test]
    public async Task ScoreAsync_ScoresAreBetweenZeroAndOne()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxRerankerProvider(loader, CacheDir);

        var scores = await provider.ScoreAsync(
            "create employee",
            ["Creates a new employee.", "Lists all branches.", "Deletes a shift."],
            CancellationToken.None);

        scores.Count().ShouldBe(3);
        foreach (var score in scores)
        {
            score.ShouldBeGreaterThanOrEqualTo(0.0);
            score.ShouldBeLessThanOrEqualTo(1.0);
        }
    }

    [Test]
    public async Task ScoreAsync_EmptyCandidates_ReturnsEmptyArray()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxRerankerProvider(loader, CacheDir);

        var scores = await provider.ScoreAsync("test query", [], CancellationToken.None);

        scores.ShouldBeEmpty();
    }

    [Test]
    public async Task ScoreAsync_GermanQuery_RanksCorrectly()
    {
        var loader = new ModelLoader(new HttpClient());
        await using var provider = new OnnxRerankerProvider(loader, CacheDir);

        var scores = await provider.ScoreAsync(
            "zeige offene Schichten",
            ["Returns all open shifts for a client.", "Deletes a user account."],
            CancellationToken.None);

        scores.Count().ShouldBe(2);
        scores[0].ShouldBeGreaterThan(scores[1]);
    }
}
