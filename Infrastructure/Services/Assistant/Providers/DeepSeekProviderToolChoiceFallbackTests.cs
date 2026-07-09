// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Text;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class DeepSeekProviderToolChoiceFallbackTests
{
    private const string ThinkingRejectionBody =
        "{\"error\":{\"message\":\"Thinking mode does not support this tool_choice\",\"type\":\"invalid_request_error\"}}";

    private const string SuccessBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}";

    private const string UnrelatedErrorBody =
        "{\"error\":{\"message\":\"Some other validation problem\",\"type\":\"invalid_request_error\"}}";

    [Test]
    public async Task ProcessAsync_ThinkingModeRejectsRequired_RetriesOnceWithAuto()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ThinkingRejectionBody) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(RequestWithToolChoice("required"));

        result.Success.ShouldBeTrue(result.Error);
        result.Content.ShouldBe("ok");
        handler.RequestBodies.Count.ShouldBe(2);
        handler.RequestBodies[0].ShouldContain("\"tool_choice\":\"required\"");
        handler.RequestBodies[1].ShouldContain("\"tool_choice\":\"auto\"");
    }

    [Test]
    public async Task ProcessAsync_UnrelatedError_DoesNotRetry()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(UnrelatedErrorBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(RequestWithToolChoice("required"));

        result.Success.ShouldBeFalse();
        handler.RequestBodies.Count.ShouldBe(1);
    }

    [Test]
    public async Task ProcessAsync_ThinkingRejectionWithoutRequired_DoesNotRetry()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ThinkingRejectionBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(RequestWithToolChoice(null));

        result.Success.ShouldBeFalse();
        handler.RequestBodies.Count.ShouldBe(1);
    }

    private static DeepSeekProvider CreateProvider(SequenceHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var provider = new DeepSeekProvider(
            httpClient,
            NullLogger<DeepSeekProvider>.Instance,
            new ConfigurationBuilder().Build());

        provider.Configure(new LLMProvider
        {
            ProviderId = "deepseek",
            ProviderName = "DeepSeek",
            IsEnabled = true,
            ApiKey = "test-key",
            BaseUrl = "https://deepseek.test/v1/"
        });

        return provider;
    }

    private static LLMProviderRequest RequestWithToolChoice(string? toolChoice)
    {
        return new LLMProviderRequest
        {
            Message = "Ändere die Telefonnummer von Frau Müller",
            SystemPrompt = "system",
            ModelId = "deepseek-v4-flash",
            ToolChoice = toolChoice,
            AvailableFunctions =
            [
                new LLMFunction { Name = "add_client_phone", Description = "Adds a phone number" }
            ]
        };
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public List<string> RequestBodies { get; } = new();

        public SequenceHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = Json("{}") };
        }
    }
}
