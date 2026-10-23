// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the knowledge-index adapter behind the Domain port IRecipeCandidateRetriever: it asks for recipes only, with no
/// permissions, no admin bypass and no route boost, passes message, topK and the cancellation token through unchanged,
/// and maps every candidate to its source id and score in the order the index ranked them.
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class KnowledgeIndexRecipeCandidateRetrieverTests
{
    private const string Message = "neuen Mitarbeiter anlegen";
    private const int TopK = 3;

    [Test]
    public async Task RetrieveAsync_AsksForRecipesOnly_WithoutPermissionsOrRouteBoost()
    {
        var retrieval = Substitute.For<IKnowledgeRetrievalService>();
        retrieval.RetrieveAsync(default!, default!, default, default, default, default, default)
            .ReturnsForAnyArgs(new RetrievalResult([]));
        using var cancellation = new CancellationTokenSource();

        await new KnowledgeIndexRecipeCandidateRetriever(retrieval).RetrieveAsync(Message, TopK, cancellation.Token);

        await retrieval.Received(1).RetrieveAsync(
            Message,
            Arg.Is<IReadOnlyCollection<string>>(permissions => permissions.Count == 0),
            false,
            TopK,
            null,
            cancellation.Token,
            KnowledgeEntryKind.Recipe);
    }

    [Test]
    public async Task RetrieveAsync_MapsSourceIdAndScore_InRankOrder()
    {
        var retrieval = Substitute.For<IKnowledgeRetrievalService>();
        retrieval.RetrieveAsync(default!, default!, default, default, default, default, default)
            .ReturnsForAnyArgs(new RetrievalResult(
            [
                Candidate("onboard-employee", 0.81),
                Candidate("create-group", 0.42),
            ]));

        var candidates = await new KnowledgeIndexRecipeCandidateRetriever(retrieval)
            .RetrieveAsync(Message, TopK, CancellationToken.None);

        candidates.ShouldBe(
        [
            new RecipeCandidate("onboard-employee", 0.81),
            new RecipeCandidate("create-group", 0.42),
        ]);
    }

    private static RetrievalCandidate Candidate(string sourceId, double score) =>
        new(new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = sourceId, Text = "irrelevant" }, score);
}
