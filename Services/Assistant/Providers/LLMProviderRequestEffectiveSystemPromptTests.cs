// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMProviderRequest.EffectiveSystemPrompt, the fallback used by providers that do
/// not split the system prompt into separate cache blocks (P1 of the Klacksy memory redesign).
/// </summary>

using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class LLMProviderRequestEffectiveSystemPromptTests
{
    [Test]
    public void EffectiveSystemPrompt_WithoutVolatilePrompt_ReturnsSystemPromptOnly()
    {
        var request = new LLMProviderRequest { SystemPrompt = "stable text" };

        Assert.That(request.EffectiveSystemPrompt, Is.EqualTo("stable text"));
    }

    [Test]
    public void EffectiveSystemPrompt_WithVolatilePrompt_ConcatenatesWithBlankLineSeparator()
    {
        var request = new LLMProviderRequest
        {
            SystemPrompt = "stable text",
            VolatileSystemPrompt = "volatile text"
        };

        Assert.That(request.EffectiveSystemPrompt, Is.EqualTo("stable text\n\nvolatile text"));
    }

    [Test]
    public void EffectiveSystemPrompt_WithEmptyVolatilePrompt_ReturnsSystemPromptOnly()
    {
        var request = new LLMProviderRequest
        {
            SystemPrompt = "stable text",
            VolatileSystemPrompt = string.Empty
        };

        Assert.That(request.EffectiveSystemPrompt, Is.EqualTo("stable text"));
    }

    [Test]
    public void EffectiveSystemPrompt_WithOnlyVolatilePrompt_ReturnsVolatilePromptOnly()
    {
        var request = new LLMProviderRequest
        {
            SystemPrompt = string.Empty,
            VolatileSystemPrompt = "volatile text"
        };

        Assert.That(request.EffectiveSystemPrompt, Is.EqualTo("volatile text"));
    }

    [Test]
    public void EffectiveSystemPrompt_WithNeitherPrompt_ReturnsEmpty()
    {
        var request = new LLMProviderRequest();

        Assert.That(request.EffectiveSystemPrompt, Is.EqualTo(string.Empty));
    }
}
