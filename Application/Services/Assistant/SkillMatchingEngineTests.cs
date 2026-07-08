// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillMatchingEngine.TopKeywordMatchedSkillNames — the deterministic keyword
/// guarantee feeding both skill-selection pipelines. Verifies keyword and synonym matching across
/// languages, the minimum-length noise guard, longest-match-first ranking and the guarantee cap.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillMatchingEngineTests
{
    private static AgentSkill Skill(string name, string[]? keywords = null, Dictionary<string, List<string>>? synonyms = null)
    {
        return new AgentSkill
        {
            Name = name,
            TriggerKeywords = keywords == null ? "[]" : System.Text.Json.JsonSerializer.Serialize(keywords),
            Synonyms = synonyms
        };
    }

    [Test]
    public void TopKeywordMatchedSkillNames_MatchesTriggerKeyword_CaseInsensitive()
    {
        var skills = new[] { Skill("create_employee", ["mitarbeiter erstellen"]) };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "Bitte MITARBEITER ERSTELLEN für Team Ost");

        result.ShouldBe(["create_employee"]);
    }

    [Test]
    public void TopKeywordMatchedSkillNames_MatchesSynonym_InAnyLanguage()
    {
        var skills = new[]
        {
            Skill("show_schedule", synonyms: new Dictionary<string, List<string>>
            {
                ["fr"] = ["horaire de travail"],
                ["de"] = ["dienstplan"]
            })
        };

        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "montre-moi l'horaire de travail")
            .ShouldBe(["show_schedule"]);
        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "zeig mir den Dienstplan")
            .ShouldBe(["show_schedule"]);
    }

    [Test]
    public void TopKeywordMatchedSkillNames_IgnoresKeywordsShorterThanFourChars()
    {
        var skills = new[] { Skill("noisy_skill", ["ein", "ab"]) };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "ein Termin ab morgen");

        result.ShouldBeEmpty();
    }

    [Test]
    public void TopKeywordMatchedSkillNames_RanksLongestMatchFirst_AndAppliesCap()
    {
        var skills = new[]
        {
            Skill("skill_a", ["plan"]),
            Skill("skill_b", ["planung"]),
            Skill("skill_c", ["planungsansicht"]),
            Skill("skill_d", ["plan"]),
            Skill("skill_e", ["plan"]),
            Skill("skill_f", ["plan"]),
            Skill("skill_g", ["plan"])
        };

        var result = SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "öffne die planungsansicht", cap: 3);

        result.Count.ShouldBe(3);
        result[0].ShouldBe("skill_c");
        result[1].ShouldBe("skill_b");
    }

    [Test]
    public void TopKeywordMatchedSkillNames_EmptyMessage_ReturnsEmpty()
    {
        var skills = new[] { Skill("any_skill", ["irgendwas"]) };

        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "  ").ShouldBeEmpty();
    }

    [Test]
    public void TopKeywordMatchedSkillNames_NoMatch_ReturnsEmpty()
    {
        var skills = new[] { Skill("create_employee", ["mitarbeiter erstellen"]) };

        SkillMatchingEngine.TopKeywordMatchedSkillNames(skills, "wie ist das Wetter heute?").ShouldBeEmpty();
    }
}
