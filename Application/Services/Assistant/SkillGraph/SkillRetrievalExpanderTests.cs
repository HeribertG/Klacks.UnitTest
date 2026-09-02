// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the silent retrieval expansion logic: only active co-required neighbours of a
/// selected skill that are permitted get added, ranked by confidence and capped to the free slots;
/// candidate/sequential edges, non-permitted neighbours and already-selected skills are excluded.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillRetrievalExpanderTests
{
    private static AgentSkill Skill(string name) => new() { Name = name };

    private static SkillRelation Edge(string a, string b, double confidence,
        SkillRelationType type = SkillRelationType.CoRequired,
        SkillRelationStatus status = SkillRelationStatus.Active)
        => new() { SkillAName = a, SkillBName = b, Type = type, Status = status, Confidence = confidence };

    [Test]
    public void Expansion_AddsActiveCoRequiredPermittedNeighbour()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[] { Edge("aa", "bb", 0.8) },
            new[] { Skill("aa") },
            new[] { Skill("aa"), Skill("bb") },
            slots: 3);

        result.Select(s => s.Name).ShouldBe(new[] { "bb" });
    }

    /// <summary>
    /// W3.2 regression guard: the 148 curated "see also" cross-references carry confidence 1.0, above
    /// anything the learner can reach (0.95). While they were typed CoRequired they won every ranking
    /// and pushed learned co-occurrence evidence out of all three expansion slots. SeeAlso must stay
    /// retrieval-neutral — it licenses a name-drop inside a description, it is not evidence.
    /// </summary>
    [Test]
    public void Expansion_IgnoresSeeAlsoEdges_EvenAtMaximumConfidence()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[] { Edge("aa", "bb", 1.0, type: SkillRelationType.SeeAlso) },
            new[] { Skill("aa") },
            new[] { Skill("aa"), Skill("bb") },
            slots: 3);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Expansion_SeeAlsoEdge_DoesNotDisplaceLearnedCoRequiredNeighbour()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[]
            {
                Edge("aa", "bb", 1.0, type: SkillRelationType.SeeAlso),
                Edge("aa", "cc", 0.95),
            },
            new[] { Skill("aa") },
            new[] { Skill("aa"), Skill("bb"), Skill("cc") },
            slots: 1);

        result.Select(s => s.Name).ShouldBe(new[] { "cc" });
    }

    [Test]
    public void Expansion_ExcludesNonPermittedNeighbour()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[] { Edge("aa", "bb", 0.8) },
            new[] { Skill("aa") },
            new[] { Skill("aa") },
            slots: 3);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Expansion_ExcludesNonActiveAndSequentialEdges()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[]
            {
                Edge("aa", "bb", 0.9, status: SkillRelationStatus.Candidate),
                Edge("aa", "cc", 0.9, type: SkillRelationType.Sequential),
            },
            new[] { Skill("aa") },
            new[] { Skill("aa"), Skill("bb"), Skill("cc") },
            slots: 3);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Expansion_ExcludesAlreadySelectedNeighbour()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[] { Edge("aa", "bb", 0.8) },
            new[] { Skill("aa"), Skill("bb") },
            new[] { Skill("aa"), Skill("bb") },
            slots: 3);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Expansion_RanksByConfidence_AndRespectsSlots()
    {
        var result = SkillRetrievalExpander.BuildExpansion(
            new[]
            {
                Edge("aa", "bb", 0.90),
                Edge("aa", "cc", 0.70),
                Edge("aa", "dd", 0.95),
            },
            new[] { Skill("aa") },
            new[] { Skill("aa"), Skill("bb"), Skill("cc"), Skill("dd") },
            slots: 2);

        result.Select(s => s.Name).ShouldBe(new[] { "dd", "bb" });
    }
}
