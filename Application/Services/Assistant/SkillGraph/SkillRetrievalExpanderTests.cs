// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the skill-graph association ranking: only active relations count, in both directions and whatever
/// their type; a neighbour must be in the association pool and must not be a seed; candidates are ranked by
/// AssociationGraphWeight * (best seed score * confidence) + (1 - weight) * own score and capped to the slots.
/// SeeAlso relations count since 2026-11-14 (W3.2 had kept them out of the old free-slot expansion, which ranked by
/// confidence alone and let their 1.0 win every slot); measured on the multi-tool goldset simulation, leaving them
/// out cost the single-target sets 14 hits against keeping them.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillRetrievalExpanderTests
{
    private static SkillRelation Edge(string a, string b, double confidence,
        SkillRelationType type = SkillRelationType.CoRequired,
        SkillRelationStatus status = SkillRelationStatus.Active)
        => new() { SkillAName = a, SkillBName = b, Type = type, Status = status, Confidence = confidence };

    private static Dictionary<string, double> Scores(params (string Name, double Score)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Score, StringComparer.OrdinalIgnoreCase);

    [Test]
    public void Association_PicksAPooledNeighbourOfASeed()
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "neighbour", 0.8)], Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 3);

        result.ShouldBe(["neighbour"]);
    }

    [Test]
    public void Association_FollowsARelationInBothDirections()
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("neighbour", "seed", 0.8)], Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 3);

        result.ShouldBe(["neighbour"]);
    }

    [Test]
    public void Association_IgnoresANeighbourOutsideThePool()
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "unscored", 0.9)], Scores(("seed", 0.8)), Scores(("other", 0.5)), slots: 3);

        result.ShouldBeEmpty();
    }

    [TestCase(SkillRelationStatus.Candidate)]
    [TestCase(SkillRelationStatus.Retired)]
    public void Association_IgnoresRelationsThatAreNotActive(SkillRelationStatus status)
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "neighbour", 0.9, status: status)], Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 3);

        result.ShouldBeEmpty();
    }

    [TestCase(SkillRelationType.CoRequired)]
    [TestCase(SkillRelationType.Sequential)]
    [TestCase(SkillRelationType.ExplainedBy)]
    [TestCase(SkillRelationType.AdvisesFor)]
    [TestCase(SkillRelationType.SeeAlso)]
    public void Association_CountsEveryActiveRelationType(SkillRelationType type)
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "neighbour", 0.9, type: type)], Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 3);

        result.ShouldBe(["neighbour"]);
    }

    [Test]
    public void Association_LetsTheNeighboursOwnScoreOutweighAStrongerRelation()
    {
        // strong: 0.2 * (0.8 * 1.0) + 0.8 * 0.50 = 0.560; close: 0.2 * (0.8 * 0.5) + 0.8 * 0.62 = 0.576
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "strong", 1.0), Edge("seed", "close", 0.5)],
            Scores(("seed", 0.8)),
            Scores(("strong", 0.50), ("close", 0.62)),
            slots: 1);

        result.ShouldBe(["close"]);
    }

    [Test]
    public void Association_TakesTheBestSeedEvidence_ForANeighbourOfSeveralSeeds()
    {
        // shared: 0.2 * max(0.9 * 0.5, 0.4 * 0.5) + 0.8 * 0.5 = 0.490; single: 0.2 * (0.4 * 1.0) + 0.8 * 0.5 = 0.480
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("strongSeed", "shared", 0.5), Edge("weakSeed", "shared", 0.5), Edge("weakSeed", "single", 1.0)],
            Scores(("strongSeed", 0.9), ("weakSeed", 0.4)),
            Scores(("shared", 0.5), ("single", 0.5)),
            slots: 1);

        result.ShouldBe(["shared"]);
    }

    [Test]
    public void Association_RespectsTheSlots()
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "a", 0.9), Edge("seed", "b", 0.9), Edge("seed", "c", 0.9)],
            Scores(("seed", 0.8)),
            Scores(("a", 0.5), ("b", 0.6), ("c", 0.4)),
            slots: 2);

        result.ShouldBe(["b", "a"]);
    }

    [Test]
    public void Association_WithZeroSlots_ReturnsNothing()
    {
        var result = SkillRetrievalExpander.RankAssociations(
            [Edge("seed", "neighbour", 0.9)], Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 0);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task AssociateAsync_WithEpsilonOne_SkipsTheTurn_WithoutReadingTheGraph()
    {
        var repository = Substitute.For<ISkillRelationRepository>();
        var expander = new SkillRetrievalExpander(
            repository, Substitute.For<ILogger<SkillRetrievalExpander>>(), new SkillGraphExpansionOptions(1.0, 3));

        var result = await expander.AssociateAsync(
            Guid.NewGuid(), Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 3);

        result.ShouldBeEmpty();
        await repository.DidNotReceiveWithAnyArgs().GetByAgentAsync(default, default);
    }

    [Test]
    public async Task AssociateAsync_WithEpsilonZero_RanksTheAgentsRelations()
    {
        var agentId = Guid.NewGuid();
        var repository = Substitute.For<ISkillRelationRepository>();
        repository.GetByAgentAsync(agentId, Arg.Any<CancellationToken>())
            .Returns([Edge("seed", "neighbour", 0.8)]);
        var expander = new SkillRetrievalExpander(
            repository, Substitute.For<ILogger<SkillRetrievalExpander>>(), new SkillGraphExpansionOptions(0, 3));

        var result = await expander.AssociateAsync(
            agentId, Scores(("seed", 0.8)), Scores(("neighbour", 0.5)), slots: 3);

        result.ShouldBe(["neighbour"]);
        expander.AssociationSlots.ShouldBe(3);
    }
}
