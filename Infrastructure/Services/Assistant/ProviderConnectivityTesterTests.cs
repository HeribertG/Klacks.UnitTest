// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProviderConnectivityTester: HTTP status classification, base-URL handling and the
/// upfront scheme allow-list. The private/loopback address block itself (enforced via
/// PrivateNetworkBlockingConnectCallback at actual connect time) is covered by
/// PrivateNetworkBlockingConnectCallbackTests, since it lives below this fake HttpMessageHandler.
/// </summary>

using System.Net;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Infrastructure.Security;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class ProviderConnectivityTesterTests
{
    private CapturingHandler _handler = null!;
    private IHttpClientFactory _httpClientFactory = null!;
    private ProviderConnectivityTester _tester = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new CapturingHandler();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(_handler, disposeHandler: false));
        _tester = new ProviderConnectivityTester(
            _httpClientFactory,
            Substitute.For<ILogger<ProviderConnectivityTester>>());
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    [TestCase(HttpStatusCode.OK, ProviderConnectivityStatus.Reachable)]
    [TestCase(HttpStatusCode.NoContent, ProviderConnectivityStatus.Reachable)]
    [TestCase(HttpStatusCode.Unauthorized, ProviderConnectivityStatus.ReachableNeedsKey)]
    [TestCase(HttpStatusCode.Forbidden, ProviderConnectivityStatus.ReachableNeedsKey)]
    [TestCase(HttpStatusCode.NotFound, ProviderConnectivityStatus.Unreachable)]
    [TestCase(HttpStatusCode.InternalServerError, ProviderConnectivityStatus.Unreachable)]
    public async Task TestAsync_ClassifiesStatusCode(HttpStatusCode status, ProviderConnectivityStatus expected)
    {
        _handler.ResponseStatus = status;

        var result = await _tester.TestAsync("https://api.example.com/v1/");

        result.ShouldBe(expected);
    }

    [Test]
    public async Task TestAsync_NetworkException_ReturnsUnreachable()
    {
        _handler.ThrowOnSend = true;

        var result = await _tester.TestAsync("https://api.example.com/v1/");

        result.ShouldBe(ProviderConnectivityStatus.Unreachable);
    }

    [Test]
    public async Task TestAsync_BaseUrlWithoutTrailingSlash_ResolvesModelsRelativeToVersionRoot()
    {
        _handler.ResponseStatus = HttpStatusCode.OK;

        await _tester.TestAsync("https://api.example.com/v1");

        _handler.LastRequestUri.ShouldBe("https://api.example.com/v1/models");
    }

    [Test]
    public async Task TestAsync_WithApiKey_SendsBearerHeader()
    {
        _handler.ResponseStatus = HttpStatusCode.OK;

        await _tester.TestAsync("https://api.example.com/v1/", "secret-key");

        _handler.LastAuthorization.ShouldBe("Bearer secret-key");
    }

    [TestCase("")]
    [TestCase("not-a-url")]
    public async Task TestAsync_InvalidBaseUrl_ReturnsUnreachable(string baseUrl)
    {
        var result = await _tester.TestAsync(baseUrl);

        result.ShouldBe(ProviderConnectivityStatus.Unreachable);
    }

    [TestCase("ftp://api.example.com/v1/")]
    [TestCase("file:///etc/passwd")]
    public async Task TestAsync_DisallowedScheme_ReturnsUnreachable(string baseUrl)
    {
        var result = await _tester.TestAsync(baseUrl);

        result.ShouldBe(ProviderConnectivityStatus.Unreachable);
        _handler.LastRequestUri.ShouldBeNull();
    }

    [Test]
    public async Task TestAsync_PrivateNetworkGuardBlocksConnection_ReturnsUnreachable()
    {
        _handler.ExceptionToThrow = new HttpRequestException(
            "guard tripped",
            new PrivateNetworkAccessBlockedException("blocked for test"));

        var result = await _tester.TestAsync("http://looks-public.example.com/v1/");

        result.ShouldBe(ProviderConnectivityStatus.Unreachable);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public bool ThrowOnSend { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            if (ThrowOnSend)
            {
                throw new HttpRequestException("network down");
            }

            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(ResponseStatus));
        }
    }
}
