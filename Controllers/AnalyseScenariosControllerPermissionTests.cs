// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Accepting, rejecting and deleting scenarios change or discard plans, so the REST endpoints demand CanEditShifts, the
/// same right the accept/reject skills require. A caller without a role (planner floor) is refused with 403 (Forbid on
/// the JWT scheme, never 401 and never a cookie redirect) before anything is dispatched; Admin and Authorised are served.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AnalyseScenariosControllerPermissionTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<IRequest<bool>>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private AnalyseScenariosController Controller(params string[] roles) => new(_mediator)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    roles.Select(role => new Claim(ClaimTypes.Role, role)).ToArray(), "TestAuth"))
            }
        }
    };

    private static void ShouldBeJwtForbid(ActionResult<bool> result)
    {
        var forbid = result.Result.ShouldBeOfType<ForbidResult>();
        forbid.AuthenticationSchemes.ShouldBe(new[] { JwtBearerDefaults.AuthenticationScheme });
    }

    [Test]
    public async Task PlannerFloor_IsForbiddenToAcceptRejectAndDelete_AndNothingIsDispatched()
    {
        var controller = Controller();

        ShouldBeJwtForbid(await controller.Accept(Guid.NewGuid()));
        ShouldBeJwtForbid(await controller.Reject(Guid.NewGuid()));
        ShouldBeJwtForbid(await controller.Delete(Guid.NewGuid()));
        ShouldBeJwtForbid(await controller.DeleteAll(Guid.NewGuid()));

        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<IRequest<bool>>(), default);
    }

    [TestCase(Roles.Admin)]
    [TestCase(Roles.Authorised)]
    public async Task EditingRoles_AreServed(string role)
    {
        var controller = Controller(role);

        (await controller.Accept(Guid.NewGuid())).Result.ShouldBeOfType<OkObjectResult>();
        (await controller.Reject(Guid.NewGuid())).Result.ShouldBeOfType<OkObjectResult>();
        (await controller.Delete(Guid.NewGuid())).Result.ShouldBeOfType<OkObjectResult>();
        (await controller.DeleteAll(Guid.NewGuid())).Result.ShouldBeOfType<OkObjectResult>();

        await _mediator.Received(1).Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>());
    }
}
