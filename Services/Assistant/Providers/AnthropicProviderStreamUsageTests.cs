// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the prompt-cache telemetry on the streaming path: cache counters arrive in the
/// message_start event and the final output count in message_delta, and both must reach the log
/// because ProcessStreamAsync yields text and cannot return a usage object.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProviderUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class AnthropicProviderStreamUsageTests
{
    private const string CachedStreamBody =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":15," +
        "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":4096,\"output_tokens\":1}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hallo\"}}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":128}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private const string UncachedStreamBody =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":300," +
        "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private sealed class RecordingLogger : ILogger<AnthropicProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class StreamingHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private static (AnthropicProvider Provider, RecordingLogger Logger) CreateProvider(string sseBody)
    {
        var logger = new RecordingLogger();
        var httpClient = new HttpClient(new StreamingHandler(sseBody))
        {
            BaseAddress = new Uri("https://api.anthropic.test/v1/"),
        };

        var provider = new AnthropicProvider(httpClient, logger, Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "anthropic",
            ProviderName = "Anthropic",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = "https://api.anthropic.test/v1/",
            ApiVersion = "2023-06-01",
        });

        return (provider, logger);
    }

    private static LLMProviderRequest CreateRequest() => new()
    {
        Message = "hi",
        SystemPrompt = "stable prefix",
        ModelId = "claude-haiku-4-5-20251001",
        Stream = true,
    };

    private static async Task DrainAsync(AnthropicProvider provider)
    {
        await foreach (var _ in provider.ProcessStreamAsync(CreateRequest(), CancellationToken.None))
        {
        }
    }

    [Test]
    public async Task ProcessStreamAsync_CacheReadReported_IsLoggedWithCountersAndHitRatio()
    {
        var (provider, logger) = CreateProvider(CachedStreamBody);

        await DrainAsync(provider);

        var telemetry = logger.Entries.SingleOrDefault(e => e.Message.Contains("prompt-cache stream"));
        telemetry.Message.ShouldNotBeNull();
        telemetry.Message.ShouldContain("cacheRead=4096");
        telemetry.Message.ShouldContain("cacheWrite=0");
        telemetry.Message.ShouldContain("uncachedInput=15");
        telemetry.Message.ShouldContain("hitRatio=1.00");
    }

    [Test]
    public async Task ProcessStreamAsync_OutputTokensFromMessageDelta_OverrideTheMessageStartPlaceholder()
    {
        var (provider, logger) = CreateProvider(CachedStreamBody);

        await DrainAsync(provider);

        logger.Entries
            .Single(e => e.Message.Contains("prompt-cache stream"))
            .Message.ShouldContain("output=128");
    }

    [Test]
    public async Task ProcessStreamAsync_NoCacheActivityAtAll_WarnsThatCachingIsInactive()
    {
        var (provider, logger) = CreateProvider(UncachedStreamBody);

        await DrainAsync(provider);

        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("prompt-cache INACTIVE"));
    }

    [Test]
    public async Task ProcessStreamAsync_UsageReported_ReachesTheCallerThroughOnStreamUsage()
    {
        var (provider, _) = CreateProvider(CachedStreamBody);
        var request = CreateRequest();
        request.CostPerInputToken = 0.003m;
        request.CostPerOutputToken = 0.015m;
        ProviderUsage? received = null;
        request.OnStreamUsage = usage => received = usage;

        await foreach (var _ in provider.ProcessStreamAsync(request, CancellationToken.None))
        {
        }

        received.ShouldNotBeNull();
        received!.CacheReadInputTokens.ShouldBe(4096);
        received.InputTokens.ShouldBe(15);
        received.OutputTokens.ShouldBe(128);
        received.TotalTokens.ShouldBe(4096 + 15 + 128);
        received.Cost.ShouldBeGreaterThan(0m);
    }

    [Test]
    public async Task ProcessStreamAsync_ExplicitCacheReadRate_OverridesTheProviderDefaultMultiplier()
    {
        var (provider, _) = CreateProvider(CachedStreamBody);
        var request = CreateRequest();
        request.CostPerInputToken = 0.003m;
        request.CostPerOutputToken = 0.015m;
        request.CostPerCacheReadToken = 0.0001m;
        ProviderUsage? received = null;
        request.OnStreamUsage = usage => received = usage;

        await foreach (var _ in provider.ProcessStreamAsync(request, CancellationToken.None))
        {
        }

        var expected =
            (15m / 1000m * 0.003m) +
            (4096m / 1000m * 0.0001m) +
            (128m / 1000m * 0.015m);

        received!.Cost.ShouldBe(expected);
    }

    [Test]
    public async Task ProcessStreamAsync_CacheActive_DoesNotWarn()
    {
        var (provider, logger) = CreateProvider(CachedStreamBody);

        await DrainAsync(provider);

        logger.Entries.ShouldNotContain(e => e.Message.Contains("prompt-cache INACTIVE"));
    }
}
