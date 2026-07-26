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
public class OpenAIProviderErrorModelIdentificationTests
{
    private const string TemperatureRejectionBody =
        "{\"error\":{\"message\":\"Model incompatible request argument supplied: temperature\"," +
        "\"type\":\"invalid_request_error\",\"param\":null,\"code\":null}}";

    private const string DeprecationBody =
        "{\"error\":{\"message\":\"The model has been deprecated\"," +
        "\"type\":\"invalid_request_error\",\"param\":null,\"code\":null}}";

    private sealed class FailingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static OpenAIProvider CreateProvider(HttpStatusCode statusCode, string body)
    {
        var httpClient = new HttpClient(new FailingHandler(statusCode, body))
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

        return provider;
    }

    private static LLMProviderRequest CreateRequest(string modelId) => new()
    {
        Message = "Hello",
        SystemPrompt = "system",
        ModelId = modelId,
        Temperature = 0.0,
        MaxTokens = 16,
    };

    [TestCase("gpt-5.3-codex", TemperatureRejectionBody)]
    [TestCase("gpt-4o-mini-search-preview-2025-03-11", DeprecationBody)]
    public async Task ProcessAsync_ProviderRejectsRequest_ErrorIdentifiesFailingModel(
        string modelId, string body)
    {
        var provider = CreateProvider(HttpStatusCode.BadRequest, body);

        var response = await provider.ProcessAsync(CreateRequest(modelId), CancellationToken.None);

        response.Success.ShouldBeFalse();
        response.Error.ShouldContain(modelId);
    }

    [Test]
    public async Task TestModelAsync_ProviderRejectsRequest_ErrorIdentifiesFailingModel()
    {
        const string modelId = "gpt-5.3-codex";
        var provider = CreateProvider(HttpStatusCode.BadRequest, TemperatureRejectionBody);

        var result = await provider.TestModelAsync(modelId);

        result.Passed.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain(modelId);
    }
}
