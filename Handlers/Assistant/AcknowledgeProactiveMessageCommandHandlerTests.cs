// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AcknowledgeProactiveMessageCommandHandler — verifies the ownership-checked
/// acknowledgement result is passed through so the controller can answer 204 or 404, and that the
/// repository's stop-truth write (AcknowledgedAtUtc, NextReminderAtUtc cleared) is the one invoked.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class AcknowledgeProactiveMessageCommandHandlerTests
{
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private AcknowledgeProactiveMessageCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new AcknowledgeProactiveMessageCommandHandler(_dispatchRepository);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Handle_PassesOwnershipResultThrough(bool found)
    {
        var id = Guid.NewGuid();
        _dispatchRepository.AcknowledgeAsync(id, "user-a", Arg.Any<CancellationToken>()).Returns(found);

        var result = await _sut.Handle(new AcknowledgeProactiveMessageCommand { Id = id, UserId = "user-a" }, CancellationToken.None);

        Assert.That(result, Is.EqualTo(found));
        await _dispatchRepository.Received(1).AcknowledgeAsync(id, "user-a", Arg.Any<CancellationToken>());
    }
}
