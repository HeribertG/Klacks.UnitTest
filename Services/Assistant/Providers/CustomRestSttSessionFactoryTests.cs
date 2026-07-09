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
public class CustomRestSttSessionFactoryTests
{
    private StubHttpMessageHandler _handler;
    private CustomRestSttSessionFactory _factory;

    [SetUp]
    public void SetUp()
    {
        _handler = new StubHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler));
        _factory = new CustomRestSttSessionFactory(httpClientFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    private static CustomSttProvider MakeProvider(string connectionType = SttProviderConstants.ConnectionTypeRest)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Self-hosted Whisper",
            ConnectionType = connectionType,
            ApiUrl = "http://whisper-stt:8000",
            IsEnabled = true,
        };

    [Test]
    public void CreateSession_ShouldReturnRestSession_ForRestConnectionType()
    {
        var session = _factory.CreateSession(MakeProvider(), "de");

        session.ShouldBeOfType<CustomRestSttSession>();
    }

    [Test]
    public void CreateSession_ShouldThrow_ForUnsupportedConnectionType()
    {
        Should.Throw<NotSupportedException>(() => _factory.CreateSession(MakeProvider("websocket"), "de"));
    }

    [Test]
    public async Task ValidateAsync_ShouldCallModelsEndpoint()
    {
        await _factory.ValidateAsync(MakeProvider());

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://whisper-stt:8000/v1/models");
    }

    [Test]
    public async Task ValidateAsync_ShouldThrow_WhenServerUnreachableStatus()
    {
        _handler.Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("unavailable"),
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _factory.ValidateAsync(MakeProvider()));
        ex.Message.ShouldContain("503");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public HttpResponseMessage Response = new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}"""),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }
}
