// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Services.Assistant;

using System.Net;
using System.Net.Http;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using LLMProvider = Klacks.Api.Domain.Models.Assistant.LLMProvider;

[TestFixture]
public class ElevenLabsTtsServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _payload;

        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(HttpStatusCode status, byte[] payload)
        {
            _status = status;
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(_payload) });
        }
    }

    private ILLMRepository _llmRepository = null!;
    private ILogger<ElevenLabsTtsService> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _llmRepository = Substitute.For<ILLMRepository>();
        _logger = Substitute.For<ILogger<ElevenLabsTtsService>>();
    }

    private static IHttpClientFactory FactoryReturning(CapturingHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        return factory;
    }

    private void GivenKey(string apiKey)
    {
        _llmRepository.GetProviderByIdAsync(ElevenLabsTtsConstants.LlmProviderId)
            .Returns(Task.FromResult<LLMProvider?>(new LLMProvider { ProviderId = ElevenLabsTtsConstants.LlmProviderId, ApiKey = apiKey }));
    }

    [Test]
    public async Task SynthesizeAsync_WhenKeyConfigured_PostsToVoiceUrlWithKeyHeader()
    {
        var audio = new byte[] { 1, 2, 3 };
        var handler = new CapturingHandler(HttpStatusCode.OK, audio);
        GivenKey("xi-test-key");
        var service = new ElevenLabsTtsService(FactoryReturning(handler), _llmRepository, _logger);

        var result = await service.SynthesizeAsync("Hallo", "pNInz6obpgDQGcFmaJgB", "de");

        result.ShouldBe(audio);
        handler.LastRequest!.RequestUri!.ToString().ShouldEndWith("pNInz6obpgDQGcFmaJgB");
        handler.LastRequest.Headers.GetValues(ElevenLabsTtsConstants.ApiKeyHeader).First().ShouldBe("xi-test-key");
    }

    [Test]
    public async Task SynthesizeAsync_WhenVoiceUnknown_FallsBackToDefaultVoiceInUrl()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, new byte[] { 9 });
        GivenKey("xi-test");
        var service = new ElevenLabsTtsService(FactoryReturning(handler), _llmRepository, _logger);

        await service.SynthesizeAsync("text", "de-DE-ConradNeural", "de");

        handler.LastRequest!.RequestUri!.ToString().ShouldEndWith(ElevenLabsTtsConstants.DefaultVoiceId);
    }

    [Test]
    public async Task SynthesizeAsync_WhenNoKey_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, System.Array.Empty<byte>());
        GivenKey(string.Empty);
        var service = new ElevenLabsTtsService(FactoryReturning(handler), _llmRepository, _logger);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.SynthesizeAsync("text", TtsProviderConstants.AutoVoice, "de"));
    }

    [Test]
    public async Task GetVoicesAsync_ReturnsCuratedVoices()
    {
        var service = new ElevenLabsTtsService(Substitute.For<IHttpClientFactory>(), _llmRepository, _logger);

        var voices = await service.GetVoicesAsync();

        voices.Count.ShouldBe(ElevenLabsTtsConstants.Voices.Count);
        voices.Select(v => v.VoiceId).ShouldContain(ElevenLabsTtsConstants.DefaultVoiceId);
    }
}
