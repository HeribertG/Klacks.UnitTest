// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the marketplace region-package client: latest-version lookup maps the wire payload
/// (countryCode/version/minKlacksVersion), a 404 yields a not-found result while invalid JSON, server
/// errors and network exceptions yield a failed result (or null for downloads) instead of throwing,
/// the download call requests the full profile artifact with industry=all and captures the optional
/// X-Klacks-Signature response header together with the exact response bytes.
/// </summary>

using System.Net;
using System.Text;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionPackageMarketplaceClientTests
{
    private const string BaseUrl = "https://marketplace.test";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string? _signatureHeader;

        public Uri? LastRequestUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body, string? signatureHeader = null)
        {
            _status = status;
            _body = body;
            _signatureHeader = signatureHeader;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };

            if (_signatureHeader != null)
            {
                response.Headers.Add(RegionPackageMarketplaceClient.SignatureHeaderName, _signatureHeader);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused");
        }
    }

    private static RegionPackageMarketplaceClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegionPackageMarketplaceClient.MarketplaceUrlConfigKey] = BaseUrl
            })
            .Build();

        return new RegionPackageMarketplaceClient(
            new HttpClient(handler),
            configuration,
            NullLogger<RegionPackageMarketplaceClient>.Instance);
    }

    [Test]
    public async Task GetLatestAsync_FullWirePayload_MapsCountryVersionAndMinKlacksVersion()
    {
        const string json = """
            {
              "countryCode": "ch",
              "countryName": "Switzerland",
              "version": "1.2.0",
              "description": "Swiss region package",
              "minKlacksVersion": "1.0.5",
              "authorName": "Klacks",
              "downloads": 42,
              "updatedAt": "2026-07-01T00:00:00Z"
            }
            """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var client = CreateClient(handler);

        var lookup = await client.GetLatestAsync("ch", CancellationToken.None);

        lookup.NotFound.ShouldBeFalse();
        lookup.Package.ShouldNotBeNull();
        lookup.Package!.Country.ShouldBe("ch");
        lookup.Package.Version.ShouldBe("1.2.0");
        lookup.Package.MinKlacksVersion.ShouldBe("1.0.5");
        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.ToString().ShouldBe($"{BaseUrl}/api/regions/ch");
    }

    [Test]
    public async Task GetLatestAsync_NotFound_ReturnsNotFoundResult()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.NotFound, string.Empty));

        var lookup = await client.GetLatestAsync("xx", CancellationToken.None);

        lookup.NotFound.ShouldBeTrue();
        lookup.Package.ShouldBeNull();
    }

    [Test]
    public async Task GetLatestAsync_ServerError_ReturnsFailedResult()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.InternalServerError, string.Empty));

        var lookup = await client.GetLatestAsync("ch", CancellationToken.None);

        lookup.NotFound.ShouldBeFalse();
        lookup.Package.ShouldBeNull();
    }

    [Test]
    public async Task GetLatestAsync_InvalidJson_ReturnsFailedResult()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, "{ this is not json"));

        var lookup = await client.GetLatestAsync("ch", CancellationToken.None);

        lookup.NotFound.ShouldBeFalse();
        lookup.Package.ShouldBeNull();
    }

    [Test]
    public async Task GetLatestAsync_PayloadWithoutVersion_ReturnsFailedResult()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, """{ "countryCode": "ch" }"""));

        var lookup = await client.GetLatestAsync("ch", CancellationToken.None);

        lookup.NotFound.ShouldBeFalse();
        lookup.Package.ShouldBeNull();
    }

    [Test]
    public async Task GetLatestAsync_HandlerThrowsHttpRequestException_ReturnsFailedResultInsteadOfThrowing()
    {
        var client = CreateClient(new ThrowingHandler());

        var lookup = await client.GetLatestAsync("ch", CancellationToken.None);

        lookup.NotFound.ShouldBeFalse();
        lookup.Package.ShouldBeNull();
    }

    [Test]
    public async Task DownloadProfileAsync_Success_ReturnsBodyAndRequestsFullProfileArtifact()
    {
        const string profileJson = """{ "version": 1 }""";
        var handler = new StubHandler(HttpStatusCode.OK, profileJson);
        var client = CreateClient(handler);

        var result = await client.DownloadProfileAsync("ch", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.ProfileJson.ShouldBe(profileJson);
        result.Content.ShouldBe(Encoding.UTF8.GetBytes(profileJson));
        result.Signature.ShouldBeNull();
        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.ToString().ShouldBe($"{BaseUrl}/api/regions/ch/download?industry=all&artifact=profileJson");
    }

    [Test]
    public async Task DownloadProfileAsync_SignatureHeaderPresent_ReturnsSignature()
    {
        const string profileJson = """{ "version": 1 }""";
        const string signature = "c2lnbmVkLXBheWxvYWQ=";
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, profileJson, signature));

        var result = await client.DownloadProfileAsync("ch", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Signature.ShouldBe(signature);
        result.ProfileJson.ShouldBe(profileJson);
    }

    [Test]
    public async Task DownloadProfileAsync_ServerError_ReturnsNull()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.InternalServerError, string.Empty));

        var result = await client.DownloadProfileAsync("ch", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task DownloadProfileAsync_HandlerThrowsHttpRequestException_ReturnsNullInsteadOfThrowing()
    {
        var client = CreateClient(new ThrowingHandler());

        var result = await client.DownloadProfileAsync("ch", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task DownloadProfileAsync_ResponseLargerThanLimit_ReturnsNull()
    {
        const int oversizeBytes = 11 * 1024 * 1024;
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, new string('x', oversizeBytes)));

        var result = await client.DownloadProfileAsync("ch", CancellationToken.None);

        result.ShouldBeNull();
    }
}
