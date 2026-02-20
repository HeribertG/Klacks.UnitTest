using NUnit.Framework;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class LLMMessageTests
{
    [Test]
    public void LLMMessage_Constructor_ShouldInitializeProperties()
    {
        // Act
        var message = new LLMMessage();

        // Assert
        Assert.That(message.Id, Is.EqualTo(Guid.Empty));
        Assert.That(message.Role, Is.EqualTo(string.Empty));
        Assert.That(message.Content, Is.EqualTo(string.Empty));
        Assert.That(message.ConversationId, Is.EqualTo(Guid.Empty));
        Assert.That(message.TokenCount, Is.Null);
        Assert.That(message.ModelId, Is.Null);
        Assert.That(message.FunctionCalls, Is.Null);
    }

    [Test]
    public void LLMMessage_SetProperties_ShouldWork()
    {
        // Arrange
        var message = new LLMMessage();
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        // Act
        message.Id = messageId;
        message.Role = "user";
        message.Content = "Hello, how are you?";
        message.ConversationId = conversationId;
        message.TokenCount = 20;
        message.ModelId = "gpt-4";
        message.FunctionCalls = "{\"functions\": []}";

        // Assert
        Assert.That(message.Id, Is.EqualTo(messageId));
        Assert.That(message.Role, Is.EqualTo("user"));
        Assert.That(message.Content, Is.EqualTo("Hello, how are you?"));
        Assert.That(message.ConversationId, Is.EqualTo(conversationId));
        Assert.That(message.TokenCount, Is.EqualTo(20));
        Assert.That(message.ModelId, Is.EqualTo("gpt-4"));
        Assert.That(message.FunctionCalls, Is.EqualTo("{\"functions\": []}"));
    }

    [Test]
    public void LLMMessage_AssistantRole_ShouldWork()
    {
        // Arrange & Act
        var message = new LLMMessage
        {
            Role = "assistant",
            Content = "I'm doing well, thank you for asking!",
            TokenCount = 35
        };

        // Assert
        Assert.That(message.Role, Is.EqualTo("assistant"));
        Assert.That(message.Content, Is.EqualTo("I'm doing well, thank you for asking!"));
        Assert.That(message.TokenCount, Is.EqualTo(35));
    }
}