// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Gemini models differ in which thinkingConfig they accept: gemini-3.5-flash-lite rejects
/// thinkingBudget 0 with a generic 400 INVALID_ARGUMENT (live-verified 2026-10-02) while its siblings
/// accept it. A 400 on a request that carried a thinkingConfig is therefore resent once without it,
/// and a successful resend is remembered for the model; a 400 without thinkingConfig and other errors are
/// not retried. The response also reports when the output-token limit cut the answer
/// short and how many tokens the model spent thinking, so callers can tell "spent its budget thinking"
/// from "answered wrongly".
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Gemini;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class GeminiThinkingFallbackTests
{
    private const string GeminiThinkingConfigField = "\"thinkingConfig\"";

    private const string GeminiCompletion =
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"{\\\"ping\\\":\\\"pong\\\"}\"}]},\"finishReason\":\"STOP\"}]," +
        "\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":5}}";

    private const string GeminiThinkingTruncated =
        "{\"candidates\":[{\"content\":{\"role\":\"model\"},\"finishReason\":\"MAX_TOKENS\"}]," +
        "\"usageMetadata\":{\"promptTokenCount\":40,\"totalTokenCount\":87,\"thoughtsTokenCount\":47}}";

    private const string GeminiInvalidArgument =
        "{\"error\":{\"code\":400,\"message\":\"Request contains an invalid argument.\",\"status\":\"INVALID_ARGUMENT\"}}";

    [Test]
    public async Task BadRequestWithThinkingConfig_IsResentOnceWithoutThinkingConfig()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest, GeminiInvalidArgument),
            (HttpStatusCode.OK, GeminiCompletion));

        var response = await Gemini(handler).ProcessAsync(Request("gemini-test-rejects-budget-once", ThinkingBudgetConstants.Disabled));

        response.Success.ShouldBeTrue();
        response.Content.ShouldBe("{\"ping\":\"pong\"}");
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[0].ShouldContain(GeminiThinkingConfigField);
        handler.Bodies[1].ShouldNotContain(GeminiThinkingConfigField);
    }

    [Test]
    public async Task BadRequestWithoutThinkingConfig_IsNotRetried()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest, GeminiInvalidArgument),
            (HttpStatusCode.OK, GeminiCompletion));

        var response = await Gemini(handler).ProcessAsync(Request("gemini-test-no-budget-bad-request", null));

        response.Success.ShouldBeFalse();
        handler.Bodies.Count.ShouldBe(1);
    }

    [Test]
    public async Task BadRequestOnTheRetryToo_ReturnsTheErrorAfterExactlyTwoCalls()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest, GeminiInvalidArgument),
            (HttpStatusCode.BadRequest, GeminiInvalidArgument),
            (HttpStatusCode.OK, GeminiCompletion));

        var response = await Gemini(handler).ProcessAsync(Request("gemini-test-rejects-everything", ThinkingBudgetConstants.Disabled));

        response.Success.ShouldBeFalse();
        handler.Bodies.Count.ShouldBe(2);
    }

    [Test]
    public async Task AfterASuccessfulResend_TheModelGetsNoThinkingConfigUpFront()
    {
        const string model = "gemini-test-remembered-rejection";
        var first = new ScriptedHandler(
            (HttpStatusCode.BadRequest, GeminiInvalidArgument),
            (HttpStatusCode.OK, GeminiCompletion));
        await Gemini(first).ProcessAsync(Request(model, ThinkingBudgetConstants.Disabled));

        var second = new ScriptedHandler((HttpStatusCode.OK, GeminiCompletion));
        var response = await Gemini(second).ProcessAsync(Request(model, ThinkingBudgetConstants.Disabled));

        response.Success.ShouldBeTrue();
        second.Bodies.Count.ShouldBe(1);
        second.Bodies[0].ShouldNotContain(GeminiThinkingConfigField);
    }

    [Test]
    public async Task AFailedResend_IsNotRemembered()
    {
        const string model = "gemini-test-failed-resend";
        var first = new ScriptedHandler(
            (HttpStatusCode.BadRequest, GeminiInvalidArgument),
            (HttpStatusCode.BadRequest, GeminiInvalidArgument));
        await Gemini(first).ProcessAsync(Request(model, ThinkingBudgetConstants.Disabled));

        var second = new ScriptedHandler((HttpStatusCode.OK, GeminiCompletion));
        await Gemini(second).ProcessAsync(Request(model, ThinkingBudgetConstants.Disabled));

        second.Bodies[0].ShouldContain(GeminiThinkingConfigField);
    }

    [Test]
    public async Task ServerErrorWithThinkingConfig_IsNotResentWithoutThinkingConfig()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.InternalServerError, GeminiInvalidArgument),
            (HttpStatusCode.OK, GeminiCompletion));

        var response = await Gemini(handler).ProcessAsync(Request("gemini-test-server-error", ThinkingBudgetConstants.Disabled));

        response.Success.ShouldBeFalse();
        handler.Bodies.Count.ShouldBe(1);
    }

    [Test]
    public async Task MaxTokensWithThoughtsAndNoText_IsReportedAsTruncatedReasoningWithoutContent()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, GeminiThinkingTruncated));

        var response = await Gemini(handler).ProcessAsync(Request("gemini-3.5-flash", null));

        response.Success.ShouldBeTrue();
        response.Content.ShouldBeEmpty();
        response.OutputTruncated.ShouldBeTrue();
        response.ReasoningTokens.ShouldBe(47);
        response.ReasoningWithoutContent.ShouldBeTrue();
    }

    [Test]
    public async Task StopWithText_IsNeitherTruncatedNorReasoningWithoutContent()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, GeminiCompletion));

        var response = await Gemini(handler).ProcessAsync(Request("gemini-3.5-flash", ThinkingBudgetConstants.Disabled));

        response.OutputTruncated.ShouldBeFalse();
        response.ReasoningTokens.ShouldBe(0);
        response.ReasoningWithoutContent.ShouldBeFalse();
    }

    private static LLMProviderRequest Request(string modelId, int? thinkingBudget) => new()
    {
        Message = "Reply with the JSON object as instructed.",
        SystemPrompt = "system",
        ModelId = modelId,
        MaxTokens = 50,
        ThinkingBudgetTokens = thinkingBudget
    };

    private static GeminiProvider Gemini(ScriptedHandler handler)
    {
        var provider = new GeminiProvider(
            new HttpClient(handler), NullLogger<GeminiProvider>.Instance, new ConfigurationBuilder().Build());
        provider.Configure(new LLMProvider
        {
            ProviderId = "google",
            ProviderName = "Google Gemini",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/"
        });
        return provider;
    }

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] script) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = script[Math.Min(Bodies.Count - 1, script.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
