// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeoServiceOutboundGuardHandler, the central outbound lock of the geo HTTP clients: a request to a
/// disabled service or to a host that is not an enabled endpoint of the client never reaches the network (503),
/// while the configured host of an enabled service passes unchanged. A redirect answer of an allowed host is not
/// followed but turned into a 503, and a request without a URI is blocked.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.GeoServices;
using Klacks.Api.Infrastructure.Http;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Http;

[TestFixture]
public class GeoServiceOutboundGuardHandlerTests
{
    private CountingHandler _inner = null!;

    [SetUp]
    public void Setup()
    {
        _inner = new CountingHandler();
    }

    [TearDown]
    public void TearDown()
    {
        _inner.Dispose();
    }

    [Test]
    public async Task DisabledService_NoRequestReachesTheNetwork_AndAnswers503()
    {
        using var client = CreateClient(GeoServicesPolicyTestFactory.NoOutbound(), GeoServiceKind.Geocoding);

        var response = await client.GetAsync(GeoServiceDefaults.NominatimBaseUrl + "/search?q=Bern");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.RequestMessage.ShouldNotBeNull();
        _inner.Requests.ShouldBe(0);
    }

    [Test]
    public async Task EnabledService_ConfiguredHostPasses()
    {
        using var client = CreateClient(GeoServicesPolicyTestFactory.Default(), GeoServiceKind.Geocoding);

        var response = await client.GetAsync(GeoServiceDefaults.NominatimBaseUrl + "/search?q=Bern");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _inner.Requests.ShouldBe(1);
    }

    [Test]
    public async Task EnabledService_ForeignHostIsBlocked()
    {
        using var client = CreateClient(GeoServicesPolicyTestFactory.Default(), GeoServiceKind.Geocoding);

        var response = await client.GetAsync("https://geocoder.elsewhere.example/search?q=Bern");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _inner.Requests.ShouldBe(0);
    }

    [Test]
    public async Task OwnInstance_PortAndSchemeMustMatch()
    {
        var policy = GeoServicesPolicyTestFactory.With(o =>
        {
            o.NoOutboundTraffic = true;
            o.Routing.BaseUrl = "http://osrm:5000";
        });
        using var client = CreateClient(policy, GeoServiceKind.Routing, GeoServiceKind.OpenRouteService);

        var allowed = await client.GetAsync("http://osrm:5000/table/v1/driving/1,2;3,4");
        var otherPort = await client.GetAsync("http://osrm:5001/table/v1/driving/1,2;3,4");
        var otherScheme = await client.GetAsync("https://osrm:5000/table/v1/driving/1,2;3,4");
        var publicOsrm = await client.GetAsync(GeoServiceDefaults.OsrmBaseUrl + "/table/v1/driving/1,2;3,4");

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        otherPort.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        otherScheme.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        publicOsrm.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _inner.Requests.ShouldBe(1);
    }

    [Test]
    public async Task RoutingClient_ServesOsrmAndOpenRouteService_EachOnlyWhileEnabled()
    {
        var policy = GeoServicesPolicyTestFactory.With(o => o.OpenRouteService.Enabled = false);
        using var client = CreateClient(policy, GeoServiceKind.Routing, GeoServiceKind.OpenRouteService);

        var osrm = await client.GetAsync(GeoServiceDefaults.OsrmBaseUrl + "/route/v1/driving/1,2;3,4");
        var ors = await client.GetAsync(GeoServiceDefaults.OpenRouteServiceBaseUrl + "/directions/driving-car");

        osrm.StatusCode.ShouldBe(HttpStatusCode.OK);
        ors.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _inner.Requests.ShouldBe(1);
    }

    [TestCase(HttpStatusCode.MovedPermanently)]
    [TestCase(HttpStatusCode.Found)]
    [TestCase(HttpStatusCode.TemporaryRedirect)]
    [TestCase(HttpStatusCode.PermanentRedirect)]
    public async Task AllowedHost_AnsweringWithARedirect_IsNotFollowed_AndBecomes503(HttpStatusCode redirect)
    {
        _inner.Respond = _ =>
        {
            var response = new HttpResponseMessage(redirect);
            response.Headers.Location = new Uri("https://collector.elsewhere.example/search?q=Bern");
            return response;
        };
        using var client = CreateClient(GeoServicesPolicyTestFactory.Default(), GeoServiceKind.Geocoding);

        var response = await client.GetAsync(GeoServiceDefaults.NominatimBaseUrl + "/search?q=Bern");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.Location.ShouldBeNull();
        _inner.Requests.ShouldBe(1);
        _inner.RequestedHosts.ShouldAllBe(host => host == new Uri(GeoServiceDefaults.NominatimBaseUrl).Host);
    }

    [Test]
    public async Task RequestWithoutUri_IsBlocked()
    {
        using var guard = new GeoServiceOutboundGuardHandler(
            GeoServicesPolicyTestFactory.Default(), [GeoServiceKind.Geocoding], NullLogger<GeoServiceOutboundGuardHandler>.Instance)
        {
            InnerHandler = _inner
        };
        using var invoker = new HttpMessageInvoker(guard, disposeHandler: false);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, (Uri?)null), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _inner.Requests.ShouldBe(0);
    }

    [Test]
    public async Task RelativeUri_IsBlocked()
    {
        using var guard = new GeoServiceOutboundGuardHandler(
            GeoServicesPolicyTestFactory.Default(), [GeoServiceKind.Geocoding], NullLogger<GeoServiceOutboundGuardHandler>.Instance)
        {
            InnerHandler = _inner
        };
        using var invoker = new HttpMessageInvoker(guard, disposeHandler: false);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri("/search?q=Bern", UriKind.Relative)), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _inner.Requests.ShouldBe(0);
    }

    private HttpClient CreateClient(IGeoServicesPolicy policy, params GeoServiceKind[] kinds)
    {
        var guard = new GeoServiceOutboundGuardHandler(policy, kinds, NullLogger<GeoServiceOutboundGuardHandler>.Instance)
        {
            InnerHandler = _inner
        };
        return new HttpClient(guard, disposeHandler: false);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public List<string> RequestedHosts { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            RequestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(Respond(request));
        }
    }
}
