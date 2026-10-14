// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for EscalationChainsController.Cancel: the Forbidden outcome of the cancel handler must surface as a plain
/// 403 (never as an exception, which would map to 401 and force a logout), the other outcomes as 200 with the body.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[TestFixture]
public class EscalationChainsControllerCancelTests
{
    private const string CurrentUserId = "22222222-2222-2222-2222-222222222222";

    private IMediator _mediator = null!;
    private EscalationChainsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, CurrentUserId) }, "Test");
        _controller = new EscalationChainsController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private void GivenOutcome(EscalationCancelOutcome outcome, bool success)
    {
        _mediator.Send(Arg.Any<CancelEscalationChainCommand>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationCancelResultResource(outcome, new HttpResultResource { Success = success, Messages = "m" }));
    }

    [Test]
    public async Task Cancel_Forbidden_Returns403WithoutBody()
    {
        GivenOutcome(EscalationCancelOutcome.Forbidden, false);

        var result = await _controller.Cancel(Guid.NewGuid(), new CancelEscalationChainResource { Reason = "r" });

        Assert.That(result.Result, Is.TypeOf<StatusCodeResult>());
        Assert.That(((StatusCodeResult)result.Result!).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [TestCase(EscalationCancelOutcome.Cancelled, true)]
    [TestCase(EscalationCancelOutcome.AlreadyResolved, false)]
    public async Task Cancel_OtherOutcomes_Return200WithTheResult(EscalationCancelOutcome outcome, bool success)
    {
        GivenOutcome(outcome, success);

        var result = await _controller.Cancel(Guid.NewGuid(), new CancelEscalationChainResource { Reason = "r" });

        Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
        var body = ((OkObjectResult)result.Result!).Value as HttpResultResource;
        Assert.That(body!.Success, Is.EqualTo(success));
    }
}
