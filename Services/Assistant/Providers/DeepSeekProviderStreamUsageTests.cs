// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeepSeek stream usage: a stream only reports token counters when stream_options
/// asks for them, and DeepSeek's prompt_tokens already includes the cache hits, so the cached part
/// must be subtracted to avoid counting the same tokens twice.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProviderUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class DeepSeekProviderStreamUsageTests
{
    private const string CachedStreamBody =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hallo\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":15000,\"completion_tokens\":300," +
        "\"total_tokens\":15300,\"prompt_cache_hit_tokens\":14000,\"prompt_cache_miss_tokens\":1000}}\n\n" +
        "data: [DONE]\n\n";

    private sealed class CapturingHandler(string body) : HttpMessageHandler
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
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static (DeepSeekProvider Provider, CapturingHandler Handler) CreateProvider(string sseBody)
    {
        var handler = new CapturingHandler(sseBody);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.deepseek.test/v1/"),
        };

        var provider = new DeepSeekProvider(
            httpClient,
            Substitute.For<ILogger<DeepSeekProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "deepseek",
            ProviderName = "DeepSeek",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = "https://api.deepseek.test/v1/",
        });

        return (provider, handler);
    }

    private static LLMProviderRequest CreateRequest() => new()
    {
        Message = "hi",
        SystemPrompt = "stable prefix",
        ModelId = "deepseek-v4-pro",
        Stream = true,
        CostPerInputToken = 0.001m,
        CostPerOutputToken = 0.002m,
    };

    private static async Task<ProviderUsage?> RunAsync(DeepSeekProvider provider, LLMProviderRequest request)
    {
        ProviderUsage? received = null;
        request.OnStreamUsage = usage => received = usage;

        await foreach (var _ in provider.ProcessStreamAsync(request, CancellationToken.None))
        {
        }

        return received;
    }

    [Test]
    public async Task ProcessStreamAsync_Always_AsksTheApiToIncludeUsage()
    {
        var (provider, handler) = CreateProvider(CachedStreamBody);

        await RunAsync(provider, CreateRequest());

        handler.CapturedRequestBody.ShouldContain("\"stream_options\"");
        handler.CapturedRequestBody.ShouldContain("\"include_usage\":true");
    }

    [Test]
    public async Task ProcessStreamAsync_UsageChunkWithEmptyChoices_StillReachesTheCaller()
    {
        var (provider, _) = CreateProvider(CachedStreamBody);

        var usage = await RunAsync(provider, CreateRequest());

        usage.ShouldNotBeNull();
        usage!.OutputTokens.ShouldBe(300);
    }

    [Test]
    public async Task ProcessStreamAsync_CacheHits_AreNotCountedTwiceInTheTotal()
    {
        var (provider, _) = CreateProvider(CachedStreamBody);

        var usage = await RunAsync(provider, CreateRequest());

        // prompt_tokens (15000) already contains the 14000 cache hits, so the uncached remainder is 1000.
        usage!.InputTokens.ShouldBe(1000);
        usage.CacheReadInputTokens.ShouldBe(14000);
        usage.TotalTokens.ShouldBe(15000 + 300);
    }

    [Test]
    public async Task ProcessStreamAsync_CacheHits_AreBilledAtTheDeepSeekCacheRate()
    {
        var (provider, _) = CreateProvider(CachedStreamBody);

        var usage = await RunAsync(provider, CreateRequest());

        var expected =
            (1000m / 1000m * 0.001m) +
            (14000m / 1000m * 0.001m * 0.1m) +
            (300m / 1000m * 0.002m);

        usage!.Cost.ShouldBe(expected);
    }

    [Test]
    public async Task ProcessStreamAsync_ExplicitCacheReadRate_WinsOverTheProviderDefault()
    {
        var (provider, _) = CreateProvider(CachedStreamBody);
        var request = CreateRequest();
        request.CostPerCacheReadToken = 0.00005m;

        var usage = await RunAsync(provider, request);

        var expected =
            (1000m / 1000m * 0.001m) +
            (14000m / 1000m * 0.00005m) +
            (300m / 1000m * 0.002m);

        usage!.Cost.ShouldBe(expected);
    }

    [Test]
    public async Task ProcessStreamAsync_ProviderReportsNoCacheCounters_TreatsEverythingAsUncached()
    {
        var body =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":500,\"completion_tokens\":50,\"total_tokens\":550}}\n\n" +
            "data: [DONE]\n\n";
        var (provider, _) = CreateProvider(body);

        var usage = await RunAsync(provider, CreateRequest());

        usage!.InputTokens.ShouldBe(500);
        usage.CacheReadInputTokens.ShouldBe(0);
        usage.TotalTokens.ShouldBe(550);
    }
}
