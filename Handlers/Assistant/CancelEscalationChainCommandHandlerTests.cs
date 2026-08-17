// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CancelEscalationChainCommandHandler — a thin pass-through onto
/// IEscalationChainService.CancelAsync; the conditional cancel transition itself is already covered
/// where CancelAsync is implemented, so this only proves the handler wires the command's fields
/// through unchanged and maps the boolean result to HttpResultResource.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class CancelEscalationChainCommandHandlerTests
{
    private IEscalationChainService _chainService = null!;
    private CancelEscalationChainCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainService = Substitute.For<IEscalationChainService>();
        _sut = new CancelEscalationChainCommandHandler(_chainService);
    }

    [Test]
    public async Task Handle_ForwardsAllFieldsToService_AndMapsSuccessResult()
    {
        var chainId = Guid.NewGuid();
        const string userId = "planner-a";
        const string userName = "Planner A";
        const string reason = "Shift already covered by another employee.";
        _chainService.CancelAsync(chainId, userId, userName, reason, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, userId, userName, reason), CancellationToken.None);

        Assert.That(result.Success, Is.True);
        await _chainService.Received(1).CancelAsync(chainId, userId, userName, reason, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ServiceFails_ReturnsFailureResult()
    {
        var chainId = Guid.NewGuid();
        const string userId = "planner-a";
        const string userName = "Planner A";
        const string reason = "Duplicate cancel attempt.";
        _chainService.CancelAsync(chainId, userId, userName, reason, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.Handle(new CancelEscalationChainCommand(chainId, userId, userName, reason), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Messages, Is.Not.Empty);
    }
}
