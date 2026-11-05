// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the global guard of cookie-authenticated requests against the real CORS policy provider: a cookie-authenticated
/// request with a foreign Origin is 403 for every method (also GET, the WebSocket upgrade of a hub); an unsafe method
/// without X-Requested-With and without JSON is 403; safe methods without proof, unsafe methods with either proof,
/// whitelisted and same-host origins pass. A request that was not authenticated by the cookie passes untouched, also
/// with a foreign Origin and a form content type.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Klacks.UnitTest.Presentation.Authentication;

[TestFixture]
public class AccessCookieRequestGuardMiddlewareTests
{
    private const string WhitelistedOrigin = "http://localhost:4200";
    private const string ServerHost = "klacks.example.com";
    private const string ForeignOrigin = "https://evil.example.org";
    private const string FormContentType = "application/x-www-form-urlencoded";
    private const string MultipartContentType = "multipart/form-data; boundary=x";
    private const string JsonContentType = "application/json";

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

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task CookieAuthenticated_UnsafeMethodWithoutProof_Is403(string method)
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, method, contentType: FormContentType);

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task CookieAuthenticated_MultipartWithoutRequestedWith_Is403()
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, HttpMethods.Post, contentType: MultipartContentType);

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task CookieAuthenticated_UnsafeMethodWithRequestedWith_Passes()
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, HttpMethods.Delete, requestedWith: true);

        AssertPassed(context, nextCalled);
    }

    [Test]
    public async Task CookieAuthenticated_UnsafeMethodWithJson_Passes()
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, HttpMethods.Post, contentType: JsonContentType);

        AssertPassed(context, nextCalled);
    }

    [TestCase("GET")]
    [TestCase("HEAD")]
    public async Task CookieAuthenticated_SafeMethodWithoutProof_Passes(string method)
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, method);

        AssertPassed(context, nextCalled);
    }

    [TestCase("GET")]
    [TestCase("POST")]
    public async Task CookieAuthenticated_WithForeignOrigin_Is403_EvenWithProof(string method)
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, method, requestedWith: true, origin: ForeignOrigin);

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task CookieAuthenticated_WithWhitelistedOrigin_Passes()
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, HttpMethods.Get, origin: WhitelistedOrigin);

        AssertPassed(context, nextCalled);
    }

    [Test]
    public async Task CookieAuthenticated_WithOriginOfTheRequestedHost_Passes()
    {
        var (context, nextCalled) = await RunAsync(cookieAuthenticated: true, HttpMethods.Get, origin: "https://" + ServerHost + ":8443");

        AssertPassed(context, nextCalled);
    }

    [Test]
    public async Task NotCookieAuthenticated_ForeignOriginAndFormPost_PassUntouched()
    {
        var (context, nextCalled) = await RunAsync(
            cookieAuthenticated: false, HttpMethods.Post, contentType: MultipartContentType, origin: ForeignOrigin);

        AssertPassed(context, nextCalled);
    }

    [Test]
    public async Task TokenTakenFromTheCookieButNotValidated_IsNotGuarded()
    {
        var context = CreateContext(HttpMethods.Post, FormContentType, requestedWith: false, origin: ForeignOrigin);
        AccessTokenCookieAuthentication.MarkTokenFromCookie(context);

        var nextCalled = await InvokeAsync(context);

        AssertPassed(context, nextCalled);
    }

    private static void AssertForbidden(HttpContext context, bool nextCalled)
    {
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        nextCalled.ShouldBeFalse();
    }

    private static void AssertPassed(HttpContext context, bool nextCalled)
    {
        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    private async Task<(HttpContext Context, bool NextCalled)> RunAsync(
        bool cookieAuthenticated,
        string method,
        string? contentType = null,
        bool requestedWith = false,
        string? origin = null)
    {
        var context = CreateContext(method, contentType, requestedWith, origin);
        if (cookieAuthenticated)
        {
            AccessTokenCookieAuthentication.MarkTokenFromCookie(context);
            AccessTokenCookieAuthentication.ConfirmIfTokenFromCookie(context);
        }

        return (context, await InvokeAsync(context));
    }

    private async Task<bool> InvokeAsync(HttpContext context)
    {
        var nextCalled = false;
        var middleware = new AccessCookieRequestGuardMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Substitute.For<ILogger<AccessCookieRequestGuardMiddleware>>());

        await middleware.InvokeAsync(context, _rules);
        return nextCalled;
    }

    private DefaultHttpContext CreateContext(string method, string? contentType, bool requestedWith, string? origin)
    {
        var context = new DefaultHttpContext { RequestServices = _provider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(ServerHost);
        context.Request.Method = method;
        context.Request.ContentType = contentType;
        if (requestedWith)
        {
            context.Request.Headers[CsrfProofConstants.RequestedWithHeader] = CsrfProofConstants.RequestedWithValue;
        }

        if (origin != null)
        {
            context.Request.Headers[HeaderNames.Origin] = origin;
        }

        return context;
    }
}
