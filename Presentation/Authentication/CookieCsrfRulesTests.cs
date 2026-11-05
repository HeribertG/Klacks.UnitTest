// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the origin rules of cookie-authenticated requests against the real CORS policy provider.
/// WebSocket upgrades (hubs, STT stream): Sec-Fetch-Site same-origin passes; otherwise only an Origin of the CORS policy
/// passes, so a page on another port of the same host (same-site) and a foreign site (cross-site) are rejected even though
/// the hostname-only rule would accept the first; without Sec-Fetch-Site (old browsers) the ordinary rule applies.
/// Ordinary requests keep the ordinary rule: no Origin, same hostname (any port) or a CORS origin.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Klacks.UnitTest.Presentation.Authentication;

[TestFixture]
public class CookieCsrfRulesTests
{
    private const string WhitelistedOrigin = "http://localhost:4200";
    private const string ServerHost = "klacks.example.com";
    private const string SameOrigin = "https://klacks.example.com";
    private const string SameHostOtherPort = "https://klacks.example.com:9000";
    private const string ForeignOrigin = "https://evil.example.org";
    private const string SameSite = "same-site";
    private const string CrossSite = "cross-site";

    private ServiceProvider _provider = null!;
    private CookieCsrfRules _rules = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors(options => options.AddPolicy(
            CorsOriginsOptions.PolicyName,
            policy => policy.WithOrigins(WhitelistedOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        _provider = services.BuildServiceProvider();
        _rules = new CookieCsrfRules(_provider.GetRequiredService<ICorsPolicyProvider>());
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    [Test]
    public async Task WebSocket_SameOriginFetchSite_IsAccepted()
    {
        (await AcceptsAsync(webSocket: true, CsrfProofConstants.FetchSiteSameOrigin, SameOrigin)).ShouldBeTrue();
    }

    [Test]
    public async Task WebSocket_SameSiteFromAnotherPortOfTheHost_WithoutCorsMatch_IsRejected()
    {
        (await AcceptsAsync(webSocket: true, SameSite, SameHostOtherPort)).ShouldBeFalse();
    }

    [Test]
    public async Task WebSocket_CrossSite_IsRejected()
    {
        (await AcceptsAsync(webSocket: true, CrossSite, ForeignOrigin)).ShouldBeFalse();
    }

    [Test]
    public async Task WebSocket_CrossSiteFromACorsOrigin_IsAccepted()
    {
        (await AcceptsAsync(webSocket: true, CrossSite, WhitelistedOrigin)).ShouldBeTrue();
    }

    [Test]
    public async Task WebSocket_CrossSiteWithoutOrigin_IsRejected()
    {
        (await AcceptsAsync(webSocket: true, CrossSite, origin: null)).ShouldBeFalse();
    }

    [Test]
    public async Task WebSocket_WithoutFetchSite_FallsBackToTheHostnameRule()
    {
        (await AcceptsAsync(webSocket: true, fetchSite: null, SameHostOtherPort)).ShouldBeTrue();
        (await AcceptsAsync(webSocket: true, fetchSite: null, ForeignOrigin)).ShouldBeFalse();
    }

    [Test]
    public async Task OrdinaryRequest_SameSiteFromAnotherPortOfTheHost_KeepsTheHostnameRule()
    {
        (await AcceptsAsync(webSocket: false, SameSite, SameHostOtherPort)).ShouldBeTrue();
    }

    [Test]
    public async Task OrdinaryRequest_ForeignOrigin_IsRejected_AndWithoutOriginAccepted()
    {
        (await AcceptsAsync(webSocket: false, CrossSite, ForeignOrigin)).ShouldBeFalse();
        (await AcceptsAsync(webSocket: false, fetchSite: null, origin: null)).ShouldBeTrue();
    }

    private async Task<bool> AcceptsAsync(bool webSocket, string? fetchSite, string? origin)
    {
        var context = new DefaultHttpContext { RequestServices = _provider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(ServerHost);
        context.Request.Method = HttpMethods.Get;
        if (webSocket)
        {
            var feature = Substitute.For<IHttpWebSocketFeature>();
            feature.IsWebSocketRequest.Returns(true);
            context.Features.Set(feature);
        }

        if (fetchSite != null)
        {
            context.Request.Headers[CsrfProofConstants.FetchSiteHeader] = fetchSite;
        }

        if (origin != null)
        {
            context.Request.Headers[HeaderNames.Origin] = origin;
        }

        context.WebSockets.IsWebSocketRequest.ShouldBe(webSocket);
        return await _rules.IsOriginAcceptedAsync(context);
    }
}
