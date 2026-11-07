// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins who may recalculate period hours by hand (owner decision 2026-11-07): recalculating open periods is planning upkeep,
/// so Works/RecalculatePeriodHoursThorough and Works/RecalculatePeriodHours carry no role and admit every signed-in user
/// (the JWT scheme comes from BaseController); the override of frozen periods (ConfirmFrozen) is admin-only and answers
/// 403 with the pinned scheme, never 401 and never the handler's 400, before anything is sent. RecalculateAllUnsealed
/// stays admin-only.
/// </summary>

using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PeriodHoursRecalculationAuthorizationTests
{
    private static readonly DateOnly WindowStart = new(2099, 1, 1);
    private static readonly DateOnly WindowEnd = new(2099, 1, 31);

    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<RecalculatePeriodHoursCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodHoursRecalculationResult(true, 0));
        _mediator.Send(Arg.Any<RecalculatePeriodHoursThoroughCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodHoursRecalculationResult(true, 0));
    }

    [TestCase("RecalculatePeriodHours")]
    [TestCase("RecalculatePeriodHoursThorough")]
    public void ManualRecalculation_CarriesNoRole_AndIsNotAnonymous(string route)
    {
        var action = PostAction(route);

        RolesOf(action).ShouldBeEmpty();
        action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).ShouldBeEmpty();
    }

    [Test]
    public void RecalculateAllUnsealed_StaysAdminOnly()
    {
        RolesOf(PostAction("RecalculateAllUnsealed")).ShouldBe([Roles.Admin]);
    }

    [Test]
    public void TheJwtScheme_IsPinnedByTheControllerBase()
    {
        var schemes = typeof(WorksController).BaseType!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Select(attribute => attribute.AuthenticationSchemes);

        schemes.ShouldContain(JwtBearerDefaults.AuthenticationScheme);
    }

    [TestCase(null)]
    [TestCase(Roles.Authorised)]
    public async Task NormalRecalculation_WithConfirmFrozen_ByANonAdmin_Is403_AndSendsNothing(string? role)
    {
        var result = await NewController(role).RecalculatePeriodHours(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "correction"));

        ShouldBeJwtForbid(result.Result);
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<RecalculatePeriodHoursCommand>(), default);
    }

    [TestCase(null)]
    [TestCase(Roles.Authorised)]
    public async Task ThoroughRecalculation_WithConfirmFrozen_ByANonAdmin_Is403_AndSendsNothing(string? role)
    {
        var result = await NewController(role).RecalculatePeriodHoursThorough(
            new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "correction"));

        ShouldBeJwtForbid(result.Result);
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<RecalculatePeriodHoursThoroughCommand>(), default);
    }

    [Test]
    public async Task ConfirmFrozen_ByAPlannerWithABlankReason_IsStill403_NotTheHandlers400()
    {
        var result = await NewController(null).RecalculatePeriodHours(
            new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: " "));

        ShouldBeJwtForbid(result.Result);
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<RecalculatePeriodHoursCommand>(), default);
    }

    [Test]
    public async Task NormalRecalculation_WithoutConfirmFrozen_ByAPlanner_IsSent()
    {
        var command = new RecalculatePeriodHoursCommand(WindowStart, WindowEnd);

        var result = await NewController(null).RecalculatePeriodHours(command);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(command, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThoroughRecalculation_WithoutConfirmFrozen_ByAPlanner_IsSent()
    {
        var command = new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd);

        var result = await NewController(null).RecalculatePeriodHoursThorough(command);

        result.Result.ShouldBeOfType<AcceptedResult>();
        await _mediator.Received(1).Send(command, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConfirmFrozen_ByAnAdmin_IsSentForBothRecalculations()
    {
        var normal = new RecalculatePeriodHoursCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "correction");
        var thorough = new RecalculatePeriodHoursThoroughCommand(WindowStart, WindowEnd, ConfirmFrozen: true, Reason: "correction");
        var controller = NewController(Roles.Admin);

        (await controller.RecalculatePeriodHours(normal)).Result.ShouldBeOfType<OkObjectResult>();
        (await controller.RecalculatePeriodHoursThorough(thorough)).Result.ShouldBeOfType<AcceptedResult>();

        await _mediator.Received(1).Send(normal, Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(thorough, Arg.Any<CancellationToken>());
    }

    private static void ShouldBeJwtForbid(IActionResult? result)
    {
        var forbid = result.ShouldBeOfType<ForbidResult>();
        forbid.AuthenticationSchemes.ShouldBe([JwtBearerDefaults.AuthenticationScheme]);
    }

    private WorksController NewController(string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-id") };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new WorksController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            }
        };
    }

    private static List<string> RolesOf(MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
            .SelectMany(attribute => attribute.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

    private static MethodInfo PostAction(string route)
    {
        var action = typeof(WorksController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(method => method.GetCustomAttributes<HttpPostAttribute>().Any(post => post.Template == route));

        action.ShouldNotBeNull($"No POST action routed as '{route}' on {nameof(WorksController)}.");
        return action!;
    }
}
