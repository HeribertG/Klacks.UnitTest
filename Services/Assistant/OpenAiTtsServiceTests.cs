// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Services.Assistant;

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;

[TestFixture]
public class OpenAiTtsServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _payload;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public CapturingHandler(HttpStatusCode status, byte[] payload)
        {
            _status = status;
            _payload = payload;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status) { Content = new ByteArrayContent(_payload) };
        }
    }

    private ITtsApiKeyResolver _apiKeyResolver = null!;
    private ILogger<OpenAiTtsService> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _apiKeyResolver = Substitute.For<ITtsApiKeyResolver>();
        _logger = Substitute.For<ILogger<OpenAiTtsService>>();
    }

    private static IHttpClientFactory FactoryReturning(CapturingHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        return factory;
    }

    private void GivenKey(string apiKey)
    {
        _apiKeyResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey));
    }

    [Test]
    public async Task SynthesizeAsync_WhenKeyConfigured_PostsToOpenAiAndReturnsAudio()
    {
        var audio = new byte[] { 1, 2, 3, 4 };
        var handler = new CapturingHandler(HttpStatusCode.OK, audio);
        GivenKey("sk-test-key");
        var service = new OpenAiTtsService(FactoryReturning(handler), _apiKeyResolver, _logger);

        var result = await service.SynthesizeAsync("Hallo Welt", "nova", "de");

        result.ShouldBe(audio);
        handler.LastRequest!.RequestUri!.ToString().ShouldBe(OpenAiTtsConstants.ApiUrl);
        handler.LastRequest.Headers.Authorization!.ToString().ShouldBe("Bearer sk-test-key");
        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("voice").GetString().ShouldBe("nova");
        doc.RootElement.GetProperty("model").GetString().ShouldBe(OpenAiTtsConstants.Model);
    }

    [Test]
    public async Task SynthesizeAsync_WhenVoiceNotInOpenAiSet_FallsBackToDefault()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, new byte[] { 9 });
        GivenKey("sk-test");
        var service = new OpenAiTtsService(FactoryReturning(handler), _apiKeyResolver, _logger);

        await service.SynthesizeAsync("text", "de-DE-ConradNeural", "de");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("voice").GetString().ShouldBe(OpenAiTtsConstants.DefaultVoice);
    }

    [Test]
    public async Task SynthesizeAsync_WhenAutoVoice_UsesDefault()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, new byte[] { 9 });
        GivenKey("sk-test");
        var service = new OpenAiTtsService(FactoryReturning(handler), _apiKeyResolver, _logger);

        await service.SynthesizeAsync("text", TtsProviderConstants.AutoVoice, "de");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("voice").GetString().ShouldBe(OpenAiTtsConstants.DefaultVoice);
    }

    [Test]
    public async Task SynthesizeAsync_WhenNoKey_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, System.Array.Empty<byte>());
        GivenKey(string.Empty);
        var service = new OpenAiTtsService(FactoryReturning(handler), _apiKeyResolver, _logger);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.SynthesizeAsync("text", TtsProviderConstants.AutoVoice, "de"));
    }

    [Test]
    public async Task SynthesizeAsync_WhenApiReturnsError_Throws()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, System.Array.Empty<byte>());
        GivenKey("sk-test");
        var service = new OpenAiTtsService(FactoryReturning(handler), _apiKeyResolver, _logger);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.SynthesizeAsync("text", "nova", "de"));
    }

    [Test]
    public async Task GetVoicesAsync_ReturnsOpenAiVoiceSet()
    {
        var service = new OpenAiTtsService(Substitute.For<IHttpClientFactory>(), _apiKeyResolver, _logger);

        var voices = await service.GetVoicesAsync();

        voices.Count.ShouldBe(OpenAiTtsConstants.Voices.Count);
        voices.Select(v => v.VoiceId).ShouldContain(OpenAiTtsConstants.DefaultVoice);
    }
}
