// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AnthropicProvider's system-prompt cache-block splitting (P1 of the Klacksy
/// memory redesign): the stable segment is sent as a cache_control-tagged block, the volatile
/// segment (when present) follows as a second, uncached block.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class AnthropicProviderCacheBlockSplittingTests
{
    private const string CompletionBody =
        "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string CapturedRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (AnthropicProvider Provider, CapturingHandler Handler) CreateProvider()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.test/v1/"),
        };

        var provider = new AnthropicProvider(
            httpClient,
            Substitute.For<ILogger<AnthropicProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "anthropic",
            ProviderName = "Anthropic",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = "https://api.anthropic.test/v1/",
            ApiVersion = "2023-06-01",
        });

        return (provider, handler);
    }

    [Test]
    public async Task ProcessAsync_StableAndVolatilePresent_EmitsExactlyTwoBlocks_OnlyFirstCached()
    {
        var (provider, handler) = CreateProvider();
        var request = new LLMProviderRequest
        {
            Message = "Hello",
            SystemPrompt = "stable identity text",
            VolatileSystemPrompt = "volatile memory text",
            ModelId = "claude-opus-4-8",
            MaxTokens = 16,
        };

        await provider.ProcessAsync(request, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.CapturedRequestBody);
        var blocks = document.RootElement.GetProperty("system");

        blocks.GetArrayLength().ShouldBe(2);

        var stableBlock = blocks[0];
        stableBlock.GetProperty("text").GetString().ShouldBe("stable identity text");
        stableBlock.TryGetProperty("cache_control", out var stableCacheControl).ShouldBeTrue();
        stableCacheControl.GetProperty("type").GetString().ShouldBe("ephemeral");

        var volatileBlock = blocks[1];
        volatileBlock.GetProperty("text").GetString().ShouldBe("volatile memory text");
        volatileBlock.TryGetProperty("cache_control", out _).ShouldBeFalse();
    }

    [Test]
    public async Task ProcessAsync_EmptyVolatileSegment_EmitsSingleCachedBlock()
    {
        var (provider, handler) = CreateProvider();
        var request = new LLMProviderRequest
        {
            Message = "Hello",
            SystemPrompt = "stable identity text",
            VolatileSystemPrompt = null,
            ModelId = "claude-opus-4-8",
            MaxTokens = 16,
        };

        await provider.ProcessAsync(request, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.CapturedRequestBody);
        var blocks = document.RootElement.GetProperty("system");

        blocks.GetArrayLength().ShouldBe(1);
        blocks[0].GetProperty("text").GetString().ShouldBe("stable identity text");
        blocks[0].TryGetProperty("cache_control", out var cacheControl).ShouldBeTrue();
        cacheControl.GetProperty("type").GetString().ShouldBe("ephemeral");
    }

    [Test]
    public async Task ProcessAsync_BlankVolatileSegment_EmitsSingleCachedBlock()
    {
        var (provider, handler) = CreateProvider();
        var request = new LLMProviderRequest
        {
            Message = "Hello",
            SystemPrompt = "stable identity text",
            VolatileSystemPrompt = string.Empty,
            ModelId = "claude-opus-4-8",
            MaxTokens = 16,
        };

        await provider.ProcessAsync(request, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.CapturedRequestBody);
        var blocks = document.RootElement.GetProperty("system");

        blocks.GetArrayLength().ShouldBe(1);
    }

    [Test]
    public async Task ProcessAsync_NoStableOrVolatilePrompt_OmitsSystemField()
    {
        var (provider, handler) = CreateProvider();
        var request = new LLMProviderRequest
        {
            Message = "Hello",
            SystemPrompt = string.Empty,
            VolatileSystemPrompt = null,
            ModelId = "claude-opus-4-8",
            MaxTokens = 16,
        };

        await provider.ProcessAsync(request, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.CapturedRequestBody);
        document.RootElement.TryGetProperty("system", out _).ShouldBeFalse();
    }
}
