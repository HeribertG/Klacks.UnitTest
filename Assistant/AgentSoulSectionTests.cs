using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class AgentSoulSectionTests
{
    [Test]
    public void AgentSoulSection_DefaultValues_AreCorrect()
    {
        // Act
        var section = new AgentSoulSection();

        // Assert
        section.SectionType.Should().BeEmpty();
        section.Content.Should().BeEmpty();
        section.SortOrder.Should().Be(0);
        section.IsActive.Should().BeTrue();
        section.Version.Should().Be(1);
        section.Source.Should().BeNull();
    }

    [Test]
    public void AgentSoulSection_SetProperties_ShouldWork()
    {
        // Arrange
        var agentId = Guid.NewGuid();

        // Act
        var section = new AgentSoulSection
        {
            AgentId = agentId,
            SectionType = SoulSectionTypes.Identity,
            Content = "I am a helpful assistant.",
            SortOrder = 0,
            IsActive = true,
            Version = 3,
            Source = "chat"
        };

        // Assert
        section.AgentId.Should().Be(agentId);
        section.SectionType.Should().Be("identity");
        section.Content.Should().Be("I am a helpful assistant.");
        section.SortOrder.Should().Be(0);
        section.Version.Should().Be(3);
        section.Source.Should().Be("chat");
    }

    [Test]
    public void SoulSectionTypes_AllValues_ArePopulated()
    {
        // Assert
        SoulSectionTypes.All.Should().HaveCount(10);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.Identity);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.Personality);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.Tone);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.Boundaries);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.CommunicationStyle);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.Values);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.GroupBehavior);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.UserContext);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.DomainExpertise);
        SoulSectionTypes.All.Should().Contain(SoulSectionTypes.ErrorHandling);
    }

    [Test]
    public void AgentSoulHistory_DefaultValues_AreCorrect()
    {
        // Act
        var history = new AgentSoulHistory();

        // Assert
        history.SectionType.Should().BeEmpty();
        history.ContentBefore.Should().BeNull();
        history.ContentAfter.Should().BeEmpty();
        history.Version.Should().Be(0);
        history.ChangeType.Should().Be("update");
        history.ChangedBy.Should().BeNull();
    }

    [Test]
    public void AgentSoulHistory_TracksChange()
    {
        // Arrange
        var agentId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();

        // Act
        var history = new AgentSoulHistory
        {
            AgentId = agentId,
            SoulSectionId = sectionId,
            SectionType = SoulSectionTypes.Identity,
            ContentBefore = "Old content",
            ContentAfter = "New content",
            Version = 2,
            ChangeType = "update",
            ChangedBy = "admin-user"
        };

        // Assert
        history.AgentId.Should().Be(agentId);
        history.SoulSectionId.Should().Be(sectionId);
        history.ContentBefore.Should().Be("Old content");
        history.ContentAfter.Should().Be("New content");
        history.Version.Should().Be(2);
        history.ChangeType.Should().Be("update");
    }
}
