// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LlmProviderCredentialReader — verifies it returns the API key only for a provider
/// that is both enabled and has a key, and null for every other case (missing, disabled, no key).
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Repositories.Assistant;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class LlmProviderCredentialReaderTests
{
    private ILLMRepository _llmRepository = null!;
    private LlmProviderCredentialReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _llmRepository = Substitute.For<ILLMRepository>();
        _reader = new LlmProviderCredentialReader(_llmRepository);
    }

    [Test]
    public async Task GetApiKeyAsync_EnabledProviderWithKey_ReturnsKey()
    {
        _llmRepository.GetProviderByIdAsync("google")
            .Returns(new LLMProvider { ProviderId = "google", IsEnabled = true, ApiKey = "real-key" });

        var result = await _reader.GetApiKeyAsync("google");

        result.ShouldBe("real-key");
    }

    [Test]
    public async Task GetApiKeyAsync_ProviderDisabled_ReturnsNull()
    {
        _llmRepository.GetProviderByIdAsync("google")
            .Returns(new LLMProvider { ProviderId = "google", IsEnabled = false, ApiKey = "real-key" });

        var result = await _reader.GetApiKeyAsync("google");

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetApiKeyAsync_ProviderHasNoKey_ReturnsNull()
    {
        _llmRepository.GetProviderByIdAsync("google")
            .Returns(new LLMProvider { ProviderId = "google", IsEnabled = true, ApiKey = null });

        var result = await _reader.GetApiKeyAsync("google");

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetApiKeyAsync_ProviderNotFound_ReturnsNull()
    {
        _llmRepository.GetProviderByIdAsync("openai").Returns((LLMProvider?)null);

        var result = await _reader.GetApiKeyAsync("openai");

        result.ShouldBeNull();
    }
}
