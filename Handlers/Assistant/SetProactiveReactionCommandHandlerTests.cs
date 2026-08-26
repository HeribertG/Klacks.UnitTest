// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SetProactiveReactionCommandHandler — verifies that a reaction is stored on the
/// user's own dispatch row, that unknown ids or rows of other users are reported as not found,
/// that a stored dismissal invokes the dismiss-streak evaluator (helpful reactions do not), that
/// every stored reaction invokes the helpful-boost evaluator, and that an evaluator failure never
/// fails the reaction request.
///
/// The condition-ledger write-back is covered here as the secondary effect it is: a dismissal of a
/// message that reported a ledger finding rejects that finding with the given reason, a dismissal of
/// an unlinked message touches the ledger at all, and neither a refused nor a throwing ledger call may
/// cost the user the dismissal they actually asked for.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class SetProactiveReactionCommandHandlerTests
{
    private const string OwnerUserId = "user-a";
    private const string OtherUserId = "user-b";

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IDismissStreakEvaluator _dismissStreakEvaluator = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IHelpfulBoostEvaluator _helpfulBoostEvaluator = null!;
    private SetProactiveReactionCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _dismissStreakEvaluator = Substitute.For<IDismissStreakEvaluator>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _helpfulBoostEvaluator = Substitute.For<IHelpfulBoostEvaluator>();
        _ledgerService
            .TryRejectAsync(Arg.Any<Guid>(), Arg.Any<AgentConditionRejectReason>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new SetProactiveReactionCommandHandler(
            _dispatchRepository,
            _dismissStreakEvaluator,
            _ledgerService,
            _helpfulBoostEvaluator,
            NullLogger<SetProactiveReactionCommandHandler>.Instance);
    }

    private static ProactiveTriggerDispatchRow MakeRow(Guid id, string userId, Guid? conditionId = null) =>
        new()
        {
            Id = id,
            UserId = userId,
            TriggerKind = "unstaffed_shift",
            DedupKey = "dedup-key",
            ConditionId = conditionId
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

    [Test]
    public async Task Handle_DismissedWithReasonOnALinkedRow_RejectsTheConditionForTheDismissingUser()
    {
        var id = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var row = MakeRow(id, userId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = userId.ToString(),
            Reaction = ProactiveReaction.Dismissed,
            RejectReason = AgentConditionRejectReason.AlreadyHandled
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        await _ledgerService.Received(1).TryRejectAsync(
            conditionId, AgentConditionRejectReason.AlreadyHandled, userId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DismissedWithoutAReasonOnALinkedRow_RejectsTheConditionAsNoReason()
    {
        var id = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId, conditionId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed
        }, CancellationToken.None);

        await _ledgerService.Received(1).TryRejectAsync(
            conditionId, AgentConditionRejectReason.NoReason, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DismissedOnAnUnlinkedRow_StoresTheDismissalAndLeavesTheLedgerAlone()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed,
            RejectReason = AgentConditionRejectReason.GenerallyUnwanted
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(row.Reaction, Is.EqualTo(ProactiveReaction.Dismissed));
        await _dispatchRepository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
        await _ledgerService.DidNotReceiveWithAnyArgs().TryRejectAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_HelpfulOnALinkedRow_NeverRejectsTheCondition()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId, Guid.NewGuid());
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Helpful
        }, CancellationToken.None);

        await _ledgerService.DidNotReceiveWithAnyArgs().TryRejectAsync(default, default, default, default);
    }

    /// <summary>
    /// The realistic refusal: the finding is in a status the state machine grants no path to Rejected
    /// from, so TryRejectAsync reports false. The dismissal is the primary effect and must survive it.
    /// </summary>
    [Test]
    public async Task Handle_LedgerRefusesTheRejection_DismissalIsStillStoredAndTrueReturned()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId, Guid.NewGuid());
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);
        _ledgerService
            .TryRejectAsync(Arg.Any<Guid>(), Arg.Any<AgentConditionRejectReason>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed,
            RejectReason = AgentConditionRejectReason.WrongThisTime
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(row.Reaction, Is.EqualTo(ProactiveReaction.Dismissed));
        await _dispatchRepository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
    }

    [TestCase(ProactiveReaction.Helpful)]
    [TestCase(ProactiveReaction.Dismissed)]
    public async Task Handle_AnyReaction_InvokesHelpfulBoostEvaluatorWithRowUserAndKind(ProactiveReaction reaction)
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);

        await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = reaction
        }, CancellationToken.None);

        await _helpfulBoostEvaluator.Received(1).EvaluateAsync(OwnerUserId, row.TriggerKind, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_HelpfulBoostEvaluatorThrows_ReactionIsStillStoredAndTrueReturned()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId);
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);
        _helpfulBoostEvaluator
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("evaluation failed")));

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
    public async Task Handle_LedgerThrows_DismissalIsStillStoredAndTheStreakEvaluatorStillRuns()
    {
        var id = Guid.NewGuid();
        var row = MakeRow(id, OwnerUserId, Guid.NewGuid());
        _dispatchRepository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(row);
        _ledgerService
            .TryRejectAsync(Arg.Any<Guid>(), Arg.Any<AgentConditionRejectReason>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("ledger down"));

        var result = await _sut.Handle(new SetProactiveReactionCommand
        {
            Id = id,
            UserId = OwnerUserId,
            Reaction = ProactiveReaction.Dismissed,
            RejectReason = AgentConditionRejectReason.WrongThisTime
        }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(row.Reaction, Is.EqualTo(ProactiveReaction.Dismissed));
        await _dispatchRepository.Received(1).UpdateAsync(row, Arg.Any<CancellationToken>());
        await _dismissStreakEvaluator.Received(1).EvaluateAsync(OwnerUserId, row.TriggerKind, Arg.Any<CancellationToken>());
    }
}
