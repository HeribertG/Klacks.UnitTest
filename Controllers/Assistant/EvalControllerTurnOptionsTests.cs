// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the turn-options endpoint of EvalController: a missing body and a missing identity are
/// client errors, the caller identity and the raw message reach the query, and the wire payload is the
/// bare option list so the outcome enum never reaches the browser.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class EvalControllerTurnOptionsTests
{
    private const string UserId = "user-1";

    private IMediator _mediator = null!;
    private EvalController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new EvalController(
            Substitute.For<IEvalRunnerService>(),
            Substitute.For<IEvalRunRepository>(),
            _mediator,
            NullLogger<EvalController>.Instance);
        SetUser(UserId);
    }

    private void SetUser(string? userId)
    {
        var claims = new List<Claim>();
        if (userId != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [Test]
    public async Task TurnOptions_WithoutABody_ReturnsBadRequest()
    {
        var result = await _controller.TurnOptions(null!, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task TurnOptions_WithoutAnIdentity_ReturnsUnauthorized()
    {
        SetUser(null);

        var result = await _controller.TurnOptions(
            new EvalController.TurnOptionsRequest { UserMessage = "Lege einen Kunden an" }, CancellationToken.None);

        result.Result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Test]
    public async Task TurnOptions_OwnTrajectory_ReturnsTheBareOptionList()
    {
        _mediator.Send(Arg.Any<GetTurnOptionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TurnOptionsResult
            {
                Outcome = TurnOptionsOutcome.Found,
                Options = new List<TurnOptionDto>
                {
                    new() { SkillName = "create_client", DisplayName = "Create client", Description = "Creates a client." }
                }
            });

        var result = await _controller.TurnOptions(
            new EvalController.TurnOptionsRequest { UserMessage = "Lege einen Kunden an" }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<List<TurnOptionDto>>();
        payload.Count.ShouldBe(1);
        payload[0].SkillName.ShouldBe("create_client");
    }

    [Test]
    public async Task TurnOptions_ForwardsTheCallerIdentityAndTheRawMessage()
    {
        _mediator.Send(Arg.Any<GetTurnOptionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TurnOptionsResult { Outcome = TurnOptionsOutcome.NotFound });

        await _controller.TurnOptions(
            new EvalController.TurnOptionsRequest { UserMessage = "Lege einen Kunden an" }, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetTurnOptionsQuery>(query =>
                query.UserId == UserId && query.UserMessage == "Lege einen Kunden an"),
            Arg.Any<CancellationToken>());
    }
}
