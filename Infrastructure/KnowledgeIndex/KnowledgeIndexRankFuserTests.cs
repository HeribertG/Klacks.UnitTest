// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexRankFuserTests
{
    private const int RrfK = KnowledgeIndexConstants.ReciprocalRankFusionK;

    private static KnowledgeEntry Entry(string sourceId, KnowledgeEntryKind kind = KnowledgeEntryKind.Skill) =>
        new() { Kind = kind, SourceId = sourceId, Text = sourceId };

    [Test]
    public void Fuse_EntryInBothLists_OutranksEntryInOnlyOneList()
    {
        var inBoth = Entry("in_both");
        var semanticOnly = Entry("semantic_only");
        var lexicalOnly = Entry("lexical_only");

        var semantic = new[] { inBoth, semanticOnly };
        var lexical = new[] { inBoth, lexicalOnly };

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, lexical, RrfK, maxCandidates: 10);

        fused[0].SourceId.ShouldBe("in_both");
    }

    [Test]
    public void Fuse_EntryOnlyInLexicalList_IsAddedToResult()
    {
        var semanticOnly = Entry("semantic_only");
        var lexicalOnly = Entry("lexical_only");

        var semantic = new[] { semanticOnly };
        var lexical = new[] { lexicalOnly };

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, lexical, RrfK, maxCandidates: 10);

        fused.ShouldContain(e => e.SourceId == "lexical_only");
        fused.Count.ShouldBe(2);
    }

    [Test]
    public void Fuse_DuplicateAcrossLists_AppearsOnceInResult()
    {
        var shared = Entry("shared_skill");

        var semantic = new[] { shared };
        var lexical = new[] { shared };

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, lexical, RrfK, maxCandidates: 10);

        fused.Count.ShouldBe(1);
    }

    [Test]
    public void Fuse_SameSourceIdDifferentKind_TreatedAsDistinctEntries()
    {
        var skill = Entry("shared_name", KnowledgeEntryKind.Skill);
        var recipe = Entry("shared_name", KnowledgeEntryKind.Recipe);

        var semantic = new[] { skill };
        var lexical = new[] { recipe };

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, lexical, RrfK, maxCandidates: 10);

        fused.Count.ShouldBe(2);
    }

    [Test]
    public void Fuse_RespectsMaxCandidatesCap()
    {
        var semantic = Enumerable.Range(0, 20).Select(i => Entry($"s{i}")).ToArray();
        var lexical = Enumerable.Range(0, 20).Select(i => Entry($"l{i}")).ToArray();

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, lexical, RrfK, maxCandidates: 5);

        fused.Count.ShouldBe(5);
    }

    [Test]
    public void Fuse_HigherRankInSingleList_OutranksLowerRankInSingleList()
    {
        var first = Entry("rank1");
        var second = Entry("rank2");
        var third = Entry("rank3");

        var semantic = new[] { first, second, third };

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, [], RrfK, maxCandidates: 10);

        fused.Select(e => e.SourceId).ShouldBe(["rank1", "rank2", "rank3"]);
    }

    [Test]
    public void Fuse_BothListsEmpty_ReturnsEmpty()
    {
        var fused = KnowledgeIndexRankFuser.Fuse([], [], RrfK, maxCandidates: 10);

        fused.ShouldBeEmpty();
    }

    [Test]
    public void Fuse_ScoreMatchesReciprocalRankFusionFormula()
    {
        var a = Entry("a");
        var b = Entry("b");

        // a: rank 1 in semantic, rank 2 in lexical -> 1/(k+1) + 1/(k+2)
        // b: rank 2 in semantic, rank 1 in lexical -> 1/(k+2) + 1/(k+1)
        // Both scores are identical, so this only asserts the two remain tied at the top over an
        // unrelated third entry which must rank last with a single lower contribution.
        var c = Entry("c");
        var semantic = new[] { a, b, c };
        var lexical = new[] { b, a };

        var fused = KnowledgeIndexRankFuser.Fuse(semantic, lexical, RrfK, maxCandidates: 10);

        fused[2].SourceId.ShouldBe("c");
        fused.Take(2).Select(e => e.SourceId).ShouldBe(["a", "b"], ignoreOrder: true);
    }
}
