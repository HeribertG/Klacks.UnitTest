using System.Net;
using Klacks.Api.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
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
}
