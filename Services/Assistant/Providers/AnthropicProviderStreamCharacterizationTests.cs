// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for AnthropicProvider.ProcessStreamAsync: the guard and HTTP-error exceptions, the SSE line
/// handling (blank and malformed data lines are skipped, nothing after message_stop is read), the per-index tool-call
/// accumulation in first-seen order with a single ToolCallEnd, and when the stream usage is reported.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProviderUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class AnthropicProviderStreamCharacterizationTests
{
    private const string ModelId = "claude-haiku-4-5-20251001";

    private const string MixedTextAndToolsBody = """
        event: message_start
        data: {"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Moment"}}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"tool_b","id":"t1"}}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","name":"tool_a","id":"t2"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"x\":"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{}"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"1}"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":7,"delta":{"type":"input_json_delta","partial_json":"ignored"}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string SkippedLinesBody = """
        : keep-alive comment
        event: content_block_delta
        data:

        event: content_block_delta
        data: {not json

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"A"}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"B"}}

        event: message_stop
        data: {"type":"message_stop"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"after stop"}}

        """;

    private const string DeltaUsageWithoutStartBody = """
        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":99}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private sealed class RecordingLogger : ILogger<AnthropicProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private static (AnthropicProvider Provider, RecordingLogger Logger, FixedResponseHandler Handler) CreateProvider(
        string body, HttpStatusCode status = HttpStatusCode.OK, bool isEnabled = true, string apiKey = "test-key")
    {
        var logger = new RecordingLogger();
        var handler = new FixedResponseHandler(status, body);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.test/v1/"),
        };

        var provider = new AnthropicProvider(httpClient, logger, Substitute.For<IConfiguration>());
        provider.Configure(new Klacks.Api.Domain.Models.Assistant.LLMProvider
        {
            ProviderId = "anthropic",
            ProviderName = "Anthropic",
            ApiKey = apiKey,
            IsEnabled = isEnabled,
            BaseUrl = "https://api.anthropic.test/v1/",
            ApiVersion = "2023-06-01",
        });

        return (provider, logger, handler);
    }

    private static LLMProviderRequest CreateRequest(bool withTools = false) => new()
    {
        Message = "hi",
        SystemPrompt = "stable prefix",
        ModelId = ModelId,
        Stream = true,
        AvailableFunctions = withTools
            ? new List<Klacks.Api.Domain.Models.Assistant.LLMFunction>
            {
                new() { Name = "tool_a", Description = "A tool" }
            }
            : new List<Klacks.Api.Domain.Models.Assistant.LLMFunction>(),
        ToolChoice = withTools ? MutationGuardConstants.ToolChoiceRequired : null,
    };

    private static async Task<List<string>> CollectAsync(AnthropicProvider provider, LLMProviderRequest request)
    {
        var tokens = new List<string>();
        await foreach (var token in provider.ProcessStreamAsync(request, CancellationToken.None))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static JsonElement ParseToolToken(string token)
    {
        token.ShouldStartWith(LLMStreamingTokens.ToolCallPrefix);
        return JsonSerializer.Deserialize<JsonElement>(token[LLMStreamingTokens.ToolCallPrefix.Length..]);
    }

    [Test]
    public async Task DisabledProvider_ThrowsOnFirstMoveNext_WithoutCallingTheApi()
    {
        var (provider, _, handler) = CreateProvider(MixedTextAndToolsBody, isEnabled: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CollectAsync(provider, CreateRequest()));

        ex.Message.ShouldBe("Anthropic provider is not enabled");
        handler.Calls.ShouldBe(0);
    }

    [Test]
    public async Task MissingApiKey_Throws_TheProviderNotAvailableMessage()
    {
        var (provider, _, handler) = CreateProvider(MixedTextAndToolsBody, apiKey: string.Empty);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CollectAsync(provider, CreateRequest()));

        ex.Message.ShouldBe("The provider for the selected model is not available.");
        handler.Calls.ShouldBe(0);
    }

    [Test]
    public async Task HttpErrorWithAnthropicErrorBody_ThrowsTheApiMessage_AndLogsTheError()
    {
        const string errorBody = "{\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-x\"}}";
        var (provider, logger, _) = CreateProvider(errorBody, HttpStatusCode.NotFound);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CollectAsync(provider, CreateRequest()));

        ex.Message.ShouldBe("model: claude-x");
        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Error
            && e.Message.StartsWith("Anthropic streaming API error for model " + ModelId + ": NotFound - ", StringComparison.Ordinal));
    }

    [Test]
    public async Task HttpErrorWithoutJsonBody_ThrowsTheStatusCodeMessage()
    {
        var (provider, _, _) = CreateProvider("upstream overloaded", HttpStatusCode.TooManyRequests);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CollectAsync(provider, CreateRequest()));

        ex.Message.ShouldBe("Anthropic rate limit exceeded");
    }

    [Test]
    public async Task RequestLog_ReportsToolChoiceAndToolCount()
    {
        var (provider, logger, _) = CreateProvider(MixedTextAndToolsBody);

        await CollectAsync(provider, CreateRequest(withTools: true));
        await CollectAsync(provider, CreateRequest(withTools: false));

        logger.Entries.Where(e => e.Message.StartsWith("Anthropic stream request:", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ShouldBe(
            [
                "Anthropic stream request: tool_choice=any toolCount=1",
                "Anthropic stream request: tool_choice=null toolCount=0"
            ]);
    }

    [Test]
    public async Task TextAndTwoToolCalls_YieldTextInStreamOrder_ThenToolsInFirstSeenIndexOrder_ThenOneToolEnd()
    {
        var (provider, _, _) = CreateProvider(MixedTextAndToolsBody);

        var tokens = await CollectAsync(provider, CreateRequest(withTools: true));

        tokens.Count.ShouldBe(4);
        tokens[0].ShouldBe("Moment");

        var first = ParseToolToken(tokens[1]);
        first.GetProperty("toolCall").GetBoolean().ShouldBeTrue();
        first.GetProperty("index").GetInt32().ShouldBe(1);
        first.GetProperty("name").GetString().ShouldBe("tool_b");
        first.GetProperty("arguments").GetString().ShouldBe("{\"x\":1}");

        var second = ParseToolToken(tokens[2]);
        second.GetProperty("index").GetInt32().ShouldBe(2);
        second.GetProperty("name").GetString().ShouldBe("tool_a");
        second.GetProperty("arguments").GetString().ShouldBe("{}");

        tokens[3].ShouldBe(LLMStreamingTokens.ToolCallEnd);
    }

    [Test]
    public async Task ToolToken_PropertyOrder_IsToolCallIndexNameArguments()
    {
        var (provider, _, _) = CreateProvider(MixedTextAndToolsBody);

        var tokens = await CollectAsync(provider, CreateRequest(withTools: true));

        ParseToolToken(tokens[1]).EnumerateObject().Select(p => p.Name)
            .ShouldBe(["toolCall", "index", "name", "arguments"]);
    }

    [Test]
    public async Task ResultLog_ReportsTheToolCallCount()
    {
        var (provider, logger, _) = CreateProvider(MixedTextAndToolsBody);

        await CollectAsync(provider, CreateRequest(withTools: true));

        logger.Entries.ShouldContain(e =>
            e.Level == LogLevel.Information
            && e.Message == "Anthropic stream result: hasToolCalls=True toolCallCount=2");
    }

    [Test]
    public async Task BlankAndMalformedDataLines_AreSkipped_AToolStartWithoutName_IsIgnored_AndNothingAfterMessageStopIsRead()
    {
        var (provider, logger, _) = CreateProvider(SkippedLinesBody);

        var tokens = await CollectAsync(provider, CreateRequest());

        tokens.ShouldBe(["A", "B"]);
        logger.Entries.ShouldContain(e => e.Message == "Anthropic stream result: hasToolCalls=False toolCallCount=0");
    }

    [Test]
    public async Task UsageOnlyInMessageDelta_WithoutMessageStart_IsNeverReported()
    {
        var (provider, logger, _) = CreateProvider(DeltaUsageWithoutStartBody);
        var request = CreateRequest();
        ProviderUsage? received = null;
        request.OnStreamUsage = usage => received = usage;

        var tokens = await CollectAsync(provider, request);

        tokens.ShouldBe(["Hi"]);
        received.ShouldBeNull();
        logger.Entries.ShouldNotContain(e => e.Message.Contains("prompt-cache"));
    }

    [Test]
    public async Task UsageFromMessageStart_IsReportedOnce_BeforeTheToolTokensAreYielded()
    {
        var (provider, _, _) = CreateProvider(MixedTextAndToolsBody);
        var request = CreateRequest(withTools: true);
        var usageCalls = 0;
        var tokensSeenWhenUsageArrived = -1;
        var tokens = new List<string>();
        request.OnStreamUsage = usage =>
        {
            usageCalls++;
            tokensSeenWhenUsageArrived = tokens.Count;
            usage.InputTokens.ShouldBe(10);
            usage.OutputTokens.ShouldBe(1);
        };

        await foreach (var token in provider.ProcessStreamAsync(request, CancellationToken.None))
        {
            tokens.Add(token);
        }

        usageCalls.ShouldBe(1);
        tokensSeenWhenUsageArrived.ShouldBe(1);
    }
}
