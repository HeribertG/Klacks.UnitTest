// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the attributes of the refresh token cookie: HttpOnly, Path, Max-Age equal to the refresh token lifetime,
/// SameSite from Auth:RefreshCookie, and Secure exactly when the browser used HTTPS (the request itself, or a TLS-terminating
/// proxy saying so in X-Forwarded-Proto); over plain HTTP (native office PC, on-premise LAN without TLS) Secure is omitted.
/// Deleting uses the same attributes, because a browser keeps a cookie whose delete header names another Path or Secure.
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
public class RefreshTokenCookieServiceTests
{
    private const string Token = "raw-refresh-token";
    private const string HttpsScheme = "https";
    private const string HttpScheme = "http";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    [Test]
    public void Write_OverHttps_SetsHttpOnlySecureStrictCookieOnTheAccountsPath()
    {
        var context = CreateContext(HttpsScheme, "klacks.example.com");

        CreateService(HttpSameSite.Strict).Write(context, Token);

        var cookie = SingleSetCookie(context);
        cookie.Name.ToString().ShouldBe(RefreshTokenCookieConstants.CookieName);
        cookie.Value.ToString().ShouldBe(Token);
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.Path.ToString().ShouldBe(RefreshTokenCookieConstants.CookiePath);
        cookie.SameSite.ShouldBe(HeaderSameSite.Strict);
        cookie.MaxAge.ShouldBe(RefreshTokenLifetime.Duration);
        cookie.Domain.HasValue.ShouldBeFalse();
    }

    [Test]
    public void Write_TakesSameSiteFromTheOptions()
    {
        var context = CreateContext(HttpsScheme, "localhost", 5001);

        CreateService(HttpSameSite.None).Write(context, Token);

        SingleSetCookie(context).SameSite.ShouldBe(HeaderSameSite.None);
    }

    [TestCase("localhost")]
    [TestCase("127.0.0.1")]
    [TestCase("192.168.1.20")]
    [TestCase("klacks-server")]
    public void Write_OverPlainHttp_OmitsSecure_SoTheBrowserKeepsTheCookie(string host)
    {
        var context = CreateContext(HttpScheme, host, 5080);

        CreateService(HttpSameSite.Strict).Write(context, Token);

        var cookie = SingleSetCookie(context);
        cookie.Secure.ShouldBeFalse();
        cookie.HttpOnly.ShouldBeTrue();
    }

    [TestCase("https")]
    [TestCase("HTTPS")]
    [TestCase("https, http")]
    public void Write_BehindAProxyThatTerminatedTls_KeepsSecure_EvenWhenTheProxyIsNotTrusted(string forwardedProto)
    {
        var context = CreateContext(HttpScheme, "klacks.example.com");
        context.Request.Headers[ForwardedProtoHeader] = forwardedProto;

        CreateService(HttpSameSite.Strict).Write(context, Token);

        SingleSetCookie(context).Secure.ShouldBeTrue();
    }

    [Test]
    public void Write_BehindAPlainHttpProxy_OmitsSecure()
    {
        var context = CreateContext(HttpScheme, "klacks-server");
        context.Request.Headers[ForwardedProtoHeader] = HttpScheme;

        CreateService(HttpSameSite.Strict).Write(context, Token);

        SingleSetCookie(context).Secure.ShouldBeFalse();
    }

    [Test]
    public void Write_WithoutToken_SetsNoCookie()
    {
        var context = CreateContext(HttpsScheme, "klacks.example.com");

        CreateService(HttpSameSite.Strict).Write(context, string.Empty);

        context.Response.Headers.SetCookie.Count.ShouldBe(0);
    }

    [Test]
    public void Delete_ExpiresTheCookieWithTheSameAttributes()
    {
        var context = CreateContext(HttpsScheme, "klacks.example.com");

        CreateService(HttpSameSite.Strict).Delete(context);

        var cookie = SingleSetCookie(context);
        cookie.Name.ToString().ShouldBe(RefreshTokenCookieConstants.CookieName);
        cookie.Value.ToString().ShouldBeEmpty();
        cookie.Expires.ShouldNotBeNull();
        cookie.Expires!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow);
        cookie.Path.ToString().ShouldBe(RefreshTokenCookieConstants.CookiePath);
        cookie.Secure.ShouldBeTrue();
        cookie.HttpOnly.ShouldBeTrue();
        cookie.SameSite.ShouldBe(HeaderSameSite.Strict);
    }

    [Test]
    public void Read_ReturnsTheCookieValue_OrNullWhenAbsent()
    {
        var service = CreateService(HttpSameSite.Strict);
        var withCookie = CreateContext(HttpsScheme, "klacks.example.com");
        withCookie.Request.Headers.Cookie = $"{RefreshTokenCookieConstants.CookieName}={Token}";
        var withoutCookie = CreateContext(HttpsScheme, "klacks.example.com");

        service.Read(withCookie.Request).ShouldBe(Token);
        service.Read(withoutCookie.Request).ShouldBeNull();
    }

    private static RefreshTokenCookieService CreateService(HttpSameSite sameSite) =>
        new(Options.Create(new RefreshTokenCookieOptions { SameSite = sameSite }));

    private static DefaultHttpContext CreateContext(string scheme, string host, int? port = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = port.HasValue ? new HostString(host, port.Value) : new HostString(host);
        return context;
    }

    private static SetCookieHeaderValue SingleSetCookie(HttpContext context)
    {
        var headers = context.Response.Headers.SetCookie;
        headers.Count.ShouldBe(1);
        return SetCookieHeaderValue.Parse(headers[0]);
    }
}
