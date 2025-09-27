using NUnit.Framework;
using Klacks.Api.Domain.Models.LLM;

namespace UnitTest.LLM;

[TestFixture]
public class LLMModelTests
{
    [Test]
    public void LLMModel_Constructor_ShouldInitializeProperties()
    {
        // Act
        var model = new LLMModel();

        // Assert
        Assert.That(model.Id, Is.EqualTo(Guid.Empty));
        Assert.That(model.ModelId, Is.EqualTo(string.Empty));
        Assert.That(model.ModelName, Is.EqualTo(string.Empty));
        Assert.That(model.ApiModelId, Is.EqualTo(string.Empty));
        Assert.That(model.ProviderId, Is.EqualTo(string.Empty));
        Assert.That(model.IsEnabled, Is.False);
        Assert.That(model.IsDefault, Is.False);
        Assert.That(model.MaxTokens, Is.EqualTo(4096));
        Assert.That(model.ContextWindow, Is.EqualTo(128000));
        Assert.That(model.CostPerInputToken, Is.EqualTo(0));
        Assert.That(model.CostPerOutputToken, Is.EqualTo(0));
    }

    [Test]
    public void LLMModel_SetProperties_ShouldWork()
    {
        // Arrange
        var model = new LLMModel();
        var modelId = Guid.NewGuid();

        // Act
        model.Id = modelId;
        model.ModelId = "gpt-4";
        model.ModelName = "GPT-4";
        model.ApiModelId = "gpt-4-turbo";
        model.ProviderId = "openai";
        model.IsEnabled = true;
        model.IsDefault = true;
        model.MaxTokens = 8000;
        model.ContextWindow = 32000;
        model.CostPerInputToken = 0.01m;
        model.CostPerOutputToken = 0.03m;
        model.Description = "Advanced AI model";
        model.Category = "Text Generation";

        // Assert
        Assert.That(model.Id, Is.EqualTo(modelId));
        Assert.That(model.ModelId, Is.EqualTo("gpt-4"));
        Assert.That(model.ModelName, Is.EqualTo("GPT-4"));
        Assert.That(model.ApiModelId, Is.EqualTo("gpt-4-turbo"));
        Assert.That(model.ProviderId, Is.EqualTo("openai"));
        Assert.That(model.IsEnabled, Is.True);
        Assert.That(model.IsDefault, Is.True);
        Assert.That(model.MaxTokens, Is.EqualTo(8000));
        Assert.That(model.ContextWindow, Is.EqualTo(32000));
        Assert.That(model.CostPerInputToken, Is.EqualTo(0.01m));
        Assert.That(model.CostPerOutputToken, Is.EqualTo(0.03m));
        Assert.That(model.Description, Is.EqualTo("Advanced AI model"));
        Assert.That(model.Category, Is.EqualTo("Text Generation"));
    }

    [Test]
    public void LLMModel_Usages_ShouldInitializeAsEmpty()
    {
        // Act
        var model = new LLMModel();

        // Assert
        Assert.That(model.Usages, Is.Not.Null);
        Assert.That(model.Usages, Is.Empty);
    }
}