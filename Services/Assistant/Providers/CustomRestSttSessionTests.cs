// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Services.Assistant.Providers;

using System.Net;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Stt;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class CustomRestSttSessionTests
{
    private StubHttpMessageHandler _handler;
    private IHttpClientFactory _httpClientFactory;

    [SetUp]
    public void SetUp()
    {
        _handler = new StubHttpMessageHandler();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler));
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    private static CustomSttProvider MakeProvider(string? apiKey = null, string? model = null, string apiUrl = "http://whisper-stt:8000")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Self-hosted Whisper",
            ConnectionType = SttProviderConstants.ConnectionTypeRest,
            ApiUrl = apiUrl,
            ApiKey = apiKey,
            LanguageModel = model,
            IsEnabled = true,
        };

    [Test]
    public async Task ReceiveAsync_ShouldReturnNull_WhenNoAudioBuffered()
    {
        await using var session = new CustomRestSttSession(_httpClientFactory, MakeProvider(), "de");

        var result = await session.ReceiveAsync();

        result.ShouldBeNull();
        _handler.LastRequest.ShouldBeNull();
    }

    [Test]
    public async Task ReceiveAsync_ShouldPostMultipartToTranscriptionsEndpoint()
    {
        await using var session = new CustomRestSttSession(_httpClientFactory, MakeProvider(model: "large-v3-turbo"), "de");
        await session.SendAudioAsync([1, 2, 3]);

        var result = await session.ReceiveAsync();

        result.ShouldNotBeNull();
        result!.Text.ShouldBe("hallo welt");
        result.IsFinal.ShouldBeTrue();
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://whisper-stt:8000/v1/audio/transcriptions");
        _handler.LastRequestBody.ShouldNotBeNull();
        _handler.LastRequestBody!.ShouldContain("name=model");
        _handler.LastRequestBody.ShouldContain("large-v3-turbo");
        _handler.LastRequestBody.ShouldContain("name=language");
    }

    [Test]
    public async Task ReceiveAsync_ShouldMapLocaleToWhisperLanguage()
    {
        await using var session = new CustomRestSttSession(_httpClientFactory, MakeProvider(), "nb");
        await session.SendAudioAsync([1, 2, 3]);

        await session.ReceiveAsync();

        _handler.LastRequestBody!.ShouldContain("\r\n\r\nno\r\n");
    }

    [Test]
    public async Task ReceiveAsync_ShouldFallBackToDefaultModel_WhenNoModelConfigured()
    {
        await using var session = new CustomRestSttSession(_httpClientFactory, MakeProvider(), "de");
        await session.SendAudioAsync([1, 2, 3]);

        await session.ReceiveAsync();

        _handler.LastRequestBody!.ShouldContain(SttProviderConstants.DefaultCustomWhisperModel);
    }

    [Test]
    public async Task ReceiveAsync_ShouldSendBearerHeader_OnlyWhenApiKeyConfigured()
    {
        await using var withKey = new CustomRestSttSession(_httpClientFactory, MakeProvider(apiKey: "secret"), "de");
        await withKey.SendAudioAsync([1, 2, 3]);
        await withKey.ReceiveAsync();
        _handler.LastRequest!.Headers.Authorization.ShouldNotBeNull();
        _handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("secret");

        await using var withoutKey = new CustomRestSttSession(_httpClientFactory, MakeProvider(), "de");
        await withoutKey.SendAudioAsync([1, 2, 3]);
        await withoutKey.ReceiveAsync();
        _handler.LastRequest!.Headers.Authorization.ShouldBeNull();
    }

    [Test]
    public async Task ReceiveAsync_ShouldThrow_OnErrorStatus()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"detail":"model not found"}"""),
        };
        await using var session = new CustomRestSttSession(_httpClientFactory, MakeProvider(), "de");
        await session.SendAudioAsync([1, 2, 3]);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.ReceiveAsync());
        ex.Message.ShouldContain("422");
    }

    [TestCase("http://whisper-stt:8000", "http://whisper-stt:8000/v1/audio/transcriptions")]
    [TestCase("http://whisper-stt:8000/", "http://whisper-stt:8000/v1/audio/transcriptions")]
    [TestCase("http://whisper-stt:8000/v1/audio/transcriptions", "http://whisper-stt:8000/v1/audio/transcriptions")]
    public void BuildTranscriptionsUrl_ShouldAppendPathOnlyWhenMissing(string apiUrl, string expected)
    {
        CustomRestSttSession.BuildTranscriptionsUrl(apiUrl).ShouldBe(expected);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public HttpResponseMessage Response = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text":"hallo welt"}"""),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            return Response;
        }
    }
}
