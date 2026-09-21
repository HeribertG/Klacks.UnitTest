// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EscalationChainsController.Acknowledge — the status code is the whole point here. A
/// take-over that released something answers 200; anything else answers 409 with the same body, because
/// the alternative, a 200 carrying Outcome = ChainAlreadyResolved, is what let the intervention list
/// report a lapsed approval as done. The 409 body still carries the outcome and the chain's status so the
/// caller can tell the user which of the two happened. Not covered here: the [Authorize] scheme pin,
/// which is enforced project-wide by ControllerAuthorizeSchemeGuardTests.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[TestFixture]
public class EscalationChainsControllerAcknowledgeTests
{
    private const string CurrentUserId = "22222222-2222-2222-2222-222222222222";

    private IMediator _mediator = null!;
    private EscalationChainsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, CurrentUserId) }, "Test");

        _controller = new EscalationChainsController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    [Test]
    public async Task Acknowledge_Acknowledged_ReturnsOk()
    {
        GivenOutcome(EscalationAcknowledgeOutcome.Acknowledged, EscalationChainStatus.Acknowledged);

        var result = await _controller.Acknowledge(Guid.NewGuid());

        Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
    }

    [Test]
    public async Task Acknowledge_ChainAlreadyResolved_ReturnsConflictWithTheOutcomeAndStatus()
    {
        GivenOutcome(EscalationAcknowledgeOutcome.ChainAlreadyResolved, EscalationChainStatus.Exhausted);

        var result = await _controller.Acknowledge(Guid.NewGuid());

        Assert.That(result.Result, Is.TypeOf<ConflictObjectResult>());

        var body = ((ConflictObjectResult)result.Result!).Value as EscalationAcknowledgeResultResource;
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Outcome, Is.EqualTo(EscalationAcknowledgeOutcome.ChainAlreadyResolved));
        Assert.That(body.ChainStatus, Is.EqualTo(EscalationChainStatus.Exhausted));
    }

    [Test]
    public async Task Acknowledge_NoNotifiedStage_ReturnsConflict()
    {
        GivenOutcome(EscalationAcknowledgeOutcome.NoNotifiedStage, EscalationChainStatus.Running);

        var result = await _controller.Acknowledge(Guid.NewGuid());

        Assert.That(result.Result, Is.TypeOf<ConflictObjectResult>());
    }

    private void GivenOutcome(EscalationAcknowledgeOutcome outcome, EscalationChainStatus chainStatus) =>
        _mediator.Send(Arg.Any<AcknowledgeEscalationChainCommand>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationAcknowledgeResultResource { Outcome = outcome, ChainStatus = chainStatus });
}
