// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class OpenAIProviderTemperatureTests
{
    private const string TemperatureField = "\"temperature\"";

    private const string CompletionBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

    private const string StreamBody =
        "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
        "data: [DONE]\n\n";

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

    private static (OpenAIProvider Provider, CapturingHandler Handler) CreateProvider(
        string body, string mediaType)
    {
        var handler = new CapturingHandler(body, mediaType);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.test/v1/"),
        };

        var provider = new OpenAIProvider(
            httpClient,
            Substitute.For<ILogger<OpenAIProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "openai",
            ProviderName = "OpenAI",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = "https://api.openai.test/v1/",
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

    [TestCase("gpt-5-nano")]
    [TestCase("gpt-5-search-api")]
    [TestCase("gpt-5-mini")]
    [TestCase("gpt-5-pro")]
    [TestCase("o3-mini")]
    [TestCase("o4-mini")]
    [TestCase("some-future-model")]
    public async Task ProcessAsync_ModelRejectingTemperature_OmitsTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        handler.CapturedRequestBody.ShouldNotBeEmpty();
        handler.CapturedRequestBody.ShouldNotContain(TemperatureField);
    }

    [TestCase("gpt-3.5-turbo")]
    [TestCase("gpt-4o")]
    [TestCase("gpt-4.1-mini")]
    [TestCase("gpt-4o-2024-08-06")]
    [TestCase("gpt-5-chat-latest")]
    [TestCase("gpt-5.2")]
    [TestCase("gpt-5.4-nano")]
    public async Task ProcessAsync_ModelAcceptingTemperature_SendsTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        handler.CapturedRequestBody.ShouldContain(TemperatureField);
    }

    [TestCase("gpt-5-nano")]
    [TestCase("gpt-5-search-api")]
    public async Task ProcessStreamAsync_ModelRejectingTemperature_OmitsTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(StreamBody, "text/event-stream");

        await foreach (var _ in provider.ProcessStreamAsync(CreateRequest(modelId), CancellationToken.None))
        {
        }

        handler.CapturedRequestBody.ShouldNotBeEmpty();
        handler.CapturedRequestBody.ShouldNotContain(TemperatureField);
    }

    [Test]
    public async Task ProcessStreamAsync_ModelAcceptingTemperature_SendsTemperature()
    {
        var (provider, handler) = CreateProvider(StreamBody, "text/event-stream");

        await foreach (var _ in provider.ProcessStreamAsync(
            CreateRequest("gpt-4o"), CancellationToken.None))
        {
        }

        handler.CapturedRequestBody.ShouldContain(TemperatureField);
    }

    [Test]
    public async Task TestModelAsync_ModelRejectingTemperature_OmitsTemperature()
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.TestModelAsync("gpt-5-nano");

        handler.CapturedRequestBody.ShouldNotBeEmpty();
        handler.CapturedRequestBody.ShouldNotContain(TemperatureField);
    }

    [Test]
    public async Task TestModelAsync_ModelAcceptingTemperature_SendsTemperature()
    {
        var (provider, handler) = CreateProvider(CompletionBody, "application/json");

        await provider.TestModelAsync("gpt-4o");

        handler.CapturedRequestBody.ShouldContain(TemperatureField);
    }
}
