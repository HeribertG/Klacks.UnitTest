// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Gemini API key must travel only in the x-goog-api-key header: never in a request URI, never in
/// a log line, never in an exception or error text. Live trigger 2026-10-02: the demo backend's debug
/// log carried "generateContent?key=..." in every provider request line.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Gemini;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class GeminiProviderApiKeyExposureTests
{
    private const string ConfiguredKey = "SENTINEL-GEMINI-KEY-0815";
    private const string CandidateKey = "SENTINEL-CANDIDATE-KEY-0816";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
    private const string QueryKeyMarker = "key=";

    private const string Completion =
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]," +
        "\"usageMetadata\":{\"promptTokenCount\":3,\"candidatesTokenCount\":1}}";

    private const string BadRequest =
        "{\"error\":{\"code\":400,\"message\":\"Request contains an invalid argument.\",\"status\":\"INVALID_ARGUMENT\"}}";

    private const string StreamBody =
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]}}]}\n\n";

    private const string ModelsBody = "{\"models\":[{\"name\":\"models/gemini-test\",\"displayName\":\"Gemini Test\"}]}";

    [Test]
    public async Task ASuccessfulRequest_SendsTheKeyOnlyInTheHeader()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, Completion));
        var (provider, logger) = Gemini(handler);

        var response = await provider.ProcessAsync(Request());

        response.Success.ShouldBeTrue();
        AssertKeyOnlyInHeader(handler, ConfiguredKey);
        AssertNotLogged(logger);
    }

    [Test]
    public async Task ARejectedRequest_KeepsTheKeyOutOfLogsAndError()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.BadRequest, BadRequest));
        var (provider, logger) = Gemini(handler);

        var response = await provider.ProcessAsync(Request());

        response.Success.ShouldBeFalse();
        (response.Error ?? string.Empty).ShouldNotContain(ConfiguredKey);
        AssertKeyOnlyInHeader(handler, ConfiguredKey);
        AssertNotLogged(logger);
    }

    [Test]
    public async Task ATransportFailure_KeepsTheKeyOutOfLogsAndError()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("No such host is known."));
        var (provider, logger) = Gemini(handler);

        var response = await provider.ProcessAsync(Request());

        response.Success.ShouldBeFalse();
        (response.Error ?? string.Empty).ShouldNotContain(ConfiguredKey);
        AssertKeyOnlyInHeader(handler, ConfiguredKey);
        AssertNotLogged(logger);
    }

    [Test]
    public async Task AStreamedRequest_SendsTheKeyOnlyInTheHeader()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, StreamBody, "text/event-stream"));
        var (provider, logger) = Gemini(handler);

        await foreach (var _ in provider.ProcessStreamAsync(Request()))
        {
        }

        AssertKeyOnlyInHeader(handler, ConfiguredKey);
        handler.Uris.ShouldAllBe(uri => uri.Contains("alt=sse"));
        AssertNotLogged(logger);
    }

    [Test]
    public async Task ModelDiscovery_SendsTheKeyOnlyInTheHeader()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, ModelsBody));
        var (provider, logger) = Gemini(handler);

        var models = await provider.GetAvailableModelsAsync();

        models.ShouldNotBeNull();
        models.Count.ShouldBe(1);
        AssertKeyOnlyInHeader(handler, ConfiguredKey);
        AssertNotLogged(logger);
    }

    [Test]
    public async Task KeyValidation_SendsExactlyTheCandidateKeyInTheHeader()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, ModelsBody));
        var (provider, logger) = Gemini(handler);

        var valid = await provider.ValidateApiKeyAsync(CandidateKey);

        valid.ShouldBeTrue();
        AssertKeyOnlyInHeader(handler, CandidateKey);
        AssertNotLogged(logger);
    }

    [Test]
    public void ReconfiguringTheProvider_LeavesASingleKeyHeader()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, Completion));
        var httpClient = new HttpClient(handler);
        var provider = new GeminiProvider(httpClient, new RecordingLogger<GeminiProvider>(), new ConfigurationBuilder().Build());

        provider.Configure(Config());
        provider.Configure(Config());

        httpClient.DefaultRequestHeaders.GetValues(GoogleApiConstants.ApiKeyHeaderName).ShouldBe([ConfiguredKey]);
    }

    private static void AssertKeyOnlyInHeader(RecordingHandler handler, string expectedKey)
    {
        handler.Uris.ShouldNotBeEmpty();
        foreach (var uri in handler.Uris)
        {
            uri.ShouldNotContain(ConfiguredKey);
            uri.ShouldNotContain(CandidateKey);
            uri.ShouldNotContain(QueryKeyMarker);
        }

        handler.KeyHeaders.ShouldAllBe(values => values.Length == 1 && values[0] == expectedKey);
    }

    private static void AssertNotLogged(RecordingLogger<GeminiProvider> logger)
    {
        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain(ConfiguredKey);
            entry.Message.ShouldNotContain(CandidateKey);
            (entry.Exception?.ToString() ?? string.Empty).ShouldNotContain(ConfiguredKey);
        }
    }

    private static LLMProviderRequest Request() => new()
    {
        Message = "Reply with ok",
        SystemPrompt = "system",
        ModelId = "gemini-exposure-test",
        MaxTokens = 5
    };

    private static LLMProvider Config() => new()
    {
        ProviderId = "google",
        ProviderName = "Google Gemini",
        ApiKey = ConfiguredKey,
        IsEnabled = true,
        BaseUrl = BaseUrl
    };

    private static (GeminiProvider Provider, RecordingLogger<GeminiProvider> Logger) Gemini(RecordingHandler handler)
    {
        var logger = new RecordingLogger<GeminiProvider>();
        var provider = new GeminiProvider(new HttpClient(handler), logger, new ConfigurationBuilder().Build());
        provider.Configure(Config());
        return (provider, logger);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Uris { get; } = new();

        public List<string[]> KeyHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
            KeyHeaders.Add(request.Headers.TryGetValues(GoogleApiConstants.ApiKeyHeaderName, out var values)
                ? values.ToArray()
                : []);
            return Task.FromResult(respond(request));
        }
    }
}
