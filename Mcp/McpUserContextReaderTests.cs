// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Claims;
using Klacks.Api.Presentation.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpUserContextReaderTests
{
    [Test]
    public void NullPrincipal_ReturnsAnonymousContext()
    {
        var context = McpUserContextReader.Read(null);

        Assert.That(context.UserId, Is.EqualTo(Guid.Empty));
        Assert.That(context.TenantId, Is.EqualTo(Guid.Empty));
        Assert.That(context.Permissions, Is.Empty);
    }

    [Test]
    public void PrincipalWithClaims_ReturnsParsedContext()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var principal = McpTestData.Principal(userId, tenantId, "alice", "Admin", "CanViewClients");

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.UserId, Is.EqualTo(userId));
        Assert.That(context.TenantId, Is.EqualTo(tenantId));
        Assert.That(context.UserName, Is.EqualTo("alice"));
        Assert.That(context.Permissions, Is.EquivalentTo(new[] { "Admin", "CanViewClients" }));
    }

    [Test]
    public void PrincipalWithoutGuidClaims_ReturnsEmptyGuids()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "not-a-guid") }, "TestAuth"));

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.UserId, Is.EqualTo(Guid.Empty));
        Assert.That(context.TenantId, Is.EqualTo(Guid.Empty));
    }
}
