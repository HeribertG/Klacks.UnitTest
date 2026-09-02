// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MarkAllProactiveMessagesReadCommandHandler — verifies all unread messages of
/// the requesting user are marked read, and that marking them read never acknowledges them
/// (package F1: reading is not acknowledging).
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class MarkAllProactiveMessagesReadCommandHandlerTests
{
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private MarkAllProactiveMessagesReadCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new MarkAllProactiveMessagesReadCommandHandler(_dispatchRepository);
    }

    [Test]
    public async Task Handle_MarksAllMessagesOfRequestingUserAsRead()
    {
        await _sut.Handle(new MarkAllProactiveMessagesReadCommand { UserId = "user-a" }, CancellationToken.None);

        await _dispatchRepository.Received(1).MarkAllReadAsync("user-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DoesNotAcknowledge_SoReadingNeverStopsTheReminderLoop()
    {
        // F1 negative case: acknowledgement is the ONLY stop truth of the reminder backoff, and reading
        // a message is not acknowledging it. A row the user merely scrolled past must keep its
        // AcknowledgedAtUtc and its NextReminderAtUtc, so the sweep reminds again on schedule.
        await _sut.Handle(new MarkAllProactiveMessagesReadCommand { UserId = "user-a" }, CancellationToken.None);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAllForKindAsync(default!, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryRescheduleReminderAsync(default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryAdvanceReminderAsync(default, default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }
}
