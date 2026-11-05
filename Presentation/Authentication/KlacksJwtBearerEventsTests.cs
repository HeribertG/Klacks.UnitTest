// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the token source order of the JwtBearer events: the klacks_access cookie supplies the token only when the request
/// has no Authorization header (whatever its scheme or validity) and no hub/STT query token was taken. A hub request
/// keeps preferring the query token and then the Authorization header. Only a token taken from the cookie is recorded,
/// and the request counts as cookie-authenticated only after that token validated.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Klacks.UnitTest.Presentation.Authentication;

[TestFixture]
public class KlacksJwtBearerEventsTests
{
    private const string CookieToken = "cookie.access.token";
    private const string HeaderToken = "header.access.token";
    private const string QueryToken = "query.access.token";
    private const string ApiPath = "/api/backend/Accounts/ValidateToken";
    private const string HubPath = "/hubs/work-notifications";
    private const string SttPath = "/api/backend/assistant/stt/stream";
    private const string BearerPrefix = "Bearer ";

    private KlacksJwtBearerEvents _events = null!;

    [SetUp]
    public void SetUp()
    {
        _events = new KlacksJwtBearerEvents(
            new AccessTokenCookieService(Options.Create(new RefreshTokenCookieOptions())),
            Substitute.For<ILogger<KlacksJwtBearerEvents>>());
    }

    [Test]
    public async Task ApiRequest_WithOnlyTheCookie_TakesTheCookieToken_AndRecordsTheSource()
    {
        var context = CreateContext(ApiPath, cookie: true);

        var received = await ReceiveAsync(context);

        received.Token.ShouldBe(CookieToken);
        await ValidateAsync(context);
        AccessTokenCookieAuthentication.IsCookieAuthenticated(context).ShouldBeTrue();
    }

    [Test]
    public async Task ApiRequest_WithTheCookie_IsNotCookieAuthenticated_UntilTheTokenValidated()
    {
        var context = CreateContext(ApiPath, cookie: true);

        await ReceiveAsync(context);

        AccessTokenCookieAuthentication.IsCookieAuthenticated(context).ShouldBeFalse();
    }

    [TestCase(BearerPrefix + HeaderToken)]
    [TestCase(BearerPrefix + "not-a-jwt")]
    [TestCase("Basic dXNlcjpwYXNz")]
    public async Task ApiRequest_WithAnyAuthorizationHeader_IgnoresTheCookie(string authorization)
    {
        var context = CreateContext(ApiPath, cookie: true, authorization: authorization);

        var received = await ReceiveAsync(context);
        await ValidateAsync(context);

        received.Token.ShouldBeNull();
        AccessTokenCookieAuthentication.IsCookieAuthenticated(context).ShouldBeFalse();
    }

    [Test]
    public async Task ApiRequest_WithoutCookieAndHeader_LeavesTheTokenEmpty()
    {
        var context = CreateContext(ApiPath, cookie: false);

        var received = await ReceiveAsync(context);
        await ValidateAsync(context);

        received.Token.ShouldBeNull();
        AccessTokenCookieAuthentication.IsCookieAuthenticated(context).ShouldBeFalse();
    }

    [Test]
    public async Task HubRequest_WithQueryToken_PrefersTheQueryOverTheCookie()
    {
        var context = CreateContext(HubPath, cookie: true, query: QueryToken);

        var received = await ReceiveAsync(context);
        await ValidateAsync(context);

        received.Token.ShouldBe(QueryToken);
        AccessTokenCookieAuthentication.IsCookieAuthenticated(context).ShouldBeFalse();
    }

    [Test]
    public async Task HubRequest_WithBearerHeader_PrefersTheHeaderOverTheCookie()
    {
        var context = CreateContext(HubPath, cookie: true, authorization: BearerPrefix + HeaderToken);

        var received = await ReceiveAsync(context);

        received.Token.ShouldBe(HeaderToken);
    }

    [Test]
    public async Task HubRequest_WithOnlyTheCookie_TakesTheCookieToken()
    {
        var context = CreateContext(HubPath, cookie: true);

        var received = await ReceiveAsync(context);
        await ValidateAsync(context);

        received.Token.ShouldBe(CookieToken);
        AccessTokenCookieAuthentication.IsCookieAuthenticated(context).ShouldBeTrue();
    }

    [Test]
    public async Task SttRequest_WithQueryToken_PrefersTheQueryOverTheCookie()
    {
        var context = CreateContext(SttPath, cookie: true, query: QueryToken);

        var received = await ReceiveAsync(context);

        received.Token.ShouldBe(QueryToken);
    }

    [Test]
    public async Task ApiRequest_WithAQueryToken_StillUsesTheCookie_BecauseOnlyHubsAndSttReadTheQuery()
    {
        var context = CreateContext(ApiPath, cookie: true, query: QueryToken);

        var received = await ReceiveAsync(context);

        received.Token.ShouldBe(CookieToken);
    }

    private async Task<MessageReceivedContext> ReceiveAsync(HttpContext context)
    {
        var received = new MessageReceivedContext(context, Scheme(), new JwtBearerOptions());
        await _events.MessageReceived(received);
        return received;
    }

    private async Task ValidateAsync(HttpContext context)
    {
        await _events.TokenValidated(new TokenValidatedContext(context, Scheme(), new JwtBearerOptions()));
    }

    private static AuthenticationScheme Scheme() =>
        new(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));

    private static DefaultHttpContext CreateContext(string path, bool cookie, string? authorization = null, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (cookie)
        {
            context.Request.Headers.Cookie = $"{AccessTokenCookieConstants.CookieName}={CookieToken}";
        }

        if (authorization != null)
        {
            context.Request.Headers[HeaderNames.Authorization] = authorization;
        }

        if (query != null)
        {
            context.Request.QueryString = QueryString.Create("access_token", query);
        }

        return context;
    }
}
