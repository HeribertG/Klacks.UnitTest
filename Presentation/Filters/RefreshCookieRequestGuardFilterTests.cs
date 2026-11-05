// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the CSRF guard of the refresh-cookie endpoints against the real CORS policy provider: a missing
/// X-Requested-With header or a foreign Origin is answered with 403 before the action runs; whitelisted origins,
/// same-host origins (hostname only, port ignored) and requests without Origin pass; without the header only a JSON
/// request passes (it forces a preflight cross-origin; tabs still running the previous Ui send JSON), never a simple-request
/// content type, and never with a foreign Origin.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Authentication;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Klacks.UnitTest.Presentation.Filters;

[TestFixture]
public class RefreshCookieRequestGuardFilterTests
{
    private const string WhitelistedOrigin = "http://localhost:4200";
    private const string ServerHost = "klacks.example.com";
    private const string ForeignOrigin = "https://evil.example.org";

    private ServiceProvider _provider = null!;
    private RefreshCookieRequestGuardFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors(options => options.AddPolicy(
            CorsOriginsOptions.PolicyName,
            policy => policy.WithOrigins(WhitelistedOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        _provider = services.BuildServiceProvider();
        _filter = new RefreshCookieRequestGuardFilter(new CookieCsrfRules(_provider.GetRequiredService<ICorsPolicyProvider>()));
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    [Test]
    public async Task WithHeaderAndNoOrigin_CallsTheAction()
    {
        var (context, nextCalled) = await RunAsync(withHeader: true, origin: null);

        context.Result.ShouldBeNull();
        nextCalled.ShouldBeTrue();
    }

    [Test]
    public async Task WithoutHeader_IsRejectedWith403()
    {
        var (context, nextCalled) = await RunAsync(withHeader: false, origin: null);

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task WithWrongHeaderValue_IsRejectedWith403()
    {
        var (context, nextCalled) = await RunAsync(withHeader: true, origin: null, headerValue: "fetch");

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task WithForeignOrigin_IsRejectedWith403()
    {
        var (context, nextCalled) = await RunAsync(withHeader: true, origin: ForeignOrigin);

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task WithNullOrigin_IsRejectedWith403()
    {
        var (context, nextCalled) = await RunAsync(withHeader: true, origin: "null");

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task WithWhitelistedOrigin_CallsTheAction()
    {
        var (context, nextCalled) = await RunAsync(withHeader: true, origin: WhitelistedOrigin);

        context.Result.ShouldBeNull();
        nextCalled.ShouldBeTrue();
    }

    [Test]
    public async Task WithOriginOfTheRequestedHost_CallsTheAction()
    {
        var (context, nextCalled) = await RunAsync(withHeader: true, origin: "https://" + ServerHost + ":8443");

        context.Result.ShouldBeNull();
        nextCalled.ShouldBeTrue();
    }

    [TestCase("application/json")]
    [TestCase("application/json; charset=utf-8")]
    public async Task WithoutHeader_ButJsonContentType_CallsTheAction(string contentType)
    {
        var (context, nextCalled) = await RunAsync(withHeader: false, origin: null, contentType: contentType);

        context.Result.ShouldBeNull();
        nextCalled.ShouldBeTrue();
    }

    [TestCase("text/plain")]
    [TestCase("application/x-www-form-urlencoded")]
    [TestCase("multipart/form-data; boundary=x")]
    public async Task WithoutHeader_AndASimpleRequestContentType_IsRejectedWith403(string contentType)
    {
        var (context, nextCalled) = await RunAsync(withHeader: false, origin: null, contentType: contentType);

        AssertForbidden(context, nextCalled);
    }

    [Test]
    public async Task JsonContentType_DoesNotExemptAForeignOrigin()
    {
        var (context, nextCalled) = await RunAsync(withHeader: false, origin: ForeignOrigin, contentType: "application/json");

        AssertForbidden(context, nextCalled);
    }

    private static void AssertForbidden(ActionExecutingContext context, bool nextCalled)
    {
        context.Result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        nextCalled.ShouldBeFalse();
    }

    private async Task<(ActionExecutingContext Context, bool NextCalled)> RunAsync(
        bool withHeader,
        string? origin,
        string headerValue = CsrfProofConstants.RequestedWithValue,
        string? contentType = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = _provider };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString(ServerHost);
        httpContext.Request.Method = HttpMethods.Post;
        if (withHeader)
        {
            httpContext.Request.Headers[CsrfProofConstants.RequestedWithHeader] = headerValue;
        }

        if (origin != null)
        {
            httpContext.Request.Headers[HeaderNames.Origin] = origin;
        }

        httpContext.Request.ContentType = contentType;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        var nextCalled = false;

        await _filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        });

        return (context, nextCalled);
    }
}
