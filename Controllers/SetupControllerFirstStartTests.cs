// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the authorization surface of SetupController: only the two first-start actions are anonymous, each under its
/// own rate limit (the status probe under SetupStatus, which AddKlacksRateLimiting registers, the completion under the
/// login limit); the seeded-admin actions stay behind the JWT scheme. The first-start actions dispatch the first-start
/// variants of the status query and the completion command.
/// </summary>

using System.Collections;
using System.Reflection;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.DTOs.Setup;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Authentification;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class SetupControllerFirstStartTests
{
    private const string PolicyMapPropertyName = "PolicyMap";

    private static MethodInfo Action(string name) =>
        typeof(SetupController).GetMethod(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException($"SetupController.{name} not found");

    [TestCase(nameof(SetupController.GetFirstStartStatus), RateLimitingPolicies.SetupStatus)]
    [TestCase(nameof(SetupController.CompleteFirstStart), RateLimitingPolicies.Login)]
    public void FirstStartActions_AreAnonymousAndRateLimited(string actionName, string policy)
    {
        var action = Action(actionName);

        action.GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        action.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.ShouldBe(policy);
    }

    [TestCase(nameof(SetupController.GetStatus))]
    [TestCase(nameof(SetupController.CompleteOwnAdmin))]
    public void SeededAdminActions_StayAuthenticated(string actionName)
    {
        var action = Action(actionName);

        action.GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
        typeof(SetupController).GetCustomAttribute<AllowAnonymousAttribute>(inherit: true).ShouldBeNull();
        action.GetCustomAttribute<AuthorizeAttribute>().ShouldNotBeNull();
    }

    [Test]
    public void AddKlacksRateLimiting_RegistersTheSetupStatusPolicy()
    {
        var services = new ServiceCollection();

        services.AddKlacksRateLimiting();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var policyMap = (IDictionary)typeof(RateLimiterOptions)
            .GetProperty(PolicyMapPropertyName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(options)!;

        policyMap.Keys.Cast<string>().ShouldContain(RateLimitingPolicies.SetupStatus);
    }

    private static SetupController Controller(IMediator mediator, string remoteAddress)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        return new SetupController(mediator) { ControllerContext = new ControllerContext { HttpContext = httpContext } };
    }

    [TestCase("127.0.0.1", true)]
    [TestCase("192.168.1.20", false)]
    public async Task GetFirstStartStatus_AsksTheFirstStartVariantForTheRequestOrigin(string remoteAddress, bool isLocal)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetSetupStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SetupStatusResource { RequiresOwnAdmin = false });
        var sut = Controller(mediator, remoteAddress);

        await sut.GetFirstStartStatus();

        await mediator.Received(1).Send(
            Arg.Is<GetSetupStatusQuery>(query => query.IsFirstStart && query.IsLocalRequest == isLocal),
            Arg.Any<CancellationToken>());
    }

    [TestCase("127.0.0.1", true)]
    [TestCase("192.168.1.20", false)]
    public async Task CompleteFirstStart_SendsTheFirstStartVariantWithOriginAndCode(string remoteAddress, bool isLocal)
    {
        var mediator = Substitute.For<IMediator>();
        var sut = Controller(mediator, remoteAddress);
        var registration = new FirstStartRegistrationResource { Email = "owner@example.com", SetupCode = "ABCD-EFGH-JK23" };

        await sut.CompleteFirstStart(registration);

        await mediator.Received(1).Send(
            Arg.Is<CompleteOwnAdminSetupCommand>(command =>
                command.IsFirstStart
                && command.NewAdmin == registration
                && command.IsLocalRequest == isLocal
                && command.SetupCode == "ABCD-EFGH-JK23"),
            Arg.Any<CancellationToken>());
    }
}
