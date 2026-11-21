// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies LocalTtsService against a recording HTTP handler: it posts the Klacks.Stt speech contract to the stored
/// base URL, reports WAV and internal chunking, splits long texts and joins the WAV answers, answers an unsupported
/// language with empty audio, and throws the not-configured error without any HTTP call when the voice is not
/// installed, and for a missing model or an unreachable service; a busy service (429) becomes TtsProviderBusyException
/// carrying its Retry-After. There is no cloud fallback.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class LocalTtsServiceTests
{
    private const string BaseUrl = "http://klacks-stt:8000/";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;

        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        public RecordingHandler(Func<int, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return _respond(Calls.Count - 1);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private ISettingsReader _settings = null!;
    private IHttpClientFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _settings = Substitute.For<ISettingsReader>();
        _factory = Substitute.For<IHttpClientFactory>();
    }

    private void Installed(string? baseUrl = BaseUrl) =>
        _settings.GetSetting(Klacks.Api.Application.Constants.Settings.ASSISTANT_TTS_LOCAL_BASE_URL)
            .Returns(baseUrl is null ? null : new SettingsModel { Type = Klacks.Api.Application.Constants.Settings.ASSISTANT_TTS_LOCAL_BASE_URL, Value = baseUrl });

    private LocalTtsService Service(HttpMessageHandler handler)
    {
        _factory.CreateClient(LocalTtsConstants.HttpClientName).Returns(_ => new HttpClient(handler));
        return new LocalTtsService(_factory, _settings, Substitute.For<ILogger<LocalTtsService>>());
    }

    private static HttpResponseMessage WavResponse(byte[] samples) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(WavConcatenatorTests.Wav(samples)) };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string code) =>
        new(status)
        {
            Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"x\"}}}}", Encoding.UTF8, "application/json"),
        };

    [Test]
    public void Provider_ReportsLocalIdWavAndInternalChunking()
    {
        var service = Service(new RecordingHandler(_ => WavResponse([1, 2])));

        service.ProviderId.ShouldBe(TtsProviderConstants.Local);
        service.MediaType.ShouldBe(TtsProviderConstants.WavMediaType);
        service.ChunksInternally.ShouldBeTrue();
    }

    [Test]
    public async Task GetVoicesAsync_ReturnsTenSupertonicVoices()
    {
        var voices = await Service(new RecordingHandler(_ => WavResponse([1]))).GetVoicesAsync();

        voices.Count.ShouldBe(10);
        voices[0].ShouldBe(new TtsVoice("supertonic:F1", TtsProviderConstants.AutoVoice, "F1"));
        voices[9].VoiceId.ShouldBe("supertonic:M5");
    }

    [Test]
    public async Task SynthesizeAsync_PostsSpeechContractToStoredBaseUrl()
    {
        Installed();
        var handler = new RecordingHandler(_ => WavResponse([1, 2, 3, 4]));

        var audio = await Service(handler).SynthesizeAsync("Guten Tag.", "supertonic:M2", "de-CH");

        audio.ShouldBe(WavConcatenatorTests.Wav([1, 2, 3, 4]));
        handler.Calls.Count.ShouldBe(1);
        var (request, body) = handler.Calls[0];
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("http://klacks-stt:8000/v1/audio/speech");
        request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("input").GetString().ShouldBe("Guten Tag.");
        json.RootElement.GetProperty("voice").GetString().ShouldBe("supertonic:M2");
        json.RootElement.GetProperty("language").GetString().ShouldBe("de");
    }

    [TestCase("auto")]
    [TestCase("nova")]
    public async Task SynthesizeAsync_UnknownVoice_UsesDefaultVoice(string voiceId)
    {
        Installed();
        var handler = new RecordingHandler(_ => WavResponse([1, 2]));

        await Service(handler).SynthesizeAsync("Test.", voiceId, "en");

        using var json = JsonDocument.Parse(handler.Calls[0].Body);
        json.RootElement.GetProperty("voice").GetString().ShouldBe(LocalTtsConstants.DefaultVoiceId);
    }

    [Test]
    public async Task SynthesizeAsync_LongText_SplitsIntoPiecesAndJoinsTheWavAnswers()
    {
        Installed();
        var handler = new RecordingHandler(call => WavResponse([(byte)(call * 2 + 1), (byte)(call * 2 + 2)]));
        var text = string.Concat(Enumerable.Repeat("Dies ist ein Satz für die Planung. ", 300));
        text.Length.ShouldBeGreaterThan(LocalTtsConstants.MaxRequestTextLength * 2);

        var audio = await Service(handler).SynthesizeAsync(text, "supertonic:F1", "de");

        handler.Calls.Count.ShouldBeGreaterThanOrEqualTo(3);
        handler.Calls.ShouldAllBe(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("input").GetString()!.Length <= LocalTtsConstants.MaxRequestTextLength);
        var expectedSamples = Enumerable.Range(0, handler.Calls.Count).SelectMany(i => new[] { (byte)(i * 2 + 1), (byte)(i * 2 + 2) }).ToArray();
        audio.ShouldBe(WavConcatenatorTests.Wav(expectedSamples));
    }

    [Test]
    public async Task SynthesizeAsync_UnsupportedLanguage_ReturnsEmptyAudio()
    {
        Installed();
        var handler = new RecordingHandler(_ => ErrorResponse(HttpStatusCode.BadRequest, LocalTtsConstants.ErrorUnsupportedLanguage));

        var audio = await Service(handler).SynthesizeAsync("Bonġu.", "supertonic:F1", "mt");

        audio.ShouldBeEmpty();
        handler.Calls.Count.ShouldBe(1);
        using var json = JsonDocument.Parse(handler.Calls[0].Body);
        json.RootElement.GetProperty("language").GetString().ShouldBe("mt");
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task SynthesizeAsync_NotInstalled_ThrowsWithoutHttpCall(string? baseUrl)
    {
        Installed(baseUrl);
        var handler = new RecordingHandler(_ => WavResponse([1]));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service(handler).SynthesizeAsync("Test.", "auto", "de"));

        ex.Message.ShouldBe(LocalTtsConstants.NotInstalledMessage);
        handler.Calls.ShouldBeEmpty();
        _factory.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    [Test]
    public async Task SynthesizeAsync_ModelNotInstalled_ThrowsNotConfigured()
    {
        Installed();
        var handler = new RecordingHandler(_ => ErrorResponse(HttpStatusCode.ServiceUnavailable, LocalTtsConstants.ErrorModelNotInstalled));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service(handler).SynthesizeAsync("Test.", "auto", "de"));

        ex.Message.ShouldBe(LocalTtsConstants.NotInstalledMessage);
    }

    [Test]
    public async Task SynthesizeAsync_ServiceUnreachable_ThrowsNotConfigured()
    {
        Installed();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service(new ThrowingHandler()).SynthesizeAsync("Test.", "auto", "de"));

        ex.Message.ShouldBe(LocalTtsConstants.NotInstalledMessage);
    }

    [Test]
    public async Task SynthesizeAsync_Busy_ThrowsBusyWithRetryAfter()
    {
        Installed();
        var handler = new RecordingHandler(_ =>
        {
            var response = ErrorResponse(HttpStatusCode.TooManyRequests, "busy");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return response;
        });

        var ex = await Should.ThrowAsync<TtsProviderBusyException>(() => Service(handler).SynthesizeAsync("Test.", "auto", "de"));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task SynthesizeAsync_OtherError_ThrowsSynthesisFailure()
    {
        Installed();
        var handler = new RecordingHandler(_ => ErrorResponse(HttpStatusCode.InternalServerError, "synthesis_failed"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service(handler).SynthesizeAsync("Test.", "auto", "de"));

        ex.Message.ShouldNotBe(LocalTtsConstants.NotInstalledMessage);
        ex.Message.ShouldContain("500");
    }
}
