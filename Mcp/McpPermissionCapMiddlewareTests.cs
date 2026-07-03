// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UseMcpPermissionCap: verifies the middleware only downgrades Admin principals
/// on the /mcp route and leaves every other route's principal untouched.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpPermissionCapMiddlewareTests
{
    private static RequestDelegate BuildPipeline()
    {
        var app = new ApplicationBuilder(Substitute.For<IServiceProvider>());
        app.UseMcpPermissionCap();
        app.Run(_ => Task.CompletedTask);

        return app.Build();
    }

    [Test]
    public async Task McpPath_AdminUser_IsCappedToAuthorised()
    {
        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.User = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin);

        await pipeline(context);

        Assert.That(context.User.IsInRole(Roles.Admin), Is.False);
        Assert.That(context.User.IsInRole(Roles.Authorised), Is.True);
    }

    [Test]
    public async Task NonMcpPath_AdminUser_IsNotCapped()
    {
        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/backend/works";
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin);
        context.User = principal;

        await pipeline(context);

        Assert.That(context.User, Is.SameAs(principal));
        Assert.That(context.User.IsInRole(Roles.Admin), Is.True);
    }
}
