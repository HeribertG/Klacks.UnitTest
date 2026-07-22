// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class NonChatModelPatternsTests
{
    [TestCase("gpt-4o")]
    [TestCase("gpt-4o-mini")]
    [TestCase("gpt-5")]
    [TestCase("gpt-5-mini")]
    [TestCase("o3")]
    [TestCase("o4-mini")]
    [TestCase("gpt-4.1")]
    [TestCase("chatgpt-4o-latest")]
    [TestCase("claude-sonnet-4-5")]
    [TestCase("gemini-2.5-pro")]
    [TestCase("gemini-2.0-flash")]
    [TestCase("deepseek-v4-flash")]
    [TestCase("mistral-large-latest")]
    [TestCase("gpt-oss-120b")]
    [TestCase("llama-3.3-70b-versatile")]
    public void IsLikelyNonChatModel_ChatModels_ReturnsFalse(string apiModelId)
    {
        NonChatModelPatterns.IsLikelyNonChatModel(apiModelId).ShouldBeFalse();
    }

    [TestCase("text-embedding-3-large")]
    [TestCase("text-embedding-ada-002")]
    [TestCase("mistral-embed")]
    [TestCase("nomic-embed-text")]
    [TestCase("whisper-1")]
    [TestCase("gpt-4o-transcribe")]
    [TestCase("gpt-4o-mini-transcribe")]
    [TestCase("tts-1")]
    [TestCase("tts-1-hd")]
    [TestCase("gpt-4o-mini-tts")]
    [TestCase("gpt-4o-audio-preview")]
    [TestCase("gpt-audio")]
    [TestCase("dall-e-2")]
    [TestCase("dall-e-3")]
    [TestCase("gpt-image-1")]
    [TestCase("text-moderation-latest")]
    [TestCase("omni-moderation-latest")]
    [TestCase("gpt-4o-realtime-preview")]
    [TestCase("gpt-realtime")]
    [TestCase("davinci-002")]
    [TestCase("babbage-002")]
    public void IsLikelyNonChatModel_NonChatModels_ReturnsTrue(string apiModelId)
    {
        NonChatModelPatterns.IsLikelyNonChatModel(apiModelId).ShouldBeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsLikelyNonChatModel_NullOrWhitespace_ReturnsFalse(string? apiModelId)
    {
        NonChatModelPatterns.IsLikelyNonChatModel(apiModelId).ShouldBeFalse();
    }

    [Test]
    public void IsLikelyNonChatModel_IsCaseInsensitive()
    {
        NonChatModelPatterns.IsLikelyNonChatModel("Text-Embedding-3-Large").ShouldBeTrue();
        NonChatModelPatterns.IsLikelyNonChatModel("WHISPER-1").ShouldBeTrue();
    }
}
