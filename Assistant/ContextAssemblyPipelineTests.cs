using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class ContextAssemblyPipelineTests
{
    private IAgentSoulRepository _soulRepository = null!;
    private IAgentMemoryRepository _memoryRepository = null!;
    private IAgentSkillRepository _skillRepository = null!;
    private IGlobalAgentRuleRepository _globalRuleRepository = null!;
    private IEmbeddingService _embeddingService = null!;
    private IConfiguration _configuration = null!;
    private ILogger<ContextAssemblyPipeline> _logger = null!;
    private ContextAssemblyPipeline _pipeline = null!;

    private static readonly Guid TestAgentId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _soulRepository = Substitute.For<IAgentSoulRepository>();
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _globalRuleRepository = Substitute.For<IGlobalAgentRuleRepository>();
        _embeddingService = Substitute.For<IEmbeddingService>();
        _logger = Substitute.For<ILogger<ContextAssemblyPipeline>>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Languages:Metadata:de:Name"] = "German",
                ["Languages:Metadata:de:DisplayName"] = "Deutsch",
                ["Languages:Metadata:en:Name"] = "English",
                ["Languages:Metadata:en:DisplayName"] = "English",
                ["Languages:Metadata:fr:Name"] = "French",
                ["Languages:Metadata:fr:DisplayName"] = "Français",
                ["Languages:Metadata:it:Name"] = "Italian",
                ["Languages:Metadata:it:DisplayName"] = "Italiano",
            })
            .Build();

        _embeddingService.IsAvailable.Returns(false);

        _globalRuleRepository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>());
        _soulRepository.GetActiveSectionsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSoulSection>());
        _memoryRepository.GetPinnedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
        _memoryRepository.HybridSearchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult>());

        _pipeline = new ContextAssemblyPipeline(
            _soulRepository, _memoryRepository, _skillRepository,
            _globalRuleRepository, _embeddingService, _configuration, _logger);
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoSectionsNoMemories_ReturnsEmptyPrompt()
    {
        // Act
        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test message", "de");

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithSoulSections_IncludesIdentityBlock()
    {
        // Arrange
        var sections = new List<AgentSoulSection>
        {
            new() { SectionType = SoulSectionTypes.Identity, Content = "I am a helpful assistant.", SortOrder = 0 },
            new() { SectionType = SoulSectionTypes.Personality, Content = "Friendly and professional.", SortOrder = 1 }
        };
        _soulRepository.GetActiveSectionsAsync(TestAgentId, Arg.Any<CancellationToken>())
            .Returns(sections);

        // Act
        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "hello", "de");

        // Assert
        result.Should().Contain("=== IDENTITY ===");
        result.Should().Contain("[IDENTITY]");
        result.Should().Contain("I am a helpful assistant.");
        result.Should().Contain("[PERSONALITY]");
        result.Should().Contain("Friendly and professional.");
        result.Should().Contain("================");
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_SoulSectionsAreSorted()
    {
        // Arrange
        var sections = new List<AgentSoulSection>
        {
            new() { SectionType = SoulSectionTypes.Identity, Content = "Assistant.", SortOrder = 0 },
            new() { SectionType = SoulSectionTypes.Tone, Content = "Formal.", SortOrder = 2 }
        };
        _soulRepository.GetActiveSectionsAsync(TestAgentId, Arg.Any<CancellationToken>())
            .Returns(sections);

        // Act
        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        // Assert
        var identityIndex = result.IndexOf("[IDENTITY]");
        var toneIndex = result.IndexOf("[TONE]");
        identityIndex.Should().BeGreaterThanOrEqualTo(0);
        toneIndex.Should().BeGreaterThan(identityIndex);
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithPinnedMemories_IncludesPinnedBlock()
    {
        // Arrange
        var pinned = new List<AgentMemory>
        {
            new()
            {
                Id = Guid.NewGuid(), AgentId = TestAgentId,
                Category = MemoryCategories.Fact, Key = "company_name",
                Content = "Acme Corp", Importance = 10, IsPinned = true
            }
        };
        _memoryRepository.GetPinnedAsync(TestAgentId, Arg.Any<CancellationToken>())
            .Returns(pinned);

        // Act
        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        // Assert
        result.Should().Contain("=== PERSISTENT KNOWLEDGE ===");
        result.Should().Contain("[PINNED]");
        result.Should().Contain("company_name: Acme Corp");
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithSearchResults_IncludesRelevantBlock()
    {
        // Arrange
        var searchResults = new List<MemorySearchResult>
        {
            new(Guid.NewGuid(), "User prefers dark mode", "ui_preference", MemoryCategories.Preference, 7, 0.85f, false)
        };
        _memoryRepository.HybridSearchAsync(TestAgentId, "test", null, 15, Arg.Any<CancellationToken>())
            .Returns(searchResults);

        // Act
        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        // Assert
        result.Should().Contain("[RELEVANT]");
        result.Should().Contain("ui_preference: User prefers dark mode");
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithEmbeddingAvailable_GeneratesEmbedding()
    {
        // Arrange
        _embeddingService.IsAvailable.Returns(true);
        var fakeEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        _embeddingService.GenerateEmbeddingAsync("what is the project?", Arg.Any<CancellationToken>())
            .Returns(fakeEmbedding);

        // Act
        await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "what is the project?", "de");

        // Assert
        await _memoryRepository.Received(1).HybridSearchAsync(
            TestAgentId, "what is the project?", fakeEmbedding, 15, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithoutEmbeddingAvailable_PassesNullEmbedding()
    {
        // Arrange
        _embeddingService.IsAvailable.Returns(false);

        // Act
        await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        // Assert
        await _memoryRepository.Received(1).HybridSearchAsync(
            TestAgentId, "test", null, 15, Arg.Any<CancellationToken>());
        await _embeddingService.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithSoulAndMemories_CorrectOrder()
    {
        // Arrange
        var sections = new List<AgentSoulSection>
        {
            new() { SectionType = SoulSectionTypes.Identity, Content = "Assistant.", SortOrder = 0 }
        };
        _soulRepository.GetActiveSectionsAsync(TestAgentId, Arg.Any<CancellationToken>())
            .Returns(sections);

        var pinned = new List<AgentMemory>
        {
            new()
            {
                Id = Guid.NewGuid(), AgentId = TestAgentId,
                Category = "fact", Key = "key", Content = "value",
                Importance = 5, IsPinned = true
            }
        };
        _memoryRepository.GetPinnedAsync(TestAgentId, Arg.Any<CancellationToken>())
            .Returns(pinned);

        // Act
        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        // Assert
        var identityIndex = result.IndexOf("=== IDENTITY ===");
        var knowledgeIndex = result.IndexOf("=== PERSISTENT KNOWLEDGE ===");
        identityIndex.Should().BeLessThan(knowledgeIndex);
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_UpdatesAccessCounts_ForSearchResults()
    {
        // Arrange
        var memoryId = Guid.NewGuid();
        var searchResults = new List<MemorySearchResult>
        {
            new(memoryId, "content", "key", "fact", 5, 0.8f, false)
        };
        _memoryRepository.HybridSearchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(searchResults);

        // Act
        await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        // Assert
        await Task.Delay(100);
        await _memoryRepository.Received(1).UpdateAccessCountsAsync(
            Arg.Is<List<Guid>>(ids => ids.Contains(memoryId)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithGlobalRules_ResolvesTemplateVariables()
    {
        var rules = new List<GlobalAgentRule>
        {
            new() { Name = "RESPONSE_LANGUAGE", Content = "Always respond in {{LANGUAGE}}.", SortOrder = 0, IsActive = true, Version = 1 }
        };
        _globalRuleRepository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(rules);

        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "de");

        result.Should().Contain("German (Deutsch)");
        result.Should().NotContain("{{LANGUAGE}}");
        result.Should().NotContain("{{LANGUAGE_CODE}}");
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithEnglishLanguage_ResolvesCorrectly()
    {
        var rules = new List<GlobalAgentRule>
        {
            new() { Name = "LANG", Content = "Respond in {{LANGUAGE}} ({{LANGUAGE_CODE}}).", SortOrder = 0, IsActive = true, Version = 1 }
        };
        _globalRuleRepository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(rules);

        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "en");

        result.Should().Contain("English (English)");
        result.Should().Contain("(en)");
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_WithUnknownLanguage_UsesCodeAsFallback()
    {
        var rules = new List<GlobalAgentRule>
        {
            new() { Name = "LANG", Content = "Respond in {{LANGUAGE}}.", SortOrder = 0, IsActive = true, Version = 1 }
        };
        _globalRuleRepository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(rules);

        var result = await _pipeline.AssembleSoulAndMemoryPromptAsync(TestAgentId, "test", "ja");

        result.Should().Contain("ja");
        result.Should().NotContain("{{LANGUAGE}}");
    }

    [Test]
    public void EstimateTokens_EmptyString_ReturnsZero()
    {
        // Act
        var result = _pipeline.EstimateTokens("");

        // Assert
        result.Should().Be(0);
    }

    [Test]
    public void EstimateTokens_NullString_ReturnsZero()
    {
        // Act
        var result = _pipeline.EstimateTokens(null!);

        // Assert
        result.Should().Be(0);
    }

    [Test]
    public void EstimateTokens_ShortText_ReturnsApproximate()
    {
        // Act
        var result = _pipeline.EstimateTokens("Hello world, this is a test.");

        // Assert
        result.Should().BeGreaterThan(0);
        result.Should().Be("Hello world, this is a test.".Length / 4);
    }
}
