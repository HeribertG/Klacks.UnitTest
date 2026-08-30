// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProactiveGovernanceController — covers the boundary validation of the two enum
/// fields: an out-of-range maxAction or autonomyLevel is rejected with 400 before the command ever
/// reaches the mediator, while a defined value is forwarded.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Mvc;

[TestFixture]
public class ProactiveGovernanceControllerTests
{
    private IMediator _mediator = null!;
    private ProactiveGovernanceController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<SetProactiveGovernanceCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDto());

        _controller = new ProactiveGovernanceController(_mediator);
    }

    [Test]
    public async Task Put_WithAutonomyLevelOutsideTheEnum_ReturnsBadRequest()
    {
        var request = new UpdateProactiveGovernanceRequest { AutonomyLevel = 4 };

        var result = await _controller.Put(request, CancellationToken.None);

        Assert.That(result.Result, Is.TypeOf<BadRequestObjectResult>());
        await _mediator.DidNotReceive()
            .Send(Arg.Any<SetProactiveGovernanceCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_WithADefinedAutonomyLevel_ForwardsItToTheCommand()
    {
        var request = new UpdateProactiveGovernanceRequest { AutonomyLevel = (int)AutonomyLevel.Autonomous };

        var result = await _controller.Put(request, CancellationToken.None);

        Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
        await _mediator.Received(1).Send(
            Arg.Is<SetProactiveGovernanceCommand>(command =>
                command.AutonomyLevel == AutonomyLevel.Autonomous),
            Arg.Any<CancellationToken>());
    }
}
