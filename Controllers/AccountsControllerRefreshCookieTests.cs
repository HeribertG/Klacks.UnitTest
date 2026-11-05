// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the refresh token cookie on the session endpoints of AccountsController and OAuth2Controller: login, refresh,
/// password change and the OAuth2 callback set it; logout and a successful password reset delete it. Refresh without a
/// token in body or cookie is 401 without calling the handler; a refresh rejected by the handler leaves the cookie alone
/// (a refresh that fails must not delete a cookie another request just renewed). Logout revokes only this device's token
/// (cookie, else body), also for an authenticated caller, and always answers 200. The endpoints carry the CSRF guard,
/// Logout is anonymous, RefreshToken and Logout accept an empty body, and the routes lie below the cookie path.
/// Next to it the same flows set the klacks_access cookie with the issued access token (Path=/, HttpOnly, Max-Age = access
/// token lifetime), and logout and a successful password reset delete it as well; a rejected refresh leaves it alone.
/// </summary>

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Commands.OAuth2;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.OAuth2;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Authentication;
using Klacks.Api.Presentation.Controllers.UserBackend.Authentification;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AccountsControllerRefreshCookieTests
{
    private const string CallerId = "11111111-aaaa-bbbb-cccc-000000000002";
    private const string IssuedToken = "issued-refresh-token";
    private const string CookieToken = "cookie-refresh-token";
    private const string IssuedAccessToken = "issued-access-token";
    private const int MinimumRefreshPermitsPerWindow = 120;
    private const string RouteSeparator = "/";
    private const string ControllerToken = "[controller]";
    private const string ControllerSuffix = "Controller";

    private IMediator _mediator = null!;
    private AccountsController _sut = null!;
    private DefaultHttpContext _httpContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _sut = new AccountsController(
            _mediator,
            Substitute.For<ILogger<AccountsController>>(),
            Substitute.For<IUsernameGeneratorService>(),
            CreateCookieService(),
            CreateAccessCookieService());
        _httpContext = CreateHttpContext();
        _sut.ControllerContext = new ControllerContext { HttpContext = _httpContext };
    }

    [Test]
    public async Task LoginUser_SetsTheCookieWithTheIssuedToken()
    {
        _mediator.Send(Arg.Any<LoginUserQuery>(), Arg.Any<CancellationToken>()).Returns(Issued());

        var response = await _sut.LoginUser(new LogInResource { Email = "user@example.com", Password = "secret" });

        response.ShouldBeOfType<OkObjectResult>();
        SetCookie(_httpContext).Value.ToString().ShouldBe(IssuedToken);
        AccessCookie(_httpContext).Value.ToString().ShouldBe(IssuedAccessToken);
    }

    [Test]
    public async Task RefreshToken_WithCookieOnly_PassesTheCookieAndSetsTheSessionCookie()
    {
        _httpContext.Request.Headers.Cookie = $"{RefreshTokenCookieConstants.CookieName}={CookieToken}";
        RefreshTokenQuery? sent = null;
        _mediator.Send(Arg.Do<RefreshTokenQuery>(q => sent = q), Arg.Any<CancellationToken>()).Returns(Issued());

        var response = await _sut.RefreshToken(null);

        response.ShouldBeOfType<OkObjectResult>();
        sent.ShouldNotBeNull();
        sent!.CookieRefreshToken.ShouldBe(CookieToken);
        sent.RefreshRequest.RefreshToken.ShouldBeEmpty();
        SetCookie(_httpContext).Value.ToString().ShouldBe(IssuedToken);
        AccessCookie(_httpContext).Value.ToString().ShouldBe(IssuedAccessToken);
    }

    [Test]
    public async Task RefreshToken_WithBodyTokenOnly_StillRefreshesAndSetsTheCookie()
    {
        var body = new RefreshRequestResource { RefreshToken = "legacy-body-token" };
        RefreshTokenQuery? sent = null;
        _mediator.Send(Arg.Do<RefreshTokenQuery>(q => sent = q), Arg.Any<CancellationToken>()).Returns(Issued());

        var response = await _sut.RefreshToken(body);

        response.ShouldBeOfType<OkObjectResult>();
        sent!.RefreshRequest.ShouldBeSameAs(body);
        sent.CookieRefreshToken.ShouldBeNull();
        SetCookie(_httpContext).Value.ToString().ShouldBe(IssuedToken);
    }

    [Test]
    public async Task RefreshToken_WithoutBodyOrCookie_Is401AndCallsNoHandler()
    {
        var response = await _sut.RefreshToken(null);

        response.ShouldBeOfType<UnauthorizedObjectResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<RefreshTokenQuery>(), Arg.Any<CancellationToken>());
        _httpContext.Response.Headers.SetCookie.Count.ShouldBe(0);
    }

    [Test]
    public async Task RefreshToken_RejectedByTheHandler_Is401AndLeavesTheCookieAlone()
    {
        _httpContext.Request.Headers.Cookie = $"{RefreshTokenCookieConstants.CookieName}={CookieToken}";
        _mediator.Send(Arg.Any<RefreshTokenQuery>(), Arg.Any<CancellationToken>()).Returns((TokenResource?)null);

        var response = await _sut.RefreshToken(null);

        response.ShouldBeOfType<UnauthorizedObjectResult>();
        _httpContext.Response.Headers.SetCookie.Count.ShouldBe(0);
    }

    [Test]
    public async Task LoginUser_WithoutAnIssuedAccessToken_SetsNoAccessCookie()
    {
        _mediator.Send(Arg.Any<LoginUserQuery>(), Arg.Any<CancellationToken>()).Returns(new TokenResource { Success = false });

        await _sut.LoginUser(new LogInResource { Email = "user@example.com", Password = "wrong" });

        _httpContext.Response.Headers.SetCookie.Count.ShouldBe(0);
    }

    [Test]
    public async Task ChangePasswordUser_SetsTheCookieOfTheRenewedSession()
    {
        SetCaller(new Claim(ClaimTypes.NameIdentifier, CallerId));
        _mediator.Send(Arg.Any<ChangePasswordUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(new AuthenticatedResult { Success = true, RefreshToken = IssuedToken, Token = IssuedAccessToken });

        await _sut.ChangePasswordUser(new ChangePasswordResource { OldPassword = "Old123!", Password = "New123!" });

        SetCookie(_httpContext).Value.ToString().ShouldBe(IssuedToken);
        AccessCookie(_httpContext).Value.ToString().ShouldBe(IssuedAccessToken);
    }

    [Test]
    public async Task Logout_WithCookie_SendsTheCookieToken_EvenForAnAuthenticatedCaller_AndDeletesTheCookie()
    {
        SetCaller(new Claim(ClaimTypes.NameIdentifier, CallerId));
        _httpContext.Request.Headers.Cookie = $"{RefreshTokenCookieConstants.CookieName}={CookieToken}";
        LogoutCommand? sent = null;
        _mediator.Send(Arg.Do<LogoutCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Unit.Value);

        var response = await _sut.Logout(new RefreshRequestResource { RefreshToken = "body-token" });

        response.ShouldBeOfType<OkResult>();
        sent.ShouldBe(new LogoutCommand(CookieToken));
        AssertCookieDeleted(_httpContext);
    }

    [Test]
    public async Task Logout_WithoutCookie_SendsTheBodyToken()
    {
        LogoutCommand? sent = null;
        _mediator.Send(Arg.Do<LogoutCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Unit.Value);

        await _sut.Logout(new RefreshRequestResource { RefreshToken = "body-token" });

        sent.ShouldBe(new LogoutCommand("body-token"));
        AssertCookieDeleted(_httpContext);
    }

    [Test]
    public async Task Logout_WithNothing_Is200AndDeletesTheCookie()
    {
        LogoutCommand? sent = null;
        _mediator.Send(Arg.Do<LogoutCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Unit.Value);

        var response = await _sut.Logout(null);

        response.ShouldBeOfType<OkResult>();
        sent.ShouldBe(new LogoutCommand(null));
        AssertCookieDeleted(_httpContext);
        AssertAccessCookieDeleted(_httpContext);
    }

    [Test]
    public async Task ResetPassword_OnSuccess_DeletesTheCookie()
    {
        _mediator.Send(Arg.Any<ResetPasswordCommand>(), Arg.Any<CancellationToken>())
            .Returns(new AuthenticatedResult { Success = true });

        await _sut.ResetPassword(new ResetPasswordResource { Token = "reset", Password = "New123!" });

        AssertCookieDeleted(_httpContext);
        AssertAccessCookieDeleted(_httpContext);
    }

    [Test]
    public async Task OAuth2Callback_SetsTheCookieWithTheIssuedToken()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<OAuth2CallbackCommand>(), Arg.Any<CancellationToken>()).Returns(Issued());
        var httpContext = CreateHttpContext();
        var controller = new OAuth2Controller(
            mediator, Substitute.For<ILogger<OAuth2Controller>>(), CreateCookieService(), CreateAccessCookieService())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var response = await controller.Callback(new OAuth2CallbackRequest());

        response.ShouldBeOfType<OkObjectResult>();
        SetCookie(httpContext).Value.ToString().ShouldBe(IssuedToken);
        AccessCookie(httpContext).Value.ToString().ShouldBe(IssuedAccessToken);
    }

    [TestCase(nameof(AccountsController.RefreshToken))]
    [TestCase(nameof(AccountsController.Logout))]
    [TestCase(nameof(AccountsController.ChangePasswordUser))]
    public void CookieReadingEndpoints_CarryTheCsrfGuard(string action)
    {
        Method(action).GetCustomAttribute<RefreshCookieRequestGuardAttribute>().ShouldNotBeNull(action);
    }

    [TestCase(nameof(AccountsController.RefreshToken))]
    [TestCase(nameof(AccountsController.Logout))]
    public void AnonymousCookieEndpoints_CarryTheRefreshRateLimit(string action)
    {
        Method(action).GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.ShouldBe(RateLimitingPolicies.RefreshToken);
    }

    [TestCase(nameof(AccountsController.RefreshToken))]
    [TestCase(nameof(AccountsController.Logout))]
    [TestCase(nameof(AccountsController.ChangePasswordUser))]
    [TestCase(nameof(AccountsController.ResetPassword))]
    [TestCase(nameof(AccountsController.LoginUser))]
    public void CookieEndpointRoutes_LieBelowTheCookiePath(string action)
    {
        var route = BuildRoute(typeof(AccountsController), Method(action));

        route.StartsWith(RefreshTokenCookieConstants.CookiePath + RouteSeparator, StringComparison.Ordinal)
            .ShouldBeTrue($"{route} must lie below {RefreshTokenCookieConstants.CookiePath}, or the browser does not send the cookie");
    }

    [Test]
    public void Logout_IsAnonymous_AndAcceptsAnEmptyBody()
    {
        var method = Method(nameof(AccountsController.Logout));

        method.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        method.GetParameters().Single().GetCustomAttribute<FromBodyAttribute>()!.EmptyBodyBehavior.ShouldBe(EmptyBodyBehavior.Allow);
    }

    [Test]
    public void RefreshToken_CarriesTheRefreshRateLimit_WhichLeavesRoomForTheMigrationWave()
    {
        Method(nameof(AccountsController.RefreshToken)).GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .ShouldBe(RateLimitingPolicies.RefreshToken);
        RateLimitingPolicies.RefreshTokenPermitLimit.ShouldBeGreaterThanOrEqualTo(MinimumRefreshPermitsPerWindow);
    }

    [Test]
    public void RefreshToken_AcceptsAnEmptyBody()
    {
        var parameter = Method(nameof(AccountsController.RefreshToken)).GetParameters().Single();

        parameter.GetCustomAttribute<FromBodyAttribute>()!.EmptyBodyBehavior.ShouldBe(EmptyBodyBehavior.Allow);
    }

    private static MethodInfo Method(string name) => typeof(AccountsController).GetMethod(name)!;

    private static string BuildRoute(Type controllerType, MethodInfo action)
    {
        var controllerTemplate = controllerType.GetCustomAttribute<RouteAttribute>(inherit: true)!.Template
            .Replace(ControllerToken, controllerType.Name[..^ControllerSuffix.Length]);
        var actionTemplate = action.GetCustomAttribute<HttpPostAttribute>()!.Template;
        return RouteSeparator + controllerTemplate + RouteSeparator + actionTemplate;
    }

    private static RefreshTokenCookieService CreateCookieService() =>
        new(Options.Create(new RefreshTokenCookieOptions()));

    private static AccessTokenCookieService CreateAccessCookieService() =>
        new(Options.Create(new RefreshTokenCookieOptions()));

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("klacks.example.com");
        return context;
    }

    private static TokenResource Issued() => new() { Success = true, Token = IssuedAccessToken, RefreshToken = IssuedToken };

    private static SetCookieHeaderValue SetCookie(HttpContext context)
    {
        var cookie = SingleCookieNamed(context, RefreshTokenCookieConstants.CookieName);
        cookie.Path.ToString().ShouldBe(RefreshTokenCookieConstants.CookiePath);
        return cookie;
    }

    private static SetCookieHeaderValue AccessCookie(HttpContext context)
    {
        var cookie = SingleCookieNamed(context, AccessTokenCookieConstants.CookieName);
        cookie.Path.ToString().ShouldBe(AccessTokenCookieConstants.CookiePath);
        cookie.Secure.ShouldBeTrue();
        cookie.SameSite.ShouldBe(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        if (!string.IsNullOrEmpty(cookie.Value.ToString()))
        {
            cookie.MaxAge.ShouldBe(AccessTokenLifetime.Duration);
        }

        return cookie;
    }

    private static SetCookieHeaderValue SingleCookieNamed(HttpContext context, string name)
    {
        var cookies = SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.ToList())
            .Where(c => c.Name.Equals(name))
            .ToList();
        cookies.Count.ShouldBe(1, name);
        cookies[0].HttpOnly.ShouldBeTrue();
        return cookies[0];
    }

    private static void AssertAccessCookieDeleted(HttpContext context)
    {
        var cookie = AccessCookie(context);
        cookie.Value.ToString().ShouldBeEmpty();
        cookie.Expires!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow);
    }

    private static void AssertCookieDeleted(HttpContext context)
    {
        var cookie = SetCookie(context);
        cookie.Value.ToString().ShouldBeEmpty();
        cookie.Expires!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow);
    }

    private void SetCaller(params Claim[] claims)
    {
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
