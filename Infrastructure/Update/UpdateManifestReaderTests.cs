// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Update;

using System.Net;
using System.Text;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Services.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class UpdateManifestReaderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static UpdateManifestReader CreateReader(string baseUrl, HttpStatusCode status, string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(UpdateManifestReader.HttpClientName).Returns(new HttpClient(new StubHandler(status, body)));
        var options = Options.Create(new UpdateTrustOptions { ManifestBaseUrl = baseUrl });
        return new UpdateManifestReader(factory, options, NullLogger<UpdateManifestReader>.Instance);
    }

    [Test]
    public async Task Parses_valid_manifest_with_string_enum_channel()
    {
        const string json = """
        {
            "channel": "Stable",
            "latestVersion": "1.4.2",
            "minUpgradableFrom": "1.0.0",
            "containsMigrations": true,
            "artifacts": { "docker": { "apiImage": "ghcr.io/x/api:1.4.2", "uiImage": "ghcr.io/x/ui:1.4.2" } }
        }
        """;

        var reader = CreateReader("https://updates.example.com", HttpStatusCode.OK, json);
        var manifest = await reader.GetManifestAsync(UpdateChannel.Stable);

        manifest.ShouldNotBeNull();
        manifest!.LatestVersion.ShouldBe("1.4.2");
        manifest.Channel.ShouldBe(UpdateChannel.Stable);
        manifest.ContainsMigrations.ShouldBeTrue();
        manifest.Artifacts.Docker!.ApiImage.ShouldBe("ghcr.io/x/api:1.4.2");
    }

    [Test]
    public async Task Returns_null_when_base_url_not_configured()
    {
        var reader = CreateReader(string.Empty, HttpStatusCode.OK, "{}");
        (await reader.GetManifestAsync(UpdateChannel.Stable)).ShouldBeNull();
    }

    [Test]
    public async Task Returns_null_on_non_success_status()
    {
        var reader = CreateReader("https://updates.example.com", HttpStatusCode.NotFound, "");
        (await reader.GetManifestAsync(UpdateChannel.Stable)).ShouldBeNull();
    }

    [Test]
    public async Task Returns_null_on_invalid_json()
    {
        var reader = CreateReader("https://updates.example.com", HttpStatusCode.OK, "not-json");
        (await reader.GetManifestAsync(UpdateChannel.Stable)).ShouldBeNull();
    }
}
