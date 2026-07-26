// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AnthropicProvider's usage deserialization: the Anthropic wire format uses
/// snake_case token counters, which must bind onto the response DTO so that cost tracking and
/// prompt-cache telemetry receive real numbers instead of zeros.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class AnthropicProviderUsageParsingTests
{
    private const string CompletionBody =
        "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]," +
        "\"usage\":{\"input_tokens\":1200,\"output_tokens\":340," +
        "\"cache_creation_input_tokens\":800,\"cache_read_input_tokens\":2400}}";

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CompletionBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AnthropicProvider CreateProvider()
    {
        var httpClient = new HttpClient(new StubHandler())
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

        return provider;
    }

    private static LLMProviderRequest CreateRequest() => new()
    {
        ModelId = "claude-test",
        Message = "hi",
        SystemPrompt = "stable",
        CostPerInputToken = 0.003m,
        CostPerOutputToken = 0.015m,
    };

    [Test]
    public async Task ProcessAsync_SnakeCaseUsage_BindsInputAndOutputTokens()
    {
        var response = await CreateProvider().ProcessAsync(CreateRequest());

        response.Success.ShouldBeTrue();
        response.Usage.ShouldNotBeNull();
        response.Usage!.InputTokens.ShouldBe(1200);
        response.Usage.OutputTokens.ShouldBe(340);
    }

    [Test]
    public async Task ProcessAsync_UsageBound_ProducesNonZeroCost()
    {
        var response = await CreateProvider().ProcessAsync(CreateRequest());

        response.Usage!.Cost.ShouldBeGreaterThan(0m);
    }

    [Test]
    public async Task ProcessAsync_CacheCountersPresent_AreCarriedIntoUsage()
    {
        var response = await CreateProvider().ProcessAsync(CreateRequest());

        response.Usage!.CacheCreationInputTokens.ShouldBe(800);
        response.Usage.CacheReadInputTokens.ShouldBe(2400);
    }

    [Test]
    public async Task ProcessAsync_CachedTokens_AreBilledAtTheirOwnRates()
    {
        var response = await CreateProvider().ProcessAsync(CreateRequest());

        var expected =
            (1200m / 1000m * 0.003m) +
            (800m / 1000m * 0.003m * 1.25m) +
            (2400m / 1000m * 0.003m * 0.1m) +
            (340m / 1000m * 0.015m);

        response.Usage!.Cost.ShouldBe(expected);
    }
}
