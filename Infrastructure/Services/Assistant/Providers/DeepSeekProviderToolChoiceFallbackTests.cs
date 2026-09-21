// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
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

        var result = await provider.ProcessAsync(RequestWithToolChoice("required", "deepseek-test-retry-once"));

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

    [Test]
    public async Task ProcessAsync_DefaultConfiguration_OmitsThinkingField()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(RequestWithToolChoice(null));

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestBodies[0].ShouldNotContain("\"thinking\"");
    }

    // The declaration this provider makes, honoured on the wire: a request that forces a tool call turns
    // thinking off by itself, which is the combination the API accepts. Without it the forcing is refused
    // and the retry falls back to auto, where a thinking model answers in prose and the step never runs.
    [Test]
    public async Task ProcessAsync_ForcedToolChoice_SendsThinkingDisabledWithoutAnyConfiguration()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(
            RequestWithToolChoice("required", "deepseek-test-forced-thinking-off"));

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestBodies.Count.ShouldBe(1);
        handler.RequestBodies[0].ShouldContain("\"thinking\":{\"type\":\"disabled\"}");
        handler.RequestBodies[0].ShouldContain("\"tool_choice\":\"required\"");
    }

    [Test]
    public async Task ProcessAsync_UnforcedToolChoice_LeavesThinkingUntouched()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(RequestWithToolChoice("auto"));

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestBodies[0].ShouldNotContain("\"thinking\"");
    }

    [Test]
    public void ResolveForcedToolChoiceSupport_DeclaresThatForcingNeedsThinkingOff()
    {
        var provider = CreateProvider(
            new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) }));

        provider.ResolveForcedToolChoiceSupport(RequestWithToolChoice("required"))
            .ShouldBe(ForcedToolChoiceSupport.RequiresThinkingDisabled);
    }

    // The retry still sends the original intent, so thinking stays off on it too: the downgrade to auto
    // answers a refusal that was NOT about thinking, and re-enabling thinking there would change two
    // things at once.
    [Test]
    public async Task ProcessAsync_ForcedToolChoiceRejectedAnyway_KeepsThinkingDisabledOnTheRetry()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ThinkingRejectionBody) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var provider = CreateProvider(handler);

        var result = await provider.ProcessAsync(
            RequestWithToolChoice("required", "deepseek-test-forced-retry-thinking"));

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestBodies.Count.ShouldBe(2);
        handler.RequestBodies[1].ShouldContain("\"thinking\":{\"type\":\"disabled\"}");
        handler.RequestBodies[1].ShouldContain("\"tool_choice\":\"auto\"");
    }

    [Test]
    public async Task ProcessAsync_DisableThinkingConfigured_SendsThinkingDisabled()
    {
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var provider = CreateProvider(handler, disableThinking: true);

        var result = await provider.ProcessAsync(RequestWithToolChoice("required"));

        result.Success.ShouldBeTrue(result.Error);
        handler.RequestBodies[0].ShouldContain("\"thinking\":{\"type\":\"disabled\"}");
        handler.RequestBodies[0].ShouldContain("\"tool_choice\":\"required\"");
    }

    [Test]
    public async Task ProcessAsync_AfterRejection_SecondCallForSameModelSendsAutoDirectly()
    {
        const string model = "deepseek-test-remembered";
        var first = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ThinkingRejectionBody) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        await CreateProvider(first).ProcessAsync(RequestWithToolChoice("required", model));

        var second = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var result = await CreateProvider(second).ProcessAsync(RequestWithToolChoice("required", model));

        result.Success.ShouldBeTrue(result.Error);
        second.RequestBodies.Count.ShouldBe(1);
        second.RequestBodies[0].ShouldContain("\"tool_choice\":\"auto\"");
    }

    [Test]
    public async Task ProcessAsync_AfterRejection_OtherModelStillSendsRequired()
    {
        var first = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ThinkingRejectionBody) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        await CreateProvider(first).ProcessAsync(RequestWithToolChoice("required", "deepseek-test-isolated-a"));

        var second = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        var result = await CreateProvider(second).ProcessAsync(RequestWithToolChoice("required", "deepseek-test-isolated-b"));

        result.Success.ShouldBeTrue(result.Error);
        second.RequestBodies.Count.ShouldBe(1);
        second.RequestBodies[0].ShouldContain("\"tool_choice\":\"required\"");
    }

    [Test]
    public async Task ProcessAsync_DisableThinkingConfigured_RejectionIsNotRemembered()
    {
        const string model = "deepseek-test-disable-thinking";
        var first = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = Json(ThinkingRejectionBody) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        await CreateProvider(first, disableThinking: true).ProcessAsync(RequestWithToolChoice("required", model));

        var second = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(SuccessBody) });
        await CreateProvider(second, disableThinking: true).ProcessAsync(RequestWithToolChoice("required", model));

        second.RequestBodies[0].ShouldContain("\"tool_choice\":\"required\"");
    }

    private static DeepSeekProvider CreateProvider(SequenceHandler handler, bool disableThinking = false)
    {
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DeepSeekProviderConfigKeys.DisableThinking] = disableThinking.ToString()
            })
            .Build();
        var provider = new DeepSeekProvider(
            httpClient,
            NullLogger<DeepSeekProvider>.Instance,
            configuration);

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

    private static LLMProviderRequest RequestWithToolChoice(string? toolChoice, string modelId = "deepseek-v4-flash")
    {
        return new LLMProviderRequest
        {
            Message = "Ändere die Telefonnummer von Frau Müller",
            SystemPrompt = "system",
            ModelId = modelId,
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
