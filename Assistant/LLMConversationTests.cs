using NUnit.Framework;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class LLMConversationTests
{
    [Test]
    public void LLMConversation_Constructor_ShouldInitializeProperties()
    {
        // Act
        var conversation = new LLMConversation();

        // Assert
        Assert.That(conversation.Id, Is.EqualTo(Guid.Empty));
        Assert.That(conversation.ConversationId, Is.EqualTo(string.Empty));
        Assert.That(conversation.UserId, Is.EqualTo(string.Empty));
        Assert.That(conversation.Title, Is.Null);
        Assert.That(conversation.Summary, Is.Null);
        Assert.That(conversation.MessageCount, Is.EqualTo(0));
        Assert.That(conversation.TotalTokens, Is.EqualTo(0));
        Assert.That(conversation.TotalCost, Is.EqualTo(0));
        Assert.That(conversation.IsArchived, Is.False);
        Assert.That(conversation.LastModelId, Is.Null);
    }

    [Test]
    public void LLMConversation_SetProperties_ShouldWork()
    {
        // Arrange
        var conversation = new LLMConversation();
        var conversationId = Guid.NewGuid();
        var lastMessageAt = DateTime.UtcNow;

        // Act
        conversation.Id = conversationId;
        conversation.ConversationId = "conv-123";
        conversation.UserId = "user-456";
        conversation.Title = "Test Conversation";
        conversation.Summary = "A test conversation about AI";
        conversation.LastMessageAt = lastMessageAt;
        conversation.MessageCount = 5;
        conversation.TotalTokens = 1000;
        conversation.TotalCost = 0.25m;
        conversation.LastModelId = "gpt-4";
        conversation.IsArchived = false;

        // Assert
        Assert.That(conversation.Id, Is.EqualTo(conversationId));
        Assert.That(conversation.ConversationId, Is.EqualTo("conv-123"));
        Assert.That(conversation.UserId, Is.EqualTo("user-456"));
        Assert.That(conversation.Title, Is.EqualTo("Test Conversation"));
        Assert.That(conversation.Summary, Is.EqualTo("A test conversation about AI"));
        Assert.That(conversation.LastMessageAt, Is.EqualTo(lastMessageAt));
        Assert.That(conversation.MessageCount, Is.EqualTo(5));
        Assert.That(conversation.TotalTokens, Is.EqualTo(1000));
        Assert.That(conversation.TotalCost, Is.EqualTo(0.25m));
        Assert.That(conversation.LastModelId, Is.EqualTo("gpt-4"));
        Assert.That(conversation.IsArchived, Is.False);
    }

    [Test]
    public void LLMConversation_Messages_ShouldInitializeAsEmpty()
    {
        // Act
        var conversation = new LLMConversation();

        // Assert
        Assert.That(conversation.Messages, Is.Not.Null);
        Assert.That(conversation.Messages, Is.Empty);
    }
}