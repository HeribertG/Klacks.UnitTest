// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the password-change endpoint of AccountsController: the account comes from the caller's token, not from the
/// e-mail in the body; the endpoint carries the per-user PasswordChange rate limit and AddKlacksRateLimiting registers that policy;
/// the former PUT ChangePassword endpoint stays removed.
/// </summary>

using System.Collections;
using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Authentication;
using Klacks.Api.Presentation.Controllers.UserBackend.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AccountsControllerPasswordChangeTests
{
    private const string CallerId = "11111111-aaaa-bbbb-cccc-000000000001";
    private const string BodyEmail = "colleague@example.com";
    private const string PolicyMapPropertyName = "PolicyMap";
    private const string RemovedActionName = "ChangePassword";

    private IMediator _mediator = null!;
    private AccountsController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _sut = new AccountsController(
            _mediator,
            Substitute.For<ILogger<AccountsController>>(),
            Substitute.For<IUsernameGeneratorService>(),
            Substitute.For<IRefreshTokenCookieService>());
    }

    [Test]
    public async Task ChangePasswordUser_SendsTheCallerIdFromTheToken_NotTheBodyEmail()
    {
        SetCaller(new Claim(ClaimTypes.NameIdentifier, CallerId));
        ChangePasswordUserCommand? sent = null;
        _mediator.Send(Arg.Do<ChangePasswordUserCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new AuthenticatedResult { Success = true });
        var model = new ChangePasswordResource { Email = BodyEmail, OldPassword = "Old123!", Password = "New123!" };

        var response = await _sut.ChangePasswordUser(model);

        response.ShouldBeOfType<OkObjectResult>();
        sent.ShouldNotBeNull();
        sent!.UserId.ShouldBe(CallerId);
        sent.ChangePassword.ShouldBeSameAs(model);
    }

    [Test]
    public async Task ChangePasswordUser_WithoutCallerId_IsUnauthorizedAndSendsNothing()
    {
        SetCaller();
        var model = new ChangePasswordResource { Email = BodyEmail, OldPassword = "Old123!", Password = "New123!" };

        var response = await _sut.ChangePasswordUser(model);

        response.ShouldBeOfType<UnauthorizedResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<ChangePasswordUserCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void ChangePasswordUser_CarriesThePasswordChangeRateLimit()
    {
        var method = typeof(AccountsController).GetMethod(nameof(AccountsController.ChangePasswordUser))!;

        var attribute = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        attribute.ShouldNotBeNull();
        attribute!.PolicyName.ShouldBe(RateLimitingPolicies.PasswordChange);
    }

    [Test]
    public void AddKlacksRateLimiting_RegistersThePasswordChangePolicy()
    {
        var services = new ServiceCollection();

        services.AddKlacksRateLimiting();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var policyMap = (IDictionary)typeof(RateLimiterOptions)
            .GetProperty(PolicyMapPropertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(options)!;

        policyMap.Keys.Cast<string>().ShouldContain(RateLimitingPolicies.PasswordChange);
    }

    [Test]
    public void TheFormerPutChangePasswordEndpoint_IsGone()
    {
        typeof(AccountsController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ShouldNotContain(RemovedActionName);
    }

    private void SetCaller(params Claim[] claims)
    {
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }
}
