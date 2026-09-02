// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MarkProactiveMessageReadCommandHandler — verifies the ownership-checked
/// mark-read result is passed through so the controller can answer 204 or 404, and that marking a
/// message read never acknowledges it (package F1: reading is not acknowledging).
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class MarkProactiveMessageReadCommandHandlerTests
{
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private MarkProactiveMessageReadCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new MarkProactiveMessageReadCommandHandler(_dispatchRepository);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Handle_PassesOwnershipResultThrough(bool found)
    {
        var id = Guid.NewGuid();
        _dispatchRepository.MarkReadAsync(id, "user-a", Arg.Any<CancellationToken>()).Returns(found);

        var result = await _sut.Handle(new MarkProactiveMessageReadCommand { Id = id, UserId = "user-a" }, CancellationToken.None);

        Assert.That(result, Is.EqualTo(found));
        await _dispatchRepository.Received(1).MarkReadAsync(id, "user-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DoesNotAcknowledge_SoReadingNeverStopsTheReminderLoop()
    {
        // F1 negative case: acknowledgement is the ONLY stop truth of the reminder backoff, and reading
        // a message is not acknowledging it. A row the user merely scrolled past must keep its
        // AcknowledgedAtUtc and its NextReminderAtUtc, so the sweep reminds again on schedule.
        var id = Guid.NewGuid();
        _dispatchRepository.MarkReadAsync(id, "user-a", Arg.Any<CancellationToken>()).Returns(true);

        await _sut.Handle(new MarkProactiveMessageReadCommand { Id = id, UserId = "user-a" }, CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAllForKindAsync(default!, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryRescheduleReminderAsync(default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryAdvanceReminderAsync(default, default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }
}
