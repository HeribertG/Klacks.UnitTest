// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The place-keeping of the skill-graph association: seeds are the best-scored retrieved skills whatever order they
/// arrive in, eligible candidates exclude seeds and always-on skills, the pool keeps only eligible candidates, and the
/// merge hands every unused open place back to the next retrieved skill.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Klacks.Api.KnowledgeIndex.Domain;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillAssociationPlacementTests
{
    private static AgentSkill Skill(string name, bool alwaysOn = false) => new() { Name = name, AlwaysOn = alwaysOn };

    private static RetrievalCandidate Pooled(string name, double score) =>
        new(new KnowledgeEntry { SourceId = name, Kind = KnowledgeEntryKind.Skill }, score);

    [Test]
    public void SplitSeeds_RanksByScore_NotByTheOrderTheSkillsArriveIn()
    {
        var scores = new Dictionary<string, double> { ["low"] = 0.2, ["high"] = 0.9, ["mid"] = 0.5 };

        var (ranked, seeds, seedScores) = SkillAssociationPlacement.SplitSeeds(
            [Skill("low"), Skill("high"), Skill("mid")], scores, slots: 1);

        ranked.Select(s => s.Name).ShouldBe(["high", "mid", "low"]);
        seeds.Select(s => s.Name).ShouldBe(["high", "mid"]);
        seedScores.Keys.ShouldBe(["high", "mid"], ignoreOrder: true);
    }

    [Test]
    public void SplitSeeds_ToleratesADuplicateName()
    {
        var scores = new Dictionary<string, double> { ["dup"] = 0.9, ["other"] = 0.5 };

        var (_, seeds, seedScores) = SkillAssociationPlacement.SplitSeeds(
            [Skill("dup"), Skill("dup"), Skill("other")], scores, slots: 1);

        seeds.Count.ShouldBe(2);
        seedScores["dup"].ShouldBe(0.9);
    }

    [Test]
    public void EligibleSkills_ExcludeSeedsAndAlwaysOnSkills()
    {
        var eligible = SkillAssociationPlacement.EligibleSkills(
            [Skill("seed"), Skill("always", alwaysOn: true), Skill("free")], [Skill("seed")]);

        eligible.Keys.ShouldBe(["free"]);
    }

    [Test]
    public void PoolScores_KeepOnlyEligibleCandidates_FirstOccurrenceWins()
    {
        var eligible = SkillAssociationPlacement.EligibleSkills([Skill("free")], []);

        var pool = SkillAssociationPlacement.PoolScores(
            [Pooled("free", 0.6), Pooled("free", 0.1), Pooled("unknown", 0.9)], eligible);

        pool.ShouldBe(new Dictionary<string, double> { ["free"] = 0.6 });
    }

    [Test]
    public void Merge_PutsPicksAfterTheSeeds_AndGivesUnusedPlacesBackToTheNextRetrievedSkill()
    {
        var ranked = new List<AgentSkill> { Skill("a"), Skill("b"), Skill("c"), Skill("d") };
        var seeds = ranked.Take(2).ToList();

        var merged = SkillAssociationPlacement.Merge(ranked, seeds, [Skill("graph")]);

        merged.Select(s => s.Name).ShouldBe(["a", "b", "graph", "c"]);
    }

    [Test]
    public void Merge_APickThatWasRetrievedAnyway_IsNotCountedTwice()
    {
        var ranked = new List<AgentSkill> { Skill("a"), Skill("b"), Skill("c"), Skill("d") };
        var seeds = ranked.Take(2).ToList();

        var merged = SkillAssociationPlacement.Merge(ranked, seeds, [Skill("d")]);

        merged.Select(s => s.Name).ShouldBe(["a", "b", "d", "c"]);
    }
}
