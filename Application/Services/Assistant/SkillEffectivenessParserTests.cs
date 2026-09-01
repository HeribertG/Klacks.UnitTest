// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the W6 toolset-provenance aggregation (W1.6 candidate snapshots).
/// </summary>

using Klacks.Api.Application.Services.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillEffectivenessParserTests
{
    [Test]
    public void ResolveChosenSource_FindsSourceOfChosenCandidate()
    {
        const string json = """
        [
          { "name": "list_spam_rules", "source": "Keyword", "rank": 1, "score": 0.9 },
          { "name": "create_spam_rule", "source": "Retrieved", "rank": 2, "score": 0.7 }
        ]
        """;

        var result = SkillEffectivenessParser.ResolveChosenSource("create_spam_rule", json);

        result.ShouldBe("Retrieved");
    }

    [Test]
    public void ResolveChosenSource_ChosenSkillNotInSnapshot_ReturnsUnknown()
    {
        const string json = """[ { "name": "list_spam_rules", "source": "Keyword", "rank": 1, "score": 0.9 } ]""";

        var result = SkillEffectivenessParser.ResolveChosenSource("create_spam_rule", json);

        result.ShouldBe("Unknown");
    }

    [Test]
    public void ResolveChosenSource_InvalidJson_ReturnsUnknown()
    {
        SkillEffectivenessParser.ResolveChosenSource("create_spam_rule", "{ not json")
            .ShouldBe("Unknown");
        SkillEffectivenessParser.ResolveChosenSource("create_spam_rule", "")
            .ShouldBe("Unknown");
    }

    [Test]
    public void DistributeChosenSources_CountsPerSourceOrderedByCount()
    {
        var rows = new (string? ChosenSkill, string CandidatesJson)[]
        {
            ("create_spam_rule", """[ { "name": "create_spam_rule", "source": "Keyword" } ]"""),
            ("delete_spam_rule", """[ { "name": "delete_spam_rule", "source": "Keyword" } ]"""),
            ("list_spam_rules", """[ { "name": "list_spam_rules", "source": "Retrieved" } ]"""),
            ("missing_skill", "[]")
        };

        var result = SkillEffectivenessParser.DistributeChosenSources(rows);

        result.Count.ShouldBe(3);
        result[0].Source.ShouldBe("Keyword");
        result[0].Count.ShouldBe(2);
        result.Single(r => r.Source == "Retrieved").Count.ShouldBe(1);
        result.Single(r => r.Source == "Unknown").Count.ShouldBe(1);
    }
}
