using System.Net;
using Klacks.Api.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Middleware;

[TestFixture]
public class NativeHostingSecurityMiddlewareTests
{
    private static async Task<(int Status, bool NextCalled)> InvokeAsync(string path, IPAddress remote)
    {
        var nextCalled = false;
        var middleware = new NativeHostingSecurityMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new ConfigurationBuilder().Build());
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remote;

        await middleware.InvokeAsync(context);

        return (context.Response.StatusCode, nextCalled);
    }

    [TestCase("/internal/health/deep")]
    [TestCase("/_blazor")]
    public async Task LoopbackOnlyPath_from_the_network_is_hidden(string path)
    {
        var (status, nextCalled) = await InvokeAsync(path, IPAddress.Parse("192.168.1.20"));

        status.ShouldBe(StatusCodes.Status404NotFound);
        nextCalled.ShouldBeFalse();
    }

    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("::ffff:127.0.0.1")]
    public async Task LoopbackOnlyPath_from_the_machine_itself_passes(string remote)
    {
        var (_, nextCalled) = await InvokeAsync("/internal/health/deep", IPAddress.Parse(remote));

        nextCalled.ShouldBeTrue();
    }

    [TestCase("/api/backend/Version")]
    [TestCase("/internals")]
    [TestCase("/workplace/schedule")]
    public async Task Other_paths_from_the_network_pass(string path)
    {
        var (_, nextCalled) = await InvokeAsync(path, IPAddress.Parse("192.168.1.20"));

        nextCalled.ShouldBeTrue();
    }

    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state)
        {
            _callbacks.Add((callback, state));
        }

        public async Task FireAsync()
        {
            for (var index = _callbacks.Count - 1; index >= 0; index--)
            {
                await _callbacks[index].Callback(_callbacks[index].State);
            }
        }
    }

    private static async Task<IHeaderDictionary> InvokeWithHeadersAsync(string path, Action<IHeaderDictionary> responseSets, bool https = false)
    {
        var feature = new CapturingResponseFeature();
        var middleware = new NativeHostingSecurityMiddleware(
            context =>
            {
                responseSets(context.Response.Headers);
                return Task.CompletedTask;
            },
            new ConfigurationBuilder().Build());
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpResponseFeature>(feature);
        httpContext.Request.Path = path;
        httpContext.Request.IsHttps = https;
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");

        await middleware.InvokeAsync(httpContext);
        await feature.FireAsync();

        return httpContext.Response.Headers;
    }

    [Test]
    public async Task ResponseWithoutSecurityHeaders_GetsTheDefaultsOnEveryPath()
    {
        foreach (var path in new[] { "/api/backend/Version", "/workplace/schedule", "/oauth/authorize" })
        {
            var headers = await InvokeWithHeadersAsync(path, _ => { }, https: true);

            headers["X-Frame-Options"].ToString().ShouldBe("SAMEORIGIN", path);
            headers["Referrer-Policy"].ToString().ShouldBe("strict-origin-when-cross-origin", path);
            headers["Content-Security-Policy"].ToString().ShouldContain("frame-ancestors 'self'", Case.Sensitive, path);
            headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff", path);
            headers["Strict-Transport-Security"].ToString().ShouldNotBeNullOrEmpty(path);
        }
    }

    [Test]
    public async Task HeadersTheResponseAlreadySet_AreKept()
    {
        var headers = await InvokeWithHeadersAsync("/oauth/authorize", responseHeaders =>
        {
            responseHeaders["X-Frame-Options"] = "DENY";
            responseHeaders["Referrer-Policy"] = "no-referrer";
            responseHeaders["Content-Security-Policy"] = "frame-ancestors 'none'";
        });

        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers["Content-Security-Policy"].ToString().ShouldBe("frame-ancestors 'none'");
        headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
    }

    [Test]
    public async Task OnlyTheHeaderAlreadySet_IsKept_TheMissingOnesAreAdded()
    {
        var headers = await InvokeWithHeadersAsync("/oauth/authorize", responseHeaders => responseHeaders["Referrer-Policy"] = "no-referrer");

        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers["X-Frame-Options"].ToString().ShouldBe("SAMEORIGIN");
        headers["Content-Security-Policy"].ToString().ShouldContain("frame-ancestors 'self'");
    }

    [Test]
    public async Task StrictTransportSecurity_IsSentOnlyOverHttps()
    {
        (await InvokeWithHeadersAsync("/", _ => { })).ContainsKey("Strict-Transport-Security").ShouldBeFalse();
        (await InvokeWithHeadersAsync("/", _ => { }, https: true)).ContainsKey("Strict-Transport-Security").ShouldBeTrue();
    }
}
