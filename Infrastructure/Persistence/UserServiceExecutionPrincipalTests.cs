// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// UserService is what every visibility check asks for the calling user. Outside an HTTP request it must fall
/// back to the ambient execution principal set by scheduled tasks and background plans, and an HTTP user must
/// always win over it.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Common;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Persistence;

[TestFixture]
public class UserServiceExecutionPrincipalTests
{
    private static UserService Create(HttpContext? httpContext)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return new UserService(accessor, null!, Substitute.For<ILogger<UserService>>());
    }

    [Test]
    public void GetIdString_WithoutRequestOrPrincipal_IsNull()
    {
        Create(null).GetIdString().ShouldBeNull();
    }

    [Test]
    public void GetIdString_WithoutRequest_FallsBackToTheExecutionPrincipal()
    {
        var owner = Guid.NewGuid();

        using (ExecutionPrincipal.Begin(owner))
        {
            Create(null).GetIdString().ShouldBe(owner.ToString());
        }
    }

    [Test]
    public void GetIdString_HttpUserWinsOverTheExecutionPrincipal()
    {
        var httpUser = Guid.NewGuid().ToString();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, httpUser)], "test"))
        };

        using (ExecutionPrincipal.Begin(Guid.NewGuid()))
        {
            Create(context).GetIdString().ShouldBe(httpUser);
        }
    }
}
