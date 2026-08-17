// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AcknowledgeEscalationChainCommandHandler — verifies it forwards ChainId/UserId to
/// IEscalationChainService.AcknowledgeChainAsync unchanged and maps the boolean result to
/// HttpResultResource.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class AcknowledgeEscalationChainCommandHandlerTests
{
    private IEscalationChainService _chainService = null!;
    private AcknowledgeEscalationChainCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainService = Substitute.For<IEscalationChainService>();
        _sut = new AcknowledgeEscalationChainCommandHandler(_chainService);
    }

    [Test]
    public async Task Handle_ServiceSucceeds_ReturnsSuccessResult()
    {
        var chainId = Guid.NewGuid();
        const string userId = "planner-a";
        _chainService.AcknowledgeChainAsync(chainId, userId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, userId), CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Messages, Is.Not.Empty);
    }

    [Test]
    public async Task Handle_ServiceFails_ReturnsFailureResultWithMessage()
    {
        var chainId = Guid.NewGuid();
        const string userId = "planner-a";
        _chainService.AcknowledgeChainAsync(chainId, userId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, userId), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Messages, Is.Not.Empty);
    }

    [Test]
    public async Task Handle_ForwardsChainIdAndUserIdToService()
    {
        var chainId = Guid.NewGuid();
        const string userId = "planner-a";
        _chainService.AcknowledgeChainAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, userId), CancellationToken.None);

        await _chainService.Received(1).AcknowledgeChainAsync(chainId, userId, Arg.Any<CancellationToken>());
    }
}
