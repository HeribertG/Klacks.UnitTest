// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MuteTriggerKindCommandHandler (F1, "muting is an acknowledgement") — verifies that
/// muting writes the preference AND acknowledges the user's open rows of exactly that kind, that
/// unmuting writes the preference only and never touches a dispatch row, that the preference is
/// written before the acknowledgement, and that a failing acknowledgement fails the whole command
/// instead of leaving a muted kind whose rows keep their reminder schedule.
/// The scoping to one user and one kind is the repository's job and is asserted through the exact
/// arguments the handler passes, since the substitute cannot scope rows itself.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class MuteTriggerKindCommandHandlerTests
{
    private const string UserId = "user-a";

    private IAgentTriggerPreferenceService _preferenceService = null!;
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private MuteTriggerKindCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new MuteTriggerKindCommandHandler(_preferenceService, _dispatchRepository);
    }

    [Test]
    public async Task Handle_Mute_AcknowledgesTheOpenRowsOfThatUserAndKind()
    {
        _dispatchRepository
            .AcknowledgeAllForKindAsync(UserId, AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>())
            .Returns(2);

        var acknowledged = await _sut.Handle(Command(muted: true), CancellationToken.None);

        Assert.That(acknowledged, Is.EqualTo(2));
        await _preferenceService.Received(1).MuteAsync(UserId, AgentTriggerKinds.UnstaffedShift, true);
        await _dispatchRepository.Received(1)
            .AcknowledgeAllForKindAsync(UserId, AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Mute_DoesNotAcknowledgeAnotherKindOrAnotherUser()
    {
        await _sut.Handle(Command(muted: true), CancellationToken.None);

        await _dispatchRepository.DidNotReceive()
            .AcknowledgeAllForKindAsync(UserId, AgentTriggerKinds.TargetHoursDrift, Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceive()
            .AcknowledgeAllForKindAsync("user-b", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1)
            .AcknowledgeAllForKindAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Unmute_WritesThePreferenceButAcknowledgesNothing()
    {
        var acknowledged = await _sut.Handle(Command(muted: false), CancellationToken.None);

        Assert.That(acknowledged, Is.EqualTo(0));
        await _preferenceService.Received(1).MuteAsync(UserId, AgentTriggerKinds.UnstaffedShift, false);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAllForKindAsync(default!, default!);
    }

    [Test]
    public async Task Handle_Mute_WritesThePreferenceBeforeAcknowledging()
    {
        await _sut.Handle(Command(muted: true), CancellationToken.None);

        Received.InOrder(() =>
        {
            _preferenceService.MuteAsync(UserId, AgentTriggerKinds.UnstaffedShift, true);
            _dispatchRepository.AcknowledgeAllForKindAsync(UserId, AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public void Handle_FailingAcknowledgement_FailsTheWholeMute()
    {
        _dispatchRepository
            .AcknowledgeAllForKindAsync(UserId, AgentTriggerKinds.UnstaffedShift, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("database unavailable"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.Handle(Command(muted: true), CancellationToken.None));
    }

    private static MuteTriggerKindCommand Command(bool muted) => new()
    {
        UserId = UserId,
        TriggerKind = AgentTriggerKinds.UnstaffedShift,
        Muted = muted
    };
}
