// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Shared test helpers for period-closing handler tests: user identity setup for admin and non-admin scenarios.
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

internal static class PeriodClosingTestHelpers
{
    public static void GivenUserIsAdmin(IHttpContextAccessor httpContextAccessor, string userName)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, Roles.Admin),
                new Claim(ClaimNames.IsAuthorised, "true"),
                new Claim(ClaimTypes.NameIdentifier, userName)
            ], "TestAuth"));
        httpContextAccessor.HttpContext.Returns(httpContext);
    }

    public static void GivenUserIsNotAdmin(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.User)], "TestAuth"));
        httpContextAccessor.HttpContext.Returns(httpContext);
    }
}
