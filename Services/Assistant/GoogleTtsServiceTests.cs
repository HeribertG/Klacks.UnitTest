// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Services.Assistant;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using LLMProvider = Klacks.Api.Domain.Models.Assistant.LLMProvider;

[TestFixture]
public class GoogleTtsServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _jsonBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public CapturingHandler(HttpStatusCode status, string jsonBody)
        {
            _status = status;
            _jsonBody = jsonBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private ILLMRepository _llmRepository = null!;
    private ILogger<GoogleTtsService> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _llmRepository = Substitute.For<ILLMRepository>();
        _logger = Substitute.For<ILogger<GoogleTtsService>>();
    }

    private static IHttpClientFactory FactoryReturning(CapturingHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        return factory;
    }

    private void GivenKey(string apiKey)
    {
        _llmRepository.GetProviderByIdAsync(GoogleTtsConstants.LlmProviderId)
            .Returns(Task.FromResult<LLMProvider?>(new LLMProvider { ProviderId = GoogleTtsConstants.LlmProviderId, ApiKey = apiKey }));
    }

    private static string AudioJson(byte[] audio)
    {
        return $"{{\"audioContent\":\"{Convert.ToBase64String(audio)}\"}}";
    }

    [Test]
    public async Task SynthesizeAsync_WhenKeyConfigured_DecodesBase64AudioContent()
    {
        var audio = new byte[] { 5, 6, 7, 8 };
        var handler = new CapturingHandler(HttpStatusCode.OK, AudioJson(audio));
        GivenKey("AIza-test-key");
        var service = new GoogleTtsService(FactoryReturning(handler), _llmRepository, _logger);

        var result = await service.SynthesizeAsync("Hallo", "de-DE-Neural2-C", "de");

        result.ShouldBe(audio);
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("key=AIza-test-key");
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("voice").GetProperty("name").GetString().ShouldBe("de-DE-Neural2-C");
        doc.RootElement.GetProperty("voice").GetProperty("languageCode").GetString().ShouldBe("de-DE");
    }

    [Test]
    public async Task SynthesizeAsync_WhenVoiceUnknown_FallsBackToLocaleDefault()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, AudioJson(new byte[] { 1 }));
        GivenKey("AIza-test");
        var service = new GoogleTtsService(FactoryReturning(handler), _llmRepository, _logger);

        await service.SynthesizeAsync("text", TtsProviderConstants.AutoVoice, "de");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("voice").GetProperty("name").GetString().ShouldBe(GoogleTtsConstants.LocaleDefaults["de"]);
    }

    [Test]
    public async Task SynthesizeAsync_WhenNoKey_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, AudioJson(System.Array.Empty<byte>()));
        GivenKey(string.Empty);
        var service = new GoogleTtsService(FactoryReturning(handler), _llmRepository, _logger);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.SynthesizeAsync("text", TtsProviderConstants.AutoVoice, "de"));
    }

    [Test]
    public async Task GetVoicesAsync_ReturnsCuratedVoicesWithLanguageCode()
    {
        var service = new GoogleTtsService(Substitute.For<IHttpClientFactory>(), _llmRepository, _logger);

        var voices = await service.GetVoicesAsync();

        voices.Count.ShouldBe(GoogleTtsConstants.Voices.Count);
        voices.First(v => v.VoiceId == "de-DE-Neural2-B").Locale.ShouldBe("de-DE");
    }
}
