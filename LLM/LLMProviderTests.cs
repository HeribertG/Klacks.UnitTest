using NUnit.Framework;
using Klacks.Api.Domain.Models.LLM;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMProviderTests
{
    [Test]
    public void LLMProvider_Constructor_ShouldInitializeProperties()
    {
        // Act
        var provider = new LLMProvider();

        // Assert
        Assert.That(provider.Id, Is.EqualTo(Guid.Empty));
        Assert.That(provider.ProviderId, Is.EqualTo(string.Empty));
        Assert.That(provider.ProviderName, Is.EqualTo(string.Empty));
        Assert.That(provider.IsEnabled, Is.False);
        Assert.That(provider.Priority, Is.EqualTo(0));
        Assert.That(provider.ApiKey, Is.Null);
        Assert.That(provider.BaseUrl, Is.Null);
        Assert.That(provider.ApiVersion, Is.Null);
        Assert.That(provider.Settings, Is.Null);
    }

    [Test]
    public void LLMProvider_SetProperties_ShouldWork()
    {
        // Arrange
        var provider = new LLMProvider();
        var providerId = Guid.NewGuid();
        var settings = new Dictionary<string, object>
        {
            { "temperature", 0.7 },
            { "max_tokens", 1000 }
        };

        // Act
        provider.Id = providerId;
        provider.ProviderId = "openai";
        provider.ProviderName = "OpenAI";
        provider.ApiKey = "sk-test-key";
        provider.BaseUrl = "https://api.openai.com/v1";
        provider.ApiVersion = "2023-07-01";
        provider.IsEnabled = true;
        provider.Priority = 1;
        provider.Settings = settings;

        // Assert
        Assert.That(provider.Id, Is.EqualTo(providerId));
        Assert.That(provider.ProviderId, Is.EqualTo("openai"));
        Assert.That(provider.ProviderName, Is.EqualTo("OpenAI"));
        Assert.That(provider.ApiKey, Is.EqualTo("sk-test-key"));
        Assert.That(provider.BaseUrl, Is.EqualTo("https://api.openai.com/v1"));
        Assert.That(provider.ApiVersion, Is.EqualTo("2023-07-01"));
        Assert.That(provider.IsEnabled, Is.True);
        Assert.That(provider.Priority, Is.EqualTo(1));
        Assert.That(provider.Settings, Is.Not.Null);
        Assert.That(provider.Settings.Count, Is.EqualTo(2));
        Assert.That(provider.Settings["temperature"], Is.EqualTo(0.7));
        Assert.That(provider.Settings["max_tokens"], Is.EqualTo(1000));
    }

    [Test]
    public void LLMProvider_Models_ShouldInitializeAsEmpty()
    {
        // Act
        var provider = new LLMProvider();

        // Assert
        Assert.That(provider.Models, Is.Not.Null);
        Assert.That(provider.Models, Is.Empty);
    }
}