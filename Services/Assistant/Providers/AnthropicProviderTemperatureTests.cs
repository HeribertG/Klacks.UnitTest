// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class AnthropicProviderTemperatureTests
{
    private const string TemperatureField = "\"temperature\"";

    private const string CompletionBody =
        "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    private const string StreamBody =
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _mediaType;

        public CapturingHandler(string body, string mediaType)
        {
            _body = body;
            _mediaType = mediaType;
        }

        public string CapturedRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _mediaType),
            };
        }
    }

    private static (AnthropicProvider Provider, CapturingHandler Handler) CreateProvider(
        string body, string mediaType)
    {
        var handler = new CapturingHandler(body, mediaType);
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

    private static LLMProviderRequest CreateRequest(string modelId) => new()
    {
        Message = "Hello",
        SystemPrompt = "system",
        ModelId = modelId,
        Temperature = 0.0,
        MaxTokens = 16,
    };

    [TestCase("claude-sonnet-5")]
    [TestCase("claude-opus-4-8")]
    [TestCase("claude-opus-4-7")]
    [TestCase("claude-fable-5")]
    [TestCase("some-future-claude-model")]
    public async Task ProcessAsync_ModelWithoutSamplingSupport_OmitsTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        handler.CapturedRequestBody.ShouldNotBeEmpty();
        handler.CapturedRequestBody.ShouldNotContain(TemperatureField);
    }

    [TestCase("claude-haiku-4-5-20251001")]
    [TestCase("claude-sonnet-4-5-20250929")]
    [TestCase("claude-opus-4-6")]
    public async Task ProcessAsync_LegacyModel_SendsTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        handler.CapturedRequestBody.ShouldContain(TemperatureField);
    }

    [TestCase("claude-sonnet-5")]
    [TestCase("claude-opus-4-8")]
    public async Task ProcessStreamAsync_ModelWithoutSamplingSupport_OmitsTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(StreamBody, "text/event-stream");

        await foreach (var _ in provider.ProcessStreamAsync(CreateRequest(modelId), CancellationToken.None))
        {
        }

        handler.CapturedRequestBody.ShouldNotBeEmpty();
        handler.CapturedRequestBody.ShouldNotContain(TemperatureField);
    }

    [Test]
    public async Task ProcessStreamAsync_LegacyModel_SendsTemperature()
    {
        var (provider, handler) = CreateProvider(StreamBody, "text/event-stream");

        await foreach (var _ in provider.ProcessStreamAsync(
            CreateRequest("claude-haiku-4-5-20251001"), CancellationToken.None))
        {
        }

        handler.CapturedRequestBody.ShouldContain(TemperatureField);
    }

    [Test]
    public async Task TestModelAsync_ModelWithoutSamplingSupport_OmitsTemperature()
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.TestModelAsync("claude-sonnet-5");

        handler.CapturedRequestBody.ShouldNotBeEmpty();
        handler.CapturedRequestBody.ShouldNotContain(TemperatureField);
    }
}
