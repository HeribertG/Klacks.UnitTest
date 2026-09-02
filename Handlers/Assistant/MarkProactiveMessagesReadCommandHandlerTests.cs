// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MarkProactiveMessagesReadCommandHandler — verifies exactly the listed message
/// ids of the requesting user are handed to the repository, and that marking them read never
/// acknowledges them (package F1: reading is not acknowledging).
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class MarkProactiveMessagesReadCommandHandlerTests
{
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private MarkProactiveMessagesReadCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new MarkProactiveMessagesReadCommandHandler(_dispatchRepository);
    }

    [Test]
    public async Task Handle_MarksExactlyTheListedMessagesOfRequestingUser()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await _sut.Handle(
            new MarkProactiveMessagesReadCommand { UserId = "user-a", Ids = [first, second] },
            CancellationToken.None);

        await _dispatchRepository.Received(1).MarkManyReadAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2 && ids.Contains(first) && ids.Contains(second)),
            "user-a",
            Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceive().MarkAllReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DoesNotAcknowledge_SoReadingNeverStopsTheReminderLoop()
    {
        // F1 negative case: acknowledgement is the ONLY stop truth of the reminder backoff, and reading
        // a message is not acknowledging it. A row the user merely scrolled past must keep its
        // AcknowledgedAtUtc and its NextReminderAtUtc, so the sweep reminds again on schedule.
        await _sut.Handle(
            new MarkProactiveMessagesReadCommand { UserId = "user-a", Ids = [Guid.NewGuid()] },
            CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAllForKindAsync(default!, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryRescheduleReminderAsync(default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryAdvanceReminderAsync(default, default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }
}
