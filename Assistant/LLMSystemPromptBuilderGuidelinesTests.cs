using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class LLMSystemPromptBuilderGuidelinesTests
{
    private LLMSystemPromptBuilder _builder = null!;

    [SetUp]
    public void Setup()
    {
        _builder = new LLMSystemPromptBuilder();
    }

    private static LLMContext CreateContext(string language = "de")
    {
        return new LLMContext
        {
            UserId = "user-123",
            UserRights = new List<string> { "CanViewSettings", "CanEditSettings" },
            AvailableFunctions = new List<LLMFunction>
            {
                new() { Name = "test_func", Description = "A test function" }
            },
            Language = language
        };
    }

    [Test]
    public void BuildSystemPrompt_NullGuidelines_UsesFallbackDefaults()
    {
        var context = CreateContext("en");

        var result = _builder.BuildSystemPrompt(context, null, null, null);

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Be polite and professional");
        result.Should().Contain("Use available functions when users ask for them");
        result.Should().Contain("Always check permissions before executing functions");
    }

    [Test]
    public void BuildSystemPrompt_EmptyGuidelines_UsesFallbackDefaults()
    {
        var context = CreateContext("en");

        var result = _builder.BuildSystemPrompt(context, null, null, "   ");

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Be polite and professional");
    }

    [Test]
    public void BuildSystemPrompt_CustomGuidelines_UsesCustomContent()
    {
        var context = CreateContext("en");
        var customGuidelines = "- Always respond in bullet points\n- Never use emojis";

        var result = _builder.BuildSystemPrompt(context, null, null, customGuidelines);

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Always respond in bullet points");
        result.Should().Contain("Never use emojis");
        result.Should().NotContain("Be polite and professional");
    }

    [Test]
    public void BuildSystemPrompt_German_UsesRichtlinienHeader()
    {
        var context = CreateContext("de");
        var guidelines = "- Eigene Richtlinie";

        var result = _builder.BuildSystemPrompt(context, null, null, guidelines);

        result.Should().Contain("Richtlinien:");
        result.Should().Contain("Eigene Richtlinie");
    }

    [Test]
    public void BuildSystemPrompt_French_UsesDirectivesHeader()
    {
        var context = CreateContext("fr");
        var guidelines = "- Custom directive";

        var result = _builder.BuildSystemPrompt(context, null, null, guidelines);

        result.Should().Contain("Directives:");
        result.Should().Contain("Custom directive");
    }

    [Test]
    public void BuildSystemPrompt_Italian_UsesLineeGuidaHeader()
    {
        var context = CreateContext("it");
        var guidelines = "- Regola personalizzata";

        var result = _builder.BuildSystemPrompt(context, null, null, guidelines);

        result.Should().Contain("Linee guida:");
        result.Should().Contain("Regola personalizzata");
    }

    [Test]
    [TestCase("de", "Richtlinien")]
    [TestCase("en", "Guidelines")]
    [TestCase("fr", "Directives")]
    [TestCase("it", "Linee guida")]
    public void BuildSystemPrompt_AllLanguages_FallbackContainsDefaultRules(string language, string expectedHeader)
    {
        var context = CreateContext(language);

        var result = _builder.BuildSystemPrompt(context, null, null, null);

        result.Should().Contain($"{expectedHeader}:");
        result.Should().Contain("Be polite and professional");
        result.Should().Contain("contact an administrator");
    }

    [Test]
    public void BuildSystemPrompt_WithSoulAndGuidelines_ContainsBoth()
    {
        var context = CreateContext("en");
        var soul = "I am a helpful planning assistant.";
        var guidelines = "- Custom rule 1\n- Custom rule 2";

        var result = _builder.BuildSystemPrompt(context, soul, null, guidelines);

        result.Should().Contain("=== IDENTITY ===");
        result.Should().Contain("helpful planning assistant");
        result.Should().Contain("Guidelines:");
        result.Should().Contain("Custom rule 1");
    }

    [Test]
    public void BuildSystemPrompt_WithMemoriesAndGuidelines_ContainsBoth()
    {
        var context = CreateContext("en");
        var memories = new List<AiMemory>
        {
            new() { Category = "test", Key = "key1", Content = "value1", Importance = 5 }
        };
        var guidelines = "- Custom guideline";

        var result = _builder.BuildSystemPrompt(context, null, memories, guidelines);

        result.Should().Contain("Guidelines:");
        result.Should().Contain("Custom guideline");
        result.Should().Contain("Persistent Knowledge:");
        result.Should().Contain("key1: value1");
    }

    [Test]
    public void BuildSystemPrompt_WithAllSections_CorrectOrder()
    {
        var context = CreateContext("en");
        var soul = "I am the assistant.";
        var memories = new List<AiMemory>
        {
            new() { Category = "info", Key = "k", Content = "v", Importance = 5 }
        };
        var guidelines = "- My rule";

        var result = _builder.BuildSystemPrompt(context, soul, memories, guidelines);

        var identityIndex = result.IndexOf("=== IDENTITY ===");
        var functionsIndex = result.IndexOf("Available Functions:");
        var guidelinesIndex = result.IndexOf("Guidelines:");
        var memoryIndex = result.IndexOf("Persistent Knowledge:");

        identityIndex.Should().BeLessThan(functionsIndex);
        functionsIndex.Should().BeLessThan(guidelinesIndex);
        guidelinesIndex.Should().BeLessThan(memoryIndex);
    }

    [Test]
    public void BuildSystemPrompt_CustomGuidelines_TrimsWhitespace()
    {
        var context = CreateContext("en");
        var guidelines = "  \n  - Trimmed rule  \n  ";

        var result = _builder.BuildSystemPrompt(context, null, null, guidelines);

        result.Should().Contain("- Trimmed rule");
    }
}
