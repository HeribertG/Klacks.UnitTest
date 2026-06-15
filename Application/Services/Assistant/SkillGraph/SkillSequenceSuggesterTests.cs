// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the proactive successor selection: only active sequential edges from the just
/// executed skill qualify, the highest-confidence successor wins, already-suggested and
/// candidate/co-required edges are excluded.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillSequenceSuggesterTests
{
    private static SkillRelation Seq(string a, string b, double confidence,
        SkillRelationStatus status = SkillRelationStatus.Active)
        => new() { SkillAName = a, SkillBName = b, Type = SkillRelationType.Sequential, Status = status, Confidence = confidence };

    [Test]
    public void Suggests_HighestConfidenceActiveSuccessor()
    {
        var successor = SkillSequenceSuggester.SelectSuccessor(
            new[] { Seq("aa", "bb", 0.82), Seq("aa", "cc", 0.91) },
            "aa", Array.Empty<string>());

        successor.ShouldBe("cc");
    }

    [Test]
    public void Excludes_AlreadySuggested()
    {
        var successor = SkillSequenceSuggester.SelectSuccessor(
            new[] { Seq("aa", "cc", 0.91), Seq("aa", "bb", 0.82) },
            "aa", new[] { "cc" });

        successor.ShouldBe("bb");
    }

    [Test]
    public void Ignores_CandidateAndCoRequiredAndOtherSources()
    {
        var successor = SkillSequenceSuggester.SelectSuccessor(
            new[]
            {
                Seq("aa", "bb", 0.9, SkillRelationStatus.Candidate),
                new SkillRelation { SkillAName = "aa", SkillBName = "dd", Type = SkillRelationType.CoRequired, Status = SkillRelationStatus.Active, Confidence = 0.95 },
                Seq("xx", "yy", 0.95),
            },
            "aa", Array.Empty<string>());

        successor.ShouldBeNull();
    }
}
