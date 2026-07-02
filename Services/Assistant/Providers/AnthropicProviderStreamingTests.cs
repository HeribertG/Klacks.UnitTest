// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class AnthropicProviderStreamingTests
{
    private const string ToolUseStreamBody =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\"}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"name\":\"navigate_to\",\"id\":\"toolu_1\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"page\\\":\\\"settings\\\",\\\"target\\\":\\\"macros\\\"}\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private const string TextOnlyStreamBody =
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hallo\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" Welt\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StreamingHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(response);
        }
    }

    private static AnthropicProvider CreateProvider(string sseBody)
    {
        var httpClient = new HttpClient(new StreamingHandler(sseBody))
        {
            BaseAddress = new Uri("https://api.anthropic.test/v1/"),
        };
        var provider = new AnthropicProvider(httpClient, Substitute.For<ILogger<AnthropicProvider>>(), Substitute.For<IConfiguration>());

        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "anthropic",
            ProviderName = "Anthropic",
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = "https://api.anthropic.test/v1/",
            ApiVersion = "2023-06-01",
        });

        return provider;
    }

    private static LLMProviderRequest CreateNavigationRequest()
    {
        return new LLMProviderRequest
        {
            Message = "Bring mich zu Macros",
            SystemPrompt = "system",
            ModelId = "claude-haiku-4-5-20251001",
            AvailableFunctions = new List<Klacks.Api.Domain.Models.Assistant.LLMFunction>
            {
                new() { Name = "navigate_to", Description = "Navigate to a page" },
            },
            ToolChoice = MutationGuardConstants.ToolChoiceRequired,
            Stream = true,
        };
    }

    [Test]
    public async Task ProcessStreamAsync_WhenModelCallsTool_YieldsToolTokenBeforeToolEnd()
    {
        var provider = CreateProvider(ToolUseStreamBody);

        var tokens = new List<string>();
        await foreach (var token in provider.ProcessStreamAsync(CreateNavigationRequest(), CancellationToken.None))
        {
            tokens.Add(token);
        }

        var toolToken = tokens.SingleOrDefault(t => t.StartsWith(LLMStreamingTokens.ToolCallPrefix, StringComparison.Ordinal));
        toolToken.ShouldNotBeNull();
        toolToken.ShouldContain("\"name\":\"navigate_to\"");
        tokens.Last().ShouldBe(LLMStreamingTokens.ToolCallEnd);
    }

    [Test]
    public async Task ProcessStreamAsync_WhenModelCallsTool_ToolCallSurvivesLLMServiceParsing()
    {
        var provider = CreateProvider(ToolUseStreamBody);

        var accumulator = new StreamAccumulator();
        var hasToolEnd = false;

        await foreach (var token in provider.ProcessStreamAsync(CreateNavigationRequest(), CancellationToken.None))
        {
            if (token.StartsWith(LLMStreamingTokens.ToolCallPrefix, StringComparison.Ordinal))
            {
                var toolJson = token[LLMStreamingTokens.ToolCallPrefix.Length..];
                var toolData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(toolJson);
                var index = toolData.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                var name = toolData.TryGetProperty("name", out var n) ? n.GetString() : null;
                var args = toolData.TryGetProperty("arguments", out var a) ? a.GetString() : null;
                accumulator.AppendToolCallDelta(index, name, args);
            }
            else if (token == LLMStreamingTokens.ToolCallEnd)
            {
                hasToolEnd = true;
            }
        }

        hasToolEnd.ShouldBeTrue();
        accumulator.FinalizeFunctionCalls();
        accumulator.HasFunctionCalls.ShouldBeTrue();
        accumulator.FunctionCalls.Single().FunctionName.ShouldBe("navigate_to");
    }

    [Test]
    public async Task ProcessStreamAsync_WhenModelReturnsPlainText_YieldsTextDeltasOnly()
    {
        var provider = CreateProvider(TextOnlyStreamBody);

        var tokens = new List<string>();
        await foreach (var token in provider.ProcessStreamAsync(CreateNavigationRequest(), CancellationToken.None))
        {
            tokens.Add(token);
        }

        tokens.ShouldBe(new[] { "Hallo", " Welt" });
    }
}
