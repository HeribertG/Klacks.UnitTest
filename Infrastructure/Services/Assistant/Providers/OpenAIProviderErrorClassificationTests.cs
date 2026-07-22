// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Text;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.OpenAI;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class OpenAIProviderErrorClassificationTests
{
    private const string NotAChatModelBody =
        "{\"error\":{\"message\":\"This is not a chat model and thus not supported in the v1/chat/completions endpoint. " +
        "Did you mean to use v1/completions?\",\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":null}}";

    private const string TransientServerErrorBody =
        "{\"error\":{\"message\":\"The server had an error while processing your request. Sorry about that!\"," +
        "\"type\":\"server_error\"}}";

    private const string GenuineBadRequestBody =
        "{\"error\":{\"message\":\"Invalid value for 'temperature'\",\"type\":\"invalid_request_error\"}}";

    [Test]
    public async Task ProcessAsync_NotAChatModel404_LogsSingleWarningWithoutStackTrace()
    {
        var logger = new RecordingLogger<OpenAIProvider>();
        var provider = CreateProvider(HttpStatusCode.NotFound, NotAChatModelBody, logger);

        var result = await provider.ProcessAsync(Request("text-embedding-3-large"));

        result.Success.ShouldBeFalse();
        logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
        logger.Entries.ShouldAllBe(e => e.Level != LogLevel.Error);
        logger.Entries.ShouldAllBe(e => e.Exception == null);
    }

    [Test]
    public async Task ProcessAsync_TransientServerError500_LogsSingleWarningWithoutStackTrace()
    {
        var logger = new RecordingLogger<OpenAIProvider>();
        var provider = CreateProvider(HttpStatusCode.InternalServerError, TransientServerErrorBody, logger);

        var result = await provider.ProcessAsync(Request("gpt-5"));

        result.Success.ShouldBeFalse();
        logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
        logger.Entries.ShouldAllBe(e => e.Level != LogLevel.Error);
        logger.Entries.ShouldAllBe(e => e.Exception == null);
    }

    [Test]
    public async Task ProcessAsync_GenuineBadRequest_StillLogsErrorWithStackTrace()
    {
        var logger = new RecordingLogger<OpenAIProvider>();
        var provider = CreateProvider(HttpStatusCode.BadRequest, GenuineBadRequestBody, logger);

        var result = await provider.ProcessAsync(Request("gpt-5"));

        result.Success.ShouldBeFalse();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Exception != null);
    }

    private static OpenAIProvider CreateProvider(
        HttpStatusCode statusCode, string body, ILogger<OpenAIProvider> logger)
    {
        var handler = new SingleResponseHandler(statusCode, body);
        var provider = new OpenAIProvider(
            new HttpClient(handler),
            logger,
            new ConfigurationBuilder().Build());

        provider.Configure(new LLMProvider
        {
            ProviderId = "openai",
            ProviderName = "OpenAI",
            IsEnabled = true,
            ApiKey = "test-key",
            BaseUrl = "https://api.openai.test/v1/"
        });

        return provider;
    }

    private static LLMProviderRequest Request(string modelId) => new()
    {
        Message = "Reply with 'ok'",
        ModelId = modelId,
        MaxTokens = 5,
        Temperature = 0.0,
    };

    private sealed class SingleResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
