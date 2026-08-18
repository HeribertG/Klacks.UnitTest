// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReorderEscalationRosterCommandHandler — verifies it forwards to
/// IEscalationRosterService.ReorderAsync and returns the result unchanged.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.DTOs;
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
    public async Task Handle_ForwardsOrderedUserIdsToService_AndReturnsResult()
    {
        var orderedUserIds = new List<string> { "user-c", "user-a", "user-b" };
        var expected = new HttpResultResource { Success = true, Messages = "Escalation roster order updated" };
        _rosterService.ReorderAsync(orderedUserIds, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.Handle(new ReorderEscalationRosterCommand(orderedUserIds), CancellationToken.None);

        await _rosterService.Received(1).ReorderAsync(orderedUserIds, Arg.Any<CancellationToken>());
        result.ShouldBe(expected);
    }
}
