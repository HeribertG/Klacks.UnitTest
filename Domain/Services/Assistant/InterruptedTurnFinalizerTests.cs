// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The safety net of a streamed turn. A turn that ended on its own is left alone (completed, clarified,
/// stopped, failed) - never stored a second time, never labelled interrupted. A turn that was left mid-way is
/// persisted exactly the way a stop is, whether or not a stop was ever requested; one that never got as far
/// as a conversation or a context is only cleaned up. A failure the caller reports is claimed as such before
/// anything else, so an unhandled exception is not recorded as "interrupted by the user". The net never throws.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;
using RepositoryLLMUsage = Klacks.Api.Domain.Models.Assistant.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class InterruptedTurnFinalizerTests
{
    private const string UserId = "3f6c1a52-58f1-4e0f-9d6a-1b2c3d4e5f60";
    private const string ConversationKey = "conv-interrupted";
    private const string ModelKey = "model-1";
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string Partial = "Sure, I am creating";
    private const string WriteSkill = "create_employee";

    private ILLMRepository _repository = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private ILLMBackgroundTaskService _backgroundTasks = null!;
    private IStoppedTurnCleanup _cleanup = null!;
    private TurnRunState _turnState = null!;
    private InterruptedTurnFinalizer _finalizer = null!;
    private LLMConversation _conversation = null!;
    private LLMModel _model = null!;
    private LLMContext _context = null!;
    private Guid _turnId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _turnPreparation = Substitute.For<ITurnPreparationService>();
        _backgroundTasks = Substitute.For<ILLMBackgroundTaskService>();
        _cleanup = Substitute.For<IStoppedTurnCleanup>();
        _turnState = new TurnRunState();
        var agents = Substitute.For<IAgentRepository>();
        agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent());

        var recorder = new TurnCompletionRecorder(
            Substitute.For<ILogger<TurnCompletionRecorder>>(),
            new LLMConversationManager(Substitute.For<ILogger<LLMConversationManager>>(), _repository),
            _turnPreparation,
            agents,
            _backgroundTasks,
            _turnState,
            _cleanup);
        _finalizer = new InterruptedTurnFinalizer(
            _turnState, recorder, _cleanup, Substitute.For<ILogger<InterruptedTurnFinalizer>>());

        _turnId = Guid.NewGuid();
        _conversation = new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationKey, UserId = UserId };
        _model = new LLMModel { Id = Guid.NewGuid(), ModelId = ModelKey };
        _context = new LLMContext { Message = UserMessage, UserId = UserId, TurnId = _turnId };
    }

    [Test]
    public async Task ATurnLeftMidStreamWithoutAnyStopRequest_IsPersistedLikeAStop()
    {
        MidTurn();

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        await _repository.Received(1).SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m =>
            m.Role == "assistant" && m.Content == Partial + "\n" + TurnInterruptionDefaults.InterruptedMarker));
        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u =>
            u.Id == _turnId && u.FunctionsCalled == "[\"create_employee\"]"));
        _turnPreparation.Received(1).RecordLastAction(
            _context, ConversationKey, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<LLMFunctionCall>>(calls => calls.Count == 1 && calls[0].FunctionName == WriteSkill),
            false);
        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Any<CancellationToken>());
        _backgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), _conversation, _context, Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>(),
            InterruptedTurnPhases.DuringText);
    }

    [Test]
    public async Task AStopThatNeverReachedItsOwnTail_IsPersistedTheSameWay()
    {
        using var stop = new CancellationTokenSource();
        _context.StopToken = stop.Token;
        MidTurn();
        stop.Cancel();

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
    }

    [Test]
    public async Task ATurnCutBetweenTheCorrectionPreparationAndTheChatService_IsOnlyCleanedUp()
    {
        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
    }

    [Test]
    public async Task ATurnCutWhileItsConversationWasStillBeingPrepared_IsOnlyCleanedUp()
    {
        _turnState.Begin(_context);

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
    }

    [TestCase(TurnOutcome.Completed)]
    [TestCase(TurnOutcome.Clarified)]
    [TestCase(TurnOutcome.Stopped)]
    [TestCase(TurnOutcome.Errored)]
    public async Task ATurnThatEndedOnItsOwn_IsLeftAloneAndKeepsItsOutcome(TurnOutcome outcome)
    {
        MidTurn();
        _turnState.TrySetOutcome(outcome);

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        _turnState.Outcome.ShouldBe(outcome);
        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
        _backgroundTasks.DidNotReceiveWithAnyArgs().RunStoppedTurnTasks(default, default!, default!, default!, default!, default!);
    }

    [Test]
    public async Task AFailureTheCallerReports_IsClaimedAsErroredAndNothingIsStoredOrCleanedUp()
    {
        MidTurn();

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: true);

        _turnState.Outcome.ShouldBe(TurnOutcome.Errored);
        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
    }

    [Test]
    public async Task AFailureTheCallerReports_DoesNotOverwriteAnOutcomeTheTurnAlreadyHad()
    {
        MidTurn();
        _turnState.TrySetOutcome(TurnOutcome.Completed);

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: true);

        _turnState.Outcome.ShouldBe(TurnOutcome.Completed);
    }

    [Test]
    public async Task CalledTwice_TheTurnIsStoredExactlyOnce()
    {
        MidTurn();

        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);
        await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AStorageFailure_NeverEscapesTheNet()
    {
        MidTurn();
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns<RepositoryLLMMessage>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false));

        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACleanupFailureOfATurnThatNeverBegan_NeverEscapesTheNet()
    {
        _cleanup.CleanUpAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cleanup broke"));

        await Should.NotThrowAsync(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false));
    }

    private void MidTurn()
    {
        _turnState.Begin(_context);
        _turnState.Attach(_conversation, _model, providerSupportsToolChoice: true);
        _turnState.RegisterCalls([new LLMFunctionCall { FunctionName = WriteSkill, Success = true, Result = "Done." }]);
        _turnState.StreamedContent.Append(Partial);
    }
}
