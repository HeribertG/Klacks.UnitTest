// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AcknowledgeEscalationChainCommandHandler — verifies it forwards ChainId/UserId to
/// IEscalationChainService.AcknowledgeChainAsync unchanged and surfaces the outcome together with the
/// chain's stored status. The point of the mapping is that a too-late acknowledgement stays
/// distinguishable all the way to the controller, which turns anything but Acknowledged into a 409; the
/// former boolean collapsed "nothing to take" and "too late, nothing was released" into one false and
/// reported a won-stage/lost-chain race as true.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class AcknowledgeEscalationChainCommandHandlerTests
{
    private const string UserId = "planner-a";

    private IEscalationChainService _chainService = null!;
    private IEscalationChainRepository _chainRepository = null!;
    private AcknowledgeEscalationChainCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainService = Substitute.For<IEscalationChainService>();
        _chainRepository = Substitute.For<IEscalationChainRepository>();
        _sut = new AcknowledgeEscalationChainCommandHandler(_chainService, _chainRepository);
    }

    [Test]
    public async Task Handle_ServiceAcknowledges_ReturnsAcknowledgedWithTheChainStatus()
    {
        var chainId = Guid.NewGuid();
        _chainService.AcknowledgeChainAsync(chainId, UserId, Arg.Any<CancellationToken>())
            .Returns(EscalationAcknowledgeOutcome.Acknowledged);
        _chainRepository.GetStatusAsync(chainId, Arg.Any<CancellationToken>())
            .Returns(EscalationChainStatus.Acknowledged);

        var result = await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, UserId), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationAcknowledgeOutcome.Acknowledged));
        Assert.That(result.ChainStatus, Is.EqualTo(EscalationChainStatus.Acknowledged));
    }

    [Test]
    public async Task Handle_ChainAlreadyResolved_ReportsTheOutcomeAndTheStatusThatResolvedIt()
    {
        var chainId = Guid.NewGuid();
        _chainService.AcknowledgeChainAsync(chainId, UserId, Arg.Any<CancellationToken>())
            .Returns(EscalationAcknowledgeOutcome.ChainAlreadyResolved);
        _chainRepository.GetStatusAsync(chainId, Arg.Any<CancellationToken>())
            .Returns(EscalationChainStatus.Exhausted);

        var result = await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, UserId), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationAcknowledgeOutcome.ChainAlreadyResolved));
        Assert.That(
            result.ChainStatus,
            Is.EqualTo(EscalationChainStatus.Exhausted),
            "The status is read back, not derived from the outcome - only the row knows whether the chain was "
            + "exhausted, cancelled or superseded.");
    }

    [Test]
    public async Task Handle_NoNotifiedStage_ReportsNoNotifiedStage()
    {
        var chainId = Guid.NewGuid();
        _chainService.AcknowledgeChainAsync(chainId, UserId, Arg.Any<CancellationToken>())
            .Returns(EscalationAcknowledgeOutcome.NoNotifiedStage);
        _chainRepository.GetStatusAsync(chainId, Arg.Any<CancellationToken>())
            .Returns(EscalationChainStatus.Running);

        var result = await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, UserId), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EscalationAcknowledgeOutcome.NoNotifiedStage));
        Assert.That(result.ChainStatus, Is.EqualTo(EscalationChainStatus.Running));
    }

    [Test]
    public async Task Handle_UnknownChain_LeavesTheChainStatusNull()
    {
        var chainId = Guid.NewGuid();
        _chainService.AcknowledgeChainAsync(chainId, UserId, Arg.Any<CancellationToken>())
            .Returns(EscalationAcknowledgeOutcome.NoNotifiedStage);
        _chainRepository.GetStatusAsync(chainId, Arg.Any<CancellationToken>())
            .Returns((EscalationChainStatus?)null);

        var result = await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, UserId), CancellationToken.None);

        Assert.That(result.ChainStatus, Is.Null);
    }

    [Test]
    public async Task Handle_ForwardsChainIdAndUserIdToService()
    {
        var chainId = Guid.NewGuid();
        _chainService.AcknowledgeChainAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(EscalationAcknowledgeOutcome.Acknowledged);

        await _sut.Handle(new AcknowledgeEscalationChainCommand(chainId, UserId), CancellationToken.None);

        await _chainService.Received(1).AcknowledgeChainAsync(chainId, UserId, Arg.Any<CancellationToken>());
    }
}
