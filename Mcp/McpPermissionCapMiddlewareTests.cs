// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for UseMcpPermissionCap: verifies the middleware downgrades Admin principals only on
/// endpoints carrying McpPermissionCapMetadata and leaves every other principal untouched. Whether the
/// middleware sits at the right place in the real pipeline (after UseAuthorization) is proven by
/// Klacks.IntegrationTest/Mcp/McpPermissionCapPipelineTests; a preset principal cannot show that.
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

    private static Endpoint BuildEndpoint(params object[] metadata)
    {
        return new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test-endpoint");
    }

    [Test]
    public async Task MarkedEndpoint_AdminUser_IsCappedToAuthorised()
    {
        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext();
        context.SetEndpoint(BuildEndpoint(McpPermissionCapMetadata.Instance));
        context.User = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin);

        await pipeline(context);

        Assert.That(context.User.IsInRole(Roles.Admin), Is.False);
        Assert.That(context.User.IsInRole(Roles.Authorised), Is.True);
    }

    [Test]
    public async Task UnmarkedEndpoint_AdminUser_IsNotCapped()
    {
        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/backend/works";
        context.SetEndpoint(BuildEndpoint());
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin);
        context.User = principal;

        await pipeline(context);

        Assert.That(context.User, Is.SameAs(principal));
    }

    [Test]
    public async Task NoEndpoint_AdminUser_IsNotCapped()
    {
        var pipeline = BuildPipeline();
        var context = new DefaultHttpContext();
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "admin-user", Roles.Admin);
        context.User = principal;

        await pipeline(context);

        Assert.That(context.User, Is.SameAs(principal));
    }
}
