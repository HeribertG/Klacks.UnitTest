using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class AgentMemoryTests
{
    [Test]
    public void AgentMemory_DefaultValues_AreCorrect()
    {
        // Act
        var memory = new AgentMemory();

        // Assert
        memory.Category.Should().BeEmpty();
        memory.Key.Should().BeEmpty();
        memory.Content.Should().BeEmpty();
        memory.Importance.Should().Be(5);
        memory.Embedding.Should().BeNull();
        memory.IsPinned.Should().BeFalse();
        memory.ExpiresAt.Should().BeNull();
        memory.SupersedesId.Should().BeNull();
        memory.AccessCount.Should().Be(0);
        memory.LastAccessedAt.Should().BeNull();
        memory.Source.Should().Be("conversation");
        memory.Metadata.Should().Be("{}");
    }

    [Test]
    public void AgentMemory_SetProperties_ShouldWork()
    {
        // Arrange
        var agentId = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(7);

        // Act
        var memory = new AgentMemory
        {
            AgentId = agentId,
            Category = MemoryCategories.Fact,
            Key = "company_address",
            Content = "123 Main Street",
            Importance = 8,
            IsPinned = true,
            ExpiresAt = expiresAt,
            Source = MemorySources.UserExplicit,
            AccessCount = 5,
            LastAccessedAt = DateTime.UtcNow
        };

        // Assert
        memory.AgentId.Should().Be(agentId);
        memory.Category.Should().Be("fact");
        memory.Key.Should().Be("company_address");
        memory.Content.Should().Be("123 Main Street");
        memory.Importance.Should().Be(8);
        memory.IsPinned.Should().BeTrue();
        memory.ExpiresAt.Should().Be(expiresAt);
        memory.Source.Should().Be("user_explicit");
    }

    [Test]
    public void AgentMemory_WithEmbedding_StoresFloatArray()
    {
        // Arrange
        var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };

        // Act
        var memory = new AgentMemory
        {
            Embedding = embedding
        };

        // Assert
        memory.Embedding.Should().NotBeNull();
        memory.Embedding.Should().HaveCount(5);
        memory.Embedding![0].Should().BeApproximately(0.1f, 0.001f);
    }

    [Test]
    public void AgentMemory_WithSupersedes_TracksReplacement()
    {
        // Arrange
        var originalId = Guid.NewGuid();

        // Act
        var correction = new AgentMemory
        {
            Category = MemoryCategories.Correction,
            Key = "ceo_name",
            Content = "Jane Smith",
            SupersedesId = originalId,
            Source = MemorySources.CorrectionSource
        };

        // Assert
        correction.SupersedesId.Should().Be(originalId);
        correction.Source.Should().Be("correction");
    }

    [Test]
    public void AgentMemory_Tags_InitializedAsEmpty()
    {
        // Act
        var memory = new AgentMemory();

        // Assert
        memory.Tags.Should().NotBeNull();
        memory.Tags.Should().BeEmpty();
    }

    [Test]
    public void MemoryCategories_AllConstants_HaveValues()
    {
        // Assert
        MemoryCategories.Fact.Should().Be("fact");
        MemoryCategories.Preference.Should().Be("preference");
        MemoryCategories.Decision.Should().Be("decision");
        MemoryCategories.InteractionSummary.Should().Be("interaction_summary");
        MemoryCategories.UserInfo.Should().Be("user_info");
        MemoryCategories.ProjectContext.Should().Be("project_context");
        MemoryCategories.LearnedBehavior.Should().Be("learned_behavior");
        MemoryCategories.Correction.Should().Be("correction");
        MemoryCategories.Temporal.Should().Be("temporal");
    }

    [Test]
    public void MemorySources_AllConstants_HaveValues()
    {
        // Assert
        MemorySources.Conversation.Should().Be("conversation");
        MemorySources.UserExplicit.Should().Be("user_explicit");
        MemorySources.AgentSelf.Should().Be("agent_self");
        MemorySources.CompactionFlush.Should().Be("compaction_flush");
        MemorySources.SystemImport.Should().Be("system_import");
        MemorySources.SkillOutput.Should().Be("skill_output");
        MemorySources.CorrectionSource.Should().Be("correction");
    }

    [Test]
    public void AgentMemoryTag_Properties_Work()
    {
        // Arrange
        var memoryId = Guid.NewGuid();

        // Act
        var tag = new AgentMemoryTag
        {
            MemoryId = memoryId,
            Tag = "project-x"
        };

        // Assert
        tag.MemoryId.Should().Be(memoryId);
        tag.Tag.Should().Be("project-x");
    }
}
