// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the attributes of the access token cookie: name klacks_access, HttpOnly, Path=/, Max-Age equal to the access token
/// lifetime, SameSite from Auth:RefreshCookie (shared with the refresh cookie) and Secure exactly when the browser used
/// HTTPS. Deleting uses the same attributes; an empty token writes nothing.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using HeaderSameSite = Microsoft.Net.Http.Headers.SameSiteMode;
using HttpSameSite = Microsoft.AspNetCore.Http.SameSiteMode;

namespace Klacks.UnitTest.Presentation.Authentication;

[TestFixture]
public class AccessTokenCookieServiceTests
{
    private const string Token = "header.payload.signature";
    private const string HttpsScheme = "https";
    private const string HttpScheme = "http";
    private const string ServerHost = "klacks.example.com";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    [Test]
    public void Write_OverHttps_SetsHttpOnlySecureCookieOnTheRootPathWithTheAccessTokenLifetime()
    {
        var context = CreateContext(HttpsScheme);

        CreateService(HttpSameSite.Strict).Write(context, Token);

        var cookie = SingleSetCookie(context);
        cookie.Name.ToString().ShouldBe(AccessTokenCookieConstants.CookieName);
        cookie.Value.ToString().ShouldBe(Token);
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.Path.ToString().ShouldBe(AccessTokenCookieConstants.CookiePath);
        cookie.SameSite.ShouldBe(HeaderSameSite.Strict);
        cookie.MaxAge.ShouldBe(AccessTokenLifetime.Duration);
        cookie.Domain.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Write_TakesSameSiteFromTheSharedOptions()
    {
        var context = CreateContext(HttpsScheme);

        CreateService(HttpSameSite.None).Write(context, Token);

        SingleSetCookie(context).SameSite.ShouldBe(HeaderSameSite.None);
    }

    [Test]
    public void Write_OverPlainHttp_OmitsSecure()
    {
        var context = CreateContext(HttpScheme);

        CreateService(HttpSameSite.Strict).Write(context, Token);

        SingleSetCookie(context).Secure.ShouldBeFalse();
    }

    [Test]
    public void Write_BehindAProxyThatTerminatedTls_KeepsSecure()
    {
        var context = CreateContext(HttpScheme);
        context.Request.Headers[ForwardedProtoHeader] = HttpsScheme;

        CreateService(HttpSameSite.Strict).Write(context, Token);

        SingleSetCookie(context).Secure.ShouldBeTrue();
    }

    [Test]
    public void Write_WithoutToken_SetsNoCookie()
    {
        var context = CreateContext(HttpsScheme);

        CreateService(HttpSameSite.Strict).Write(context, null);

        context.Response.Headers.SetCookie.Count.ShouldBe(0);
    }

    [Test]
    public void Delete_ExpiresTheCookieWithTheSameAttributes()
    {
        var context = CreateContext(HttpsScheme);

        CreateService(HttpSameSite.Strict).Delete(context);

        var cookie = SingleSetCookie(context);
        cookie.Name.ToString().ShouldBe(AccessTokenCookieConstants.CookieName);
        cookie.Value.ToString().ShouldBeEmpty();
        cookie.Expires!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow);
        cookie.Path.ToString().ShouldBe(AccessTokenCookieConstants.CookiePath);
        cookie.Secure.ShouldBeTrue();
        cookie.HttpOnly.ShouldBeTrue();
        cookie.SameSite.ShouldBe(HeaderSameSite.Strict);
    }

    [Test]
    public void Read_ReturnsTheCookieValue_OrNullWhenAbsent()
    {
        var service = CreateService(HttpSameSite.Strict);
        var withCookie = CreateContext(HttpsScheme);
        withCookie.Request.Headers.Cookie = $"{AccessTokenCookieConstants.CookieName}={Token}";

        service.Read(withCookie.Request).ShouldBe(Token);
        service.Read(CreateContext(HttpsScheme).Request).ShouldBeNull();
    }

    private static AccessTokenCookieService CreateService(HttpSameSite sameSite) =>
        new(Options.Create(new RefreshTokenCookieOptions { SameSite = sameSite }));

    private static DefaultHttpContext CreateContext(string scheme)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(ServerHost);
        return context;
    }

    private static SetCookieHeaderValue SingleSetCookie(HttpContext context)
    {
        var headers = context.Response.Headers.SetCookie;
        headers.Count.ShouldBe(1);
        return SetCookieHeaderValue.Parse(headers[0]);
    }
}
