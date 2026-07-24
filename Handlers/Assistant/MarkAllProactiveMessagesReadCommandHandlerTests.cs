// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MarkAllProactiveMessagesReadCommandHandler — verifies all unread messages of
/// the requesting user are marked read.
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
}
