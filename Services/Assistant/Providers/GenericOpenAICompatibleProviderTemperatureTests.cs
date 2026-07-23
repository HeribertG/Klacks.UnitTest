// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class GenericOpenAICompatibleProviderTemperatureTests
{
    private const string KimiBaseUrl = "https://api.kimi.com/coding/v1/";
    private const string NonKimiBaseUrl = "https://api.groq.test/openai/v1/";

    private const string ForcedTemperatureField = "\"temperature\":1";
    private const string PassthroughTemperatureField = "\"temperature\":0.3";
    private const double PassthroughTemperature = 0.3;

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

    private static (GenericOpenAICompatibleProvider Provider, CapturingHandler Handler) CreateProvider(
        string baseUrl, string body, string mediaType)
    {
        var handler = new CapturingHandler(body, mediaType);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
        };

        var provider = new GenericOpenAICompatibleProvider(
            httpClient,
            Substitute.For<ILogger<GenericOpenAICompatibleProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "kimi",
            ProviderName = "Kimi (Moonshot AI)",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = baseUrl,
        });

        return (provider, handler);
    }

    private static LLMProviderRequest CreateRequest(string modelId) => new()
    {
        Message = "Hello",
        SystemPrompt = "system",
        ModelId = modelId,
        Temperature = PassthroughTemperature,
        MaxTokens = 16,
    };

    [TestCase("kimi-for-coding-highspeed")]
    [TestCase("k3")]
    public async Task ProcessAsync_KimiEndpointFixedTemperatureModel_ForcesTemperatureToOne(string modelId)
    {
        var (provider, handler) = CreateProvider(KimiBaseUrl, CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        handler.CapturedRequestBody.ShouldContain(ForcedTemperatureField);
        handler.CapturedRequestBody.ShouldNotContain(PassthroughTemperatureField);
    }

    [Test]
    public async Task ProcessAsync_KimiEndpointRegularModel_KeepsRequestedTemperature()
    {
        var (provider, handler) = CreateProvider(KimiBaseUrl, CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest("kimi-for-coding"), CancellationToken.None);

        handler.CapturedRequestBody.ShouldContain(PassthroughTemperatureField);
        handler.CapturedRequestBody.ShouldNotContain(ForcedTemperatureField);
    }

    [TestCase("k3")]
    [TestCase("kimi-for-coding-highspeed")]
    public async Task ProcessAsync_NonKimiEndpoint_KeepsRequestedTemperature(string modelId)
    {
        var (provider, handler) = CreateProvider(NonKimiBaseUrl, CompletionBody, "application/json");

        await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        handler.CapturedRequestBody.ShouldContain(PassthroughTemperatureField);
        handler.CapturedRequestBody.ShouldNotContain(ForcedTemperatureField);
    }

    [Test]
    public async Task ProcessStreamAsync_KimiEndpointFixedTemperatureModel_ForcesTemperatureToOne()
    {
        var (provider, handler) = CreateProvider(KimiBaseUrl, StreamBody, "text/event-stream");

        await foreach (var _ in provider.ProcessStreamAsync(
            CreateRequest("kimi-for-coding-highspeed"), CancellationToken.None))
        {
        }

        handler.CapturedRequestBody.ShouldContain(ForcedTemperatureField);
        handler.CapturedRequestBody.ShouldNotContain(PassthroughTemperatureField);
    }

    [Test]
    public async Task TestModelAsync_KimiEndpointFixedTemperatureModel_ForcesTemperatureToOne()
    {
        var (provider, handler) = CreateProvider(KimiBaseUrl, CompletionBody, "application/json");

        await provider.TestModelAsync("kimi-for-coding-highspeed");

        handler.CapturedRequestBody.ShouldContain(ForcedTemperatureField);
    }
}
