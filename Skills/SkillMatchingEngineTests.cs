// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillMatchingEngine covering keyword matching, synonym matching,
/// legacy trigger keywords, and language priority resolution.
/// </summary>
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillMatchingEngineTests
{
    [Test]
    public void MatchesKeyword_ShortKeyword_UsesWordBoundary()
    {
        SkillMatchingEngine.MatchesKeyword("configure llm settings", "llm").Should().BeTrue();
        SkillMatchingEngine.MatchesKeyword("overwhelm the system", "llm").Should().BeFalse();
    }

    [Test]
    public void MatchesKeyword_LongKeyword_UsesContains()
    {
        SkillMatchingEngine.MatchesKeyword("zeig mir die mitarbeiterliste", "mitarbeiter").Should().BeTrue();
    }

    [Test]
    public void MatchesSynonyms_GermanSynonym_ReturnsTrue()
    {
        var synonyms = new Dictionary<string, List<string>>
        {
            { "de", new List<string> { "Einstellungen", "Konfiguration" } },
            { "en", new List<string> { "settings", "configuration" } }
        };

        var result = SkillMatchingEngine.MatchesSynonyms(synonyms, "zeig mir die einstellungen", "de");

        result.Should().BeTrue();
    }

    [Test]
    public void MatchesSynonyms_FallbackToEnglish_WhenGermanNotFound()
    {
        var synonyms = new Dictionary<string, List<string>>
        {
            { "en", new List<string> { "settings", "configuration" } }
        };

        var result = SkillMatchingEngine.MatchesSynonyms(synonyms, "show me settings please", "de");

        result.Should().BeTrue();
    }

    [Test]
    public void MatchesSynonyms_NullSynonyms_ReturnsFalse()
    {
        var result = SkillMatchingEngine.MatchesSynonyms(null, "some message", "de");

        result.Should().BeFalse();
    }

    [Test]
    public void MatchesSynonyms_EmptySynonyms_ReturnsFalse()
    {
        var synonyms = new Dictionary<string, List<string>>();

        var result = SkillMatchingEngine.MatchesSynonyms(synonyms, "some message", "de");

        result.Should().BeFalse();
    }

    [Test]
    public void MatchesSkillKeywords_ViaSynonyms_ReturnsTrue()
    {
        var skill = CreateTestSkill(
            synonyms: new Dictionary<string, List<string>>
            {
                { "de", new List<string> { "Mitarbeiter anzeigen", "Personalliste" } }
            });

        var result = SkillMatchingEngine.MatchesSkillKeywords(skill, "Zeig mir die Personalliste", "de");

        result.Should().BeTrue();
    }

    [Test]
    public void MatchesSkillKeywords_NoMatch_ReturnsFalse()
    {
        var skill = CreateTestSkill(
            synonyms: new Dictionary<string, List<string>>
            {
                { "de", new List<string> { "Einstellungen" } }
            },
            triggerKeywords: "[\"settings\"]");

        var result = SkillMatchingEngine.MatchesSkillKeywords(skill, "was gibt es zum mittagessen", "de");

        result.Should().BeFalse();
    }

    [Test]
    public void MatchesLegacyTriggerKeywords_JsonArray_ReturnsTrue()
    {
        var result = SkillMatchingEngine.MatchesLegacyTriggerKeywords(
            "[\"email\", \"nachricht\"]",
            "bitte email senden");

        result.Should().BeTrue();
    }

    [Test]
    public void MatchesLegacyTriggerKeywords_EmptyString_ReturnsFalse()
    {
        SkillMatchingEngine.MatchesLegacyTriggerKeywords("", "some message").Should().BeFalse();
        SkillMatchingEngine.MatchesLegacyTriggerKeywords("[]", "some message").Should().BeFalse();
    }

    [Test]
    public void MatchesLegacyTriggerKeywords_InvalidJson_ReturnsFalse()
    {
        var result = SkillMatchingEngine.MatchesLegacyTriggerKeywords("not valid json{", "some message");

        result.Should().BeFalse();
    }

    [Test]
    public void GetLanguagePriority_German_ReturnsDeEn()
    {
        var result = SkillMatchingEngine.GetLanguagePriority("de");

        result.Should().BeEquivalentTo(new List<string> { "de", "en" }, options => options.WithStrictOrdering());
    }

    [Test]
    public void GetLanguagePriority_French_ReturnsFrEnDe()
    {
        var result = SkillMatchingEngine.GetLanguagePriority("fr");

        result.Should().BeEquivalentTo(new List<string> { "fr", "en", "de" }, options => options.WithStrictOrdering());
    }

    [Test]
    public void GetLanguagePriority_Null_DefaultsToDe()
    {
        var result = SkillMatchingEngine.GetLanguagePriority(null);

        result.Should().BeEquivalentTo(new List<string> { "de", "en" }, options => options.WithStrictOrdering());
    }

    private static AgentSkill CreateTestSkill(
        Dictionary<string, List<string>>? synonyms = null,
        string triggerKeywords = "[]")
    {
        return new AgentSkill
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            Name = "test_skill",
            Description = "A test skill",
            Synonyms = synonyms,
            TriggerKeywords = triggerKeywords
        };
    }
}
