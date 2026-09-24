// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A reasoning model that writes only reasoning_content and neither content nor a tool call must produce an
/// empty answer - on the streaming and the non-streaming path of both OpenAI-compatible providers that read
/// the channel (DeepSeek, Generic). Until 2026-09-24 the reasoning was returned as the answer and users saw
/// the model's deliberation as Klacksy's reply. The reasoning text may only reach the Debug log.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Base;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class ReasoningOnlyProviderResponseTests
{
    private const string Reasoning = "We need to call manage_pending_notes? But no tools are available";
    private const string Answer = "Soll ich die Gruppe anlegen?";
    private const string BaseUrl = "https://provider.test/v1/";

    private const string ReasoningOnlyStream =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"" + Reasoning + "\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private const string ReasoningThenContentStream =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"" + Reasoning + "\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + Answer + "\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private const string ReasoningOnlyCompletion =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":\"" + Reasoning +
        "\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}";

    public enum ProviderKind
    {
        DeepSeek,
        Generic
    }

    [TestCase(ProviderKind.DeepSeek)]
    [TestCase(ProviderKind.Generic)]
    public async Task Streaming_ReasoningOnly_YieldsNoContentAndWarns(ProviderKind kind)
    {
        var logger = new RecordingLogger<ReasoningOnlyProviderResponseTests>();
        var provider = Create(kind, ReasoningOnlyStream, "text/event-stream", logger);

        var tokens = await StreamAsync(provider);

        string.Concat(tokens).ShouldNotContain(Reasoning);
        tokens.ShouldBeEmpty();
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning);
        ReasoningOnlyAtDebug(logger);
    }

    [TestCase(ProviderKind.DeepSeek)]
    [TestCase(ProviderKind.Generic)]
    public async Task Streaming_ReasoningThenContent_YieldsOnlyTheContent(ProviderKind kind)
    {
        var logger = new RecordingLogger<ReasoningOnlyProviderResponseTests>();
        var provider = Create(kind, ReasoningThenContentStream, "text/event-stream", logger);

        var tokens = await StreamAsync(provider);

        string.Concat(tokens).ShouldBe(Answer);
        logger.Entries.ShouldNotContain(entry => entry.Level == LogLevel.Warning);
    }

    [TestCase(ProviderKind.DeepSeek)]
    [TestCase(ProviderKind.Generic)]
    public async Task NonStreaming_ReasoningOnly_ReturnsEmptyContentAndTheFlag(ProviderKind kind)
    {
        var logger = new RecordingLogger<ReasoningOnlyProviderResponseTests>();
        var provider = Create(kind, ReasoningOnlyCompletion, "application/json", logger);

        var response = await provider.ProcessAsync(Request(stream: false));

        response.Success.ShouldBeTrue(response.Error);
        response.Content.ShouldBe(string.Empty);
        response.ReasoningWithoutContent.ShouldBeTrue();
        ReasoningOnlyAtDebug(logger);
    }

    private static void ReasoningOnlyAtDebug(RecordingLogger<ReasoningOnlyProviderResponseTests> logger) =>
        logger.Entries
            .Where(entry => entry.Level > LogLevel.Debug)
            .ShouldNotContain(entry => entry.Message.Contains(Reasoning));

    private static async Task<List<string>> StreamAsync(BaseHttpProvider provider)
    {
        var tokens = new List<string>();
        await foreach (var token in provider.ProcessStreamAsync(Request(stream: true), CancellationToken.None))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static LLMProviderRequest Request(bool stream) => new()
    {
        Message = "Erstelle eine Gruppe",
        SystemPrompt = "system",
        ModelId = "reasoning-model-test",
        Stream = stream
    };

    private static BaseHttpProvider Create(
        ProviderKind kind, string body, string mediaType, RecordingLogger<ReasoningOnlyProviderResponseTests> logger)
    {
        var httpClient = new HttpClient(new FixedResponseHandler(body, mediaType)) { BaseAddress = new Uri(BaseUrl) };
        var configuration = new ConfigurationBuilder().Build();
        BaseHttpProvider provider = kind == ProviderKind.DeepSeek
            ? new DeepSeekProvider(httpClient, new CategoryLogger<DeepSeekProvider>(logger), configuration)
            : new GenericOpenAICompatibleProvider(
                httpClient, new CategoryLogger<GenericOpenAICompatibleProvider>(logger), configuration);

        provider.Configure(new LLMProvider
        {
            ProviderId = kind.ToString().ToLowerInvariant(),
            ProviderName = kind.ToString(),
            ApiKey = "test-key",
            IsEnabled = true,
            BaseUrl = BaseUrl
        });

        return provider;
    }

    private sealed class CategoryLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class FixedResponseHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
