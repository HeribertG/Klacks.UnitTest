// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Name-candidate extraction and strict claim-side matching: only adjacent capitalized pairs
/// become candidates (overlapping, so a title word cannot eat the real pair), single capitalized
/// words and caseless scripts yield nothing, the cap holds, and coverage tolerates exactly one
/// edit from token length four upwards with no phonetic path.
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Grounding;

[TestFixture]
public class AnswerNameCandidateExtractorTests
{
    [Test]
    public void AdjacentCapitalizedPair_IsACandidate()
    {
        var candidates = AnswerNameCandidateExtractor.Extract("Die Schicht von Anna Meier wurde angepasst.");

        candidates.ShouldContain("Anna Meier");
    }

    [Test]
    public void OverlappingPairs_AreAllExtracted()
    {
        var candidates = AnswerNameCandidateExtractor.Extract("Herr Peter Meier hat zugesagt.");

        candidates.ShouldContain("Herr Peter");
        candidates.ShouldContain("Peter Meier");
    }

    [Test]
    public void SingleCapitalizedWords_AreNoCandidates()
    {
        var candidates = AnswerNameCandidateExtractor.Extract("Heute ist die Planung fertig und morgen folgt der Export.");

        candidates.ShouldBeEmpty();
    }

    [Test]
    public void CaselessScripts_YieldNoCandidates()
    {
        AnswerNameCandidateExtractor.Extract("山田太郎 は本日勤務します。").ShouldBeEmpty();
        AnswerNameCandidateExtractor.Extract("สมชาย ใจดี ทำงานวันนี้").ShouldBeEmpty();
    }

    [Test]
    public void AllCapsAndDigits_AreNoCandidates()
    {
        AnswerNameCandidateExtractor.Extract("Der CSV EXPORT und Plan B2 laufen.").ShouldBeEmpty();
    }

    [Test]
    public void DuplicatesCollapse_AndTheCapHolds()
    {
        var many = string.Join(" und ", Enumerable.Range(0, 30).Select(i => $"Vorname{ToWord(i)} Nachname{ToWord(i)}"));
        var candidates = AnswerNameCandidateExtractor.Extract($"Anna Meier, nochmals Anna Meier und {many}.");

        candidates.Count(c => c == "Anna Meier").ShouldBe(1);
        candidates.Count.ShouldBe(AnswerNameCandidateExtractor.CandidateCap);
    }

    private static string ToWord(int i) => new((char)('a' + i / 5), 1 + i % 5);

    [Test]
    public void StrictEquals_ExactAndSingleEditFromLengthFour()
    {
        AnswerNameMatching.StrictEquals("meier", "meier").ShouldBeTrue();
        AnswerNameMatching.StrictEquals("meier", "mayer").ShouldBeFalse();
        AnswerNameMatching.StrictEquals("meier", "meyer").ShouldBeTrue();
        AnswerNameMatching.StrictEquals("ito", "ita").ShouldBeFalse();
        AnswerNameMatching.StrictEquals("anna", "anne").ShouldBeTrue();
    }

    [Test]
    public void Normalize_StripsAccentsAndCase()
    {
        AnswerNameMatching.Normalize("Müller-Lüdenscheid").ShouldBe("muller ludenscheid");
        AnswerNameMatching.Normalize("  José ").ShouldBe("jose");
    }

    [Test]
    public void PoolCoversName_ViaCorpusTokens_WithStrictTolerance()
    {
        var pool = new GroundingPool { TextCorpus = "schichtplan für anna meier am standort west" };

        pool.CoversName("Anna Meier").ShouldBeTrue();
        pool.CoversName("Anne Meyer").ShouldBeTrue();
        pool.CoversName("Anna Huber").ShouldBeFalse();
        pool.CoversName("Petra Meier").ShouldBeFalse();
    }
}
