// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SetProactiveReactionCommandHandler — verifies that a reaction is stored on the
/// user's own dispatch row, that unknown ids or rows of other users are reported as not found,
/// that a stored dismissal invokes the dismiss-streak evaluator (helpful reactions do not), and
/// that an evaluator failure never fails the reaction request.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class SetProactiveReactionCommandHandlerTests
{
    private const string OwnerUserId = "user-a";
    private const string OtherUserId = "user-b";

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IDismissStreakEvaluator _dismissStreakEvaluator = null!;
    private SetProactiveReactionCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _dismissStreakEvaluator = Substitute.For<IDismissStreakEvaluator>();
        _sut = new SetProactiveReactionCommandHandler(
            _dispatchRepository,
            _dismissStreakEvaluator,
            NullLogger<SetProactiveReactionCommandHandler>.Instance);
    }

    private static ProactiveTriggerDispatchRow MakeRow(Guid id, string userId) =>
        new()
        {
            Id = id,
            UserId = userId,
            TriggerKind = "unstaffed_shift",
            DedupKey = "dedup-key"
        };

    [TestCase(ProactiveReaction.Helpful)]
    [TestCase(ProactiveReaction.Dismissed)]
    public async Task Handle_OwnRow_SetsReactionAndReturnsTrue(ProactiveReaction reaction)
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);
        var before = DateTime.UtcNow;

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = reaction
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(row.Reaction, Is.EqualTo(reaction));
        Assert.That(row.ReactionAtUtc, Is.Not.Null);
        Assert.That(row.ReactionAtUtc, Is.GreaterThanOrEqualTo(before));
        await _dispatchRepository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RowOfOtherUser_ReturnsFalseAndDoesNotUpdate()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OtherUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Helpful
        }, CancellationToken.None);

        Assert.That(result, Is.False);
        Assert.That(row.Reaction, Is.EqualTo(ProactiveReaction.None));
        Assert.That(row.ReactionAtUtc, Is.Null);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
        await _dismissStreakEvaluator.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default!, default);
    }

    [Test]
    public async Task Handle_UnknownId_ReturnsFalseAndDoesNotUpdate()
    {
        _dispatchRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ProactiveTriggerDispatchRow?)null);

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = Guid.NewGuid(),
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed
        }, CancellationToken.None);

        Assert.That(result, Is.False);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
        await _dismissStreakEvaluator.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default!, default);
    }

    [Test]
    public async Task Handle_OwnRowWithDifferentUserIdCasing_SetsReaction()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId.ToUpperInvariant());
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Helpful
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(row.Reaction, Is.EqualTo(ProactiveReaction.Helpful));
    }

    [Test]
    public async Task Handle_Dismissed_InvokesDismissStreakEvaluatorWithRowUserAndKind()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed
        }, CancellationToken.None);

        await _dismissStreakEvaluator.Received(1).EvaluateAsync(OwnerUserId, row.TriggerKind, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Helpful_DoesNotInvokeDismissStreakEvaluator()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Helpful
        }, CancellationToken.None);

        await _dismissStreakEvaluator.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default!, default);
    }

    [Test]
    public async Task Handle_EvaluatorThrows_ReactionIsStillStoredAndTrueReturned()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);
        _dismissStreakEvaluator
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("evaluation failed")));

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(row.Reaction, Is.EqualTo(ProactiveReaction.Dismissed));
        await _dispatchRepository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
    }
}
