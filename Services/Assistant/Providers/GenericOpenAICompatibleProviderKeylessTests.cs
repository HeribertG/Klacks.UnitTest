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
public class GenericOpenAICompatibleProviderKeylessTests
{
    private const string BaseUrl = "http://localhost:11434/v1/";

    private const string CompletionBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

    private const string ModelsBody =
        "{\"data\":[{\"id\":\"llama3\"},{\"id\":\"qwen3\",\"name\":\"Qwen 3\"}]}";

    private const string StreamBody =
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
        "data: [DONE]\n\n";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _mediaType;

        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(string body, string mediaType = "application/json")
        {
            _body = body;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, _mediaType),
            };
            return Task.FromResult(response);
        }
    }

    private static GenericOpenAICompatibleProvider CreateProvider(RecordingHandler handler, bool requiresApiKey, string? apiKey)
    {
        var httpClient = new HttpClient(handler);
        var provider = new GenericOpenAICompatibleProvider(
            httpClient,
            Substitute.For<ILogger<GenericOpenAICompatibleProvider>>(),
            Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "ollama",
            ProviderName = "Ollama (local)",
            ApiKey = apiKey,
            RequiresApiKey = requiresApiKey,
            IsEnabled = true,
            BaseUrl = BaseUrl,
        });

        return provider;
    }

    private static LLMProviderRequest CreateRequest()
    {
        return new LLMProviderRequest
        {
            ModelId = "llama3",
            Message = "Hi",
        };
    }

    [Test]
    public async Task ProcessAsync_KeylessProviderWithoutKey_CallsApiWithoutAuthorizationHeader()
    {
        var handler = new RecordingHandler(CompletionBody);
        var provider = CreateProvider(handler, requiresApiKey: false, apiKey: null);

        var response = await provider.ProcessAsync(CreateRequest());

        response.Success.ShouldBeTrue();
        response.Content.ShouldBe("ok");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Headers.Authorization.ShouldBeNull();
    }

    [Test]
    public async Task ProcessAsync_KeyRequiredButMissing_ReturnsErrorWithoutHttpCall()
    {
        var handler = new RecordingHandler(CompletionBody);
        var provider = CreateProvider(handler, requiresApiKey: true, apiKey: null);

        var response = await provider.ProcessAsync(CreateRequest());

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe("The provider for the selected model is not available.");
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task ProcessStreamAsync_KeylessProviderWithoutKey_StreamsContent()
    {
        var handler = new RecordingHandler(StreamBody, "text/event-stream");
        var provider = CreateProvider(handler, requiresApiKey: false, apiKey: null);

        var chunks = new List<string>();
        await foreach (var chunk in provider.ProcessStreamAsync(CreateRequest()))
        {
            chunks.Add(chunk);
        }

        chunks.ShouldBe(new[] { "Hello" });
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Headers.Authorization.ShouldBeNull();
    }

    [Test]
    public async Task ProcessStreamAsync_KeyRequiredButMissing_Throws()
    {
        var handler = new RecordingHandler(StreamBody, "text/event-stream");
        var provider = CreateProvider(handler, requiresApiKey: true, apiKey: null);

        var act = async () =>
        {
            await foreach (var _ in provider.ProcessStreamAsync(CreateRequest()))
            {
            }
        };

        await act.ShouldThrowAsync<InvalidOperationException>();
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task TestModelAsync_KeylessProviderWithoutKey_RunsTestAndPasses()
    {
        var handler = new RecordingHandler(CompletionBody);
        var provider = CreateProvider(handler, requiresApiKey: false, apiKey: null);

        var result = await provider.TestModelAsync("llama3");

        result.Passed.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task TestModelAsync_KeyRequiredButMissing_FailsWithoutHttpCall()
    {
        var handler = new RecordingHandler(CompletionBody);
        var provider = CreateProvider(handler, requiresApiKey: true, apiKey: null);

        var result = await provider.TestModelAsync("llama3");

        result.Passed.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("No API key configured");
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task GetAvailableModelsAsync_KeylessProviderWithoutKey_ReturnsDiscoveredModels()
    {
        var handler = new RecordingHandler(ModelsBody);
        var provider = CreateProvider(handler, requiresApiKey: false, apiKey: null);

        var models = await provider.GetAvailableModelsAsync();

        models.ShouldNotBeNull();
        models!.Select(m => m.ApiModelId).ShouldBe(new[] { "llama3", "qwen3" });
    }

    [Test]
    public async Task GetAvailableModelsAsync_KeyRequiredButMissing_ReturnsNull()
    {
        var handler = new RecordingHandler(ModelsBody);
        var provider = CreateProvider(handler, requiresApiKey: true, apiKey: null);

        var models = await provider.GetAvailableModelsAsync();

        models.ShouldBeNull();
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task ProcessAsync_KeylessProviderWithKey_StillSendsBearerHeader()
    {
        var handler = new RecordingHandler(CompletionBody);
        var provider = CreateProvider(handler, requiresApiKey: false, apiKey: "local-key");

        var response = await provider.ProcessAsync(CreateRequest());

        response.Success.ShouldBeTrue();
        handler.Requests[0].Headers.Authorization.ShouldNotBeNull();
        handler.Requests[0].Headers.Authorization!.Parameter.ShouldBe("local-key");
    }
}
