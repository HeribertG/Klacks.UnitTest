// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReorderEscalationRosterCommandHandler — verifies it forwards to
/// IEscalationRosterService.SetOrderAsync and reports success.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class ReorderEscalationRosterCommandHandlerTests
{
    private IEscalationRosterService _rosterService = null!;
    private ReorderEscalationRosterCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _rosterService = Substitute.For<IEscalationRosterService>();
        _sut = new ReorderEscalationRosterCommandHandler(_rosterService);
    }

    [Test]
    public async Task Handle_ForwardsGroupIdAndOrderToService_AndReturnsSuccess()
    {
        var groupId = Guid.NewGuid();
        var orderedUserIds = new List<string> { "user-c", "user-a", "user-b" };

        var result = await _sut.Handle(new ReorderEscalationRosterCommand(groupId, orderedUserIds), CancellationToken.None);

        await _rosterService.Received(1).SetOrderAsync(groupId, orderedUserIds, Arg.Any<CancellationToken>());
        Assert.That(result.Success, Is.True);
    }
}
