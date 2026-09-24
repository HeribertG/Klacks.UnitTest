// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// ThinkingBudgetConstants.Disabled on the wire. DeepSeek has no budget, only on/off: Disabled turns thinking
/// off, a missing budget leaves the field out, and the DisableThinking configuration wins either way.
/// Gemini keeps receiving thinkingBudget 0 for every model except the Pro models, which cannot switch
/// thinking off; for them the thinkingConfig is left out instead of risking a rejected request. Any other
/// budget is passed through unchanged.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Gemini;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class ProviderThinkingBudgetTests
{
    private const string OpenAiCompletion =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}";

    private const string GeminiCompletion =
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]}}]," +
        "\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1}}";

    private const string ThinkingDisabledField = "\"thinking\":{\"type\":\"disabled\"}";
    private const string ThinkingField = "\"thinking\"";
    private const string GeminiThinkingConfigField = "\"thinkingConfig\"";

    [Test]
    public async Task DeepSeek_DisabledBudget_SendsThinkingDisabled()
    {
        var handler = new CapturingHandler(OpenAiCompletion);

        await DeepSeek(handler).ProcessAsync(Request("deepseek-test-budget-zero", ThinkingBudgetConstants.Disabled));

        handler.Body.ShouldContain(ThinkingDisabledField);
    }

    [Test]
    public async Task DeepSeek_NoBudget_LeavesTheThinkingFieldOut()
    {
        var handler = new CapturingHandler(OpenAiCompletion);

        await DeepSeek(handler).ProcessAsync(Request("deepseek-test-budget-null", null));

        handler.Body.ShouldNotContain(ThinkingField);
    }

    [Test]
    public async Task DeepSeek_PositiveBudget_LeavesTheThinkingFieldOut()
    {
        var handler = new CapturingHandler(OpenAiCompletion);

        await DeepSeek(handler).ProcessAsync(Request("deepseek-test-budget-positive", 1024));

        handler.Body.ShouldNotContain(ThinkingField);
    }

    [Test]
    public async Task DeepSeek_DisableThinkingConfigured_SendsThinkingDisabledRegardlessOfTheBudget()
    {
        var handler = new CapturingHandler(OpenAiCompletion);

        await DeepSeek(handler, disableThinking: true).ProcessAsync(Request("deepseek-test-budget-configured", null));

        handler.Body.ShouldContain(ThinkingDisabledField);
    }

    [TestCase("gemini-2.5-flash")]
    [TestCase("gemini-2.5-flash-lite")]
    [TestCase("gemini-3-flash-preview")]
    [TestCase("gemini-3.1-flash-lite-preview")]
    public async Task Gemini_DisabledBudgetOnANonProModel_SendsThinkingBudgetZero(string model)
    {
        var handler = new CapturingHandler(GeminiCompletion);

        await Gemini(handler).ProcessAsync(Request(model, ThinkingBudgetConstants.Disabled));

        handler.Body.ShouldContain("\"thinkingConfig\":{\"thinkingBudget\":0}");
    }

    [TestCase("gemini-3.1-pro-preview")]
    [TestCase("gemini-2.5-pro")]
    public async Task Gemini_DisabledBudgetOnAProModel_LeavesTheThinkingConfigOut(string model)
    {
        var handler = new CapturingHandler(GeminiCompletion);

        await Gemini(handler).ProcessAsync(Request(model, ThinkingBudgetConstants.Disabled));

        handler.Body.ShouldNotContain(GeminiThinkingConfigField);
    }

    [Test]
    public async Task Gemini_PositiveBudget_IsPassedThrough()
    {
        var handler = new CapturingHandler(GeminiCompletion);

        await Gemini(handler).ProcessAsync(Request("gemini-3.1-pro-preview", 2048));

        handler.Body.ShouldContain("\"thinkingConfig\":{\"thinkingBudget\":2048}");
    }

    [Test]
    public async Task Gemini_NoBudget_LeavesTheThinkingConfigOut()
    {
        var handler = new CapturingHandler(GeminiCompletion);

        await Gemini(handler).ProcessAsync(Request("gemini-2.5-flash", null));

        handler.Body.ShouldNotContain(GeminiThinkingConfigField);
    }

    private static LLMProviderRequest Request(string modelId, int? thinkingBudget) => new()
    {
        Message = "Hallo",
        SystemPrompt = "system",
        ModelId = modelId,
        ThinkingBudgetTokens = thinkingBudget
    };

    private static DeepSeekProvider DeepSeek(CapturingHandler handler, bool disableThinking = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DeepSeekProviderConfigKeys.DisableThinking] = disableThinking.ToString()
            })
            .Build();
        var provider = new DeepSeekProvider(new HttpClient(handler), NullLogger<DeepSeekProvider>.Instance, configuration);
        provider.Configure(Config("deepseek", "DeepSeek", "https://deepseek.test/v1/"));
        return provider;
    }

    private static GeminiProvider Gemini(CapturingHandler handler)
    {
        var provider = new GeminiProvider(
            new HttpClient(handler), NullLogger<GeminiProvider>.Instance, new ConfigurationBuilder().Build());
        provider.Configure(Config("google", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/"));
        return provider;
    }

    private static LLMProvider Config(string id, string name, string baseUrl) => new()
    {
        ProviderId = id,
        ProviderName = name,
        ApiKey = "test-key",
        IsEnabled = true,
        BaseUrl = baseUrl
    };

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
