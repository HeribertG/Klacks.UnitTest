using NUnit.Framework;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class LLMUsageTests
{
    [Test]
    public void LLMUsage_Constructor_ShouldInitializeProperties()
    {
        // Act
        var usage = new LLMUsage();

        // Assert
        Assert.That(usage.Id, Is.EqualTo(Guid.Empty));
        Assert.That(usage.InputTokens, Is.EqualTo(0));
        Assert.That(usage.OutputTokens, Is.EqualTo(0));
        Assert.That(usage.TotalTokens, Is.EqualTo(0));
        Assert.That(usage.Cost, Is.EqualTo(0));
        Assert.That(usage.UserId, Is.EqualTo(string.Empty));
        Assert.That(usage.ConversationId, Is.EqualTo(string.Empty));
        Assert.That(usage.ModelId, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void LLMUsage_SetTokens_ShouldCalculateTotalTokens()
    {
        // Arrange & Act
        var usage = new LLMUsage
        {
            InputTokens = 100,
            OutputTokens = 150
        };

        // Assert
        Assert.That(usage.InputTokens, Is.EqualTo(100));
        Assert.That(usage.OutputTokens, Is.EqualTo(150));
        Assert.That(usage.TotalTokens, Is.EqualTo(250));
    }

    [Test]
    public void LLMUsage_SetCost_ShouldWork()
    {
        // Arrange
        var usage = new LLMUsage();

        // Act
        usage.Cost = 0.05m;

        // Assert
        Assert.That(usage.Cost, Is.EqualTo(0.05m));
    }

    [Test]
    public void LLMUsage_SetAllProperties_ShouldWork()
    {
        // Arrange
        var usage = new LLMUsage();
        var usageId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        // Act
        usage.Id = usageId;
        usage.InputTokens = 500;
        usage.OutputTokens = 300;
        usage.Cost = 0.12m;
        usage.ModelId = modelId;
        usage.UserId = "user-123";
        usage.ConversationId = "conv-456";

        // Assert
        Assert.That(usage.Id, Is.EqualTo(usageId));
        Assert.That(usage.InputTokens, Is.EqualTo(500));
        Assert.That(usage.OutputTokens, Is.EqualTo(300));
        Assert.That(usage.TotalTokens, Is.EqualTo(800));
        Assert.That(usage.Cost, Is.EqualTo(0.12m));
        Assert.That(usage.ModelId, Is.EqualTo(modelId));
        Assert.That(usage.UserId, Is.EqualTo("user-123"));
        Assert.That(usage.ConversationId, Is.EqualTo("conv-456"));
    }
}