// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BreakUserContextProvider: role-based resolution of admin and authorised flags from the HTTP context.
/// </summary>

using Klacks.Api.Application.Services.Breaks;
using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Services.Breaks;

[TestFixture]
public class BreakUserContextProviderTests
{
    private IHttpContextAccessor _httpContextAccessor = null!;
    private BreakUserContextProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _provider = new BreakUserContextProvider(_httpContextAccessor);
    }

    [Test]
    public void GetUserContext_SetsIsAuthorised_WhenUserHasAuthorisedRole()
    {
        GivenUserWithRole(Roles.Authorised, "authorised-user");

        var context = _provider.GetUserContext();

        context.IsAuthorised.ShouldBeTrue();
        context.IsAdmin.ShouldBeFalse();
        context.UserName.ShouldBe("authorised-user");
    }

    [Test]
    public void GetUserContext_SetsIsAdmin_WhenUserHasAdminRole()
    {
        GivenUserWithRole(Roles.Admin, "admin-user");

        var context = _provider.GetUserContext();

        context.IsAdmin.ShouldBeTrue();
        context.IsAuthorised.ShouldBeFalse();
        context.UserName.ShouldBe("admin-user");
    }

    [Test]
    public void GetUserContext_SetsNoFlags_WhenUserHasOnlyUserRole()
    {
        GivenUserWithRole(Roles.User, "regular-user");

        var context = _provider.GetUserContext();

        context.IsAdmin.ShouldBeFalse();
        context.IsAuthorised.ShouldBeFalse();
    }

    [Test]
    public void GetUserContext_ReturnsDefaults_WhenHttpContextIsMissing()
    {
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var context = _provider.GetUserContext();

        context.IsAdmin.ShouldBeFalse();
        context.IsAuthorised.ShouldBeFalse();
        context.UserName.ShouldBe("Unknown");
    }

    private void GivenUserWithRole(string role, string userName)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.NameIdentifier, userName)
            ], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }
}
