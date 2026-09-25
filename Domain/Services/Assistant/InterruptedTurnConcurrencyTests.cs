// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The persistence of a cut-off turn under real concurrency. The stop tail and the safety net (and two calls
/// of the net) can reach the same turn at the same time; exactly one may write it and exactly one may report
/// the stop to the client. "At the same time" is forced here, not hoped for: the first writer is held inside
/// the history write by a gate while the second one runs, so the interleaving does not depend on the
/// scheduler. Also pinned: a stop that arrives after the turn was completed changes nothing, a stop that
/// arrives while the net is writing does not cut its writes, and the net tells its caller when it, and only
/// it, persisted the stop.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;
using RepositoryLLMUsage = Klacks.Api.Domain.Models.Assistant.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class InterruptedTurnConcurrencyTests
{
    private const string UserId = "3f6c1a52-58f1-4e0f-9d6a-1b2c3d4e5f60";
    private const string ConversationKey = "conv-concurrency";
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string Partial = "Sure, I am creating";
    private const string WriteSkill = "create_employee";
    private const string WriteLabel = "Create employee";
    private const string Language = "en";
    private const int ConcurrentCallers = 8;
    private const int RaceRepetitions = 50;
    private const int MessagesPerTurn = 2;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private ILLMRepository _repository = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private ILLMBackgroundTaskService _backgroundTasks = null!;
    private IStoppedTurnCleanup _cleanup = null!;
    private TurnRunState _turnState = null!;
    private TurnCompletionRecorder _recorder = null!;
    private InterruptedTurnFinalizer _finalizer = null!;
    private LLMConversation _conversation = null!;
    private LLMModel _model = null!;
    private LLMContext _context = null!;
    private Guid _turnId;
    private int _saveCalls;

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

        _recorder = new TurnCompletionRecorder(
            Substitute.For<ILogger<TurnCompletionRecorder>>(),
            new LLMConversationManager(Substitute.For<ILogger<LLMConversationManager>>(), _repository),
            _turnPreparation,
            agents,
            _backgroundTasks,
            _turnState,
            _cleanup);
        _finalizer = new InterruptedTurnFinalizer(
            _turnState, _recorder, _cleanup, Substitute.For<ILogger<InterruptedTurnFinalizer>>());

        _turnId = Guid.NewGuid();
        _conversation = new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationKey, UserId = UserId };
        _model = new LLMModel { Id = Guid.NewGuid(), ModelId = "model-1" };
        _context = new LLMContext
        {
            Message = UserMessage,
            UserId = UserId,
            TurnId = _turnId,
            Language = Language,
            AvailableFunctions = new List<LLMFunction>
            {
                new() { Name = WriteSkill, Labels = new Dictionary<string, string> { [Language] = WriteLabel } }
            }
        };
        _saveCalls = 0;
    }

    [Test]
    public async Task TheFinalizerThatPersistsTheStop_ReturnsWhatTheWriteActionsThatRanAreCalled()
    {
        MidTurn();

        var summary = await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        summary.ShouldNotBeNull();
        summary.ExecutedCount.ShouldBe(1);
        summary.Labels.ShouldBe(new[] { WriteLabel });
    }

    [Test]
    public async Task ATurnThatNeverGotAContext_ReportsTheStopWithNothingExecuted()
    {
        var summary = await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        summary.ShouldBe(StoppedTurnSummary.Nothing);
    }

    [TestCase(TurnOutcome.Completed)]
    [TestCase(TurnOutcome.Clarified)]
    [TestCase(TurnOutcome.Stopped)]
    [TestCase(TurnOutcome.Errored)]
    public async Task ATurnThatEndedOnItsOwn_IsNotReportedAsStoppedByTheFinalizer(TurnOutcome outcome)
    {
        MidTurn();
        _turnState.TrySetOutcome(outcome);

        (await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false)).ShouldBeNull();
    }

    [Test]
    public async Task AnErrorTheCallerReports_IsNeverReportedAsStopped()
    {
        MidTurn();

        (await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: true)).ShouldBeNull();
    }

    [Test]
    public async Task AFailureInsideThePersistence_StillReportsTheStopTheNetClaimed()
    {
        MidTurn();
        _cleanup.CleanUpAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cleanup broke"));

        var summary = await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);

        summary.ShouldNotBeNull();
        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
    }

    [Test]
    public async Task TwoFinalizersMeetingWhileTheFirstIsStillWriting_LetOnlyTheFirstWriteAndReport()
    {
        MidTurn();
        var gate = HoldTheFirstHistoryWrite();

        var first = Task.Run(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false));
        await gate.FirstWriteEntered.Task.WaitAsync(Patience);

        var second = await Task.Run(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false))
            .WaitAsync(Patience);

        second.ShouldBeNull();
        first.IsCompleted.ShouldBeFalse();

        gate.Release();
        (await first.WaitAsync(Patience)).ShouldNotBeNull();
        _saveCalls.ShouldBe(MessagesPerTurn);
        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Any<CancellationToken>());
        _turnPreparation.ReceivedWithAnyArgs(1).RecordLastAction(default!, default!, default!, default!, default);
    }

    [Test]
    public async Task TheRecorder_ReturnsTheSummaryToTheCallerThatClaimedTheStopAndNullToTheOthers()
    {
        MidTurn();

        var first = await _recorder.TryRecordStoppedAsync(CancellationToken.None);
        var second = await _recorder.TryRecordStoppedAsync(CancellationToken.None);
        var forTheTail = await _recorder.RecordStoppedAsync(CancellationToken.None);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
        forTheTail.ExecutedCount.ShouldBe(first.ExecutedCount);
        forTheTail.Labels.ShouldBe(first.Labels);
        _saveCalls.ShouldBe(MessagesPerTurn);
    }

    [Test]
    public async Task ManyFinalizersReleasedTogether_ExactlyOneWritesAndExactlyOneReports()
    {
        for (var repetition = 0; repetition < RaceRepetitions; repetition++)
        {
            SetUp();
            MidTurn();
            using var start = new ManualResetEventSlim(false);

            var callers = Enumerable.Range(0, ConcurrentCallers)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    return _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false);
                }))
                .ToList();
            start.Set();
            var results = await Task.WhenAll(callers).WaitAsync(Patience);

            results.Count(summary => summary != null).ShouldBe(1, $"repetition {repetition}");
            _saveCalls.ShouldBe(MessagesPerTurn, $"repetition {repetition}");
            await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        }
    }

    [Test]
    public async Task TheStopTailAndTheFinalizerAtTheSameTime_WriteTheTurnOnce_AndTheFinalizerDoesNotReportIt()
    {
        MidTurn();
        var gate = HoldTheFirstHistoryWrite();

        var tail = Task.Run(async () => await StoppedTurnTail.StreamAsync(_recorder, _turnState).ToListAsync());
        await gate.FirstWriteEntered.Task.WaitAsync(Patience);

        var fromTheNet = await Task.Run(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false))
            .WaitAsync(Patience);

        fromTheNet.ShouldBeNull();
        gate.Release();
        var chunks = await tail.WaitAsync(Patience);
        chunks.Select(chunk => chunk.Type).ShouldBe(new[] { SseChunkType.TurnStopped, SseChunkType.Done });
        _saveCalls.ShouldBe(MessagesPerTurn);
        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
    }

    [Test]
    public async Task TheFinalizerHoldingTheStop_LeavesTheStopTailNothingToWrite_ButItStillAnswersTheClient()
    {
        MidTurn();
        var gate = HoldTheFirstHistoryWrite();

        var net = Task.Run(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false));
        await gate.FirstWriteEntered.Task.WaitAsync(Patience);

        var chunks = await Task.Run(async () => await StoppedTurnTail.StreamAsync(_recorder, _turnState).ToListAsync())
            .WaitAsync(Patience);

        chunks.Select(chunk => chunk.Type).ShouldBe(new[] { SseChunkType.TurnStopped, SseChunkType.Done });
        gate.Release();
        (await net.WaitAsync(Patience)).ShouldNotBeNull();
        _saveCalls.ShouldBe(MessagesPerTurn);
        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
    }

    [Test]
    public async Task ManyErrorReportsReleasedTogether_TheErroredTurnIsWrittenOnce()
    {
        for (var repetition = 0; repetition < RaceRepetitions; repetition++)
        {
            SetUp();
            MidTurn();
            using var start = new ManualResetEventSlim(false);

            var callers = Enumerable.Range(0, ConcurrentCallers)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    return _finalizer.FinalizeAsync(UserId, _turnId, endedInError: true);
                }))
                .ToList();
            start.Set();
            var results = await Task.WhenAll(callers).WaitAsync(Patience);

            results.ShouldAllBe(summary => summary == null);
            _saveCalls.ShouldBe(MessagesPerTurn, $"repetition {repetition}");
            await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(usage => usage.HasError));
        }
    }

    [Test]
    public async Task AStopThatArrivesAfterTheTurnWasCompleted_ChangesNothing()
    {
        using var stop = new CancellationTokenSource();
        _context.StopToken = stop.Token;
        MidTurn();
        _turnState.TrySetOutcome(TurnOutcome.Completed);
        stop.Cancel();

        (await _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false)).ShouldBeNull();

        _turnState.Outcome.ShouldBe(TurnOutcome.Completed);
        _saveCalls.ShouldBe(0);
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
        _backgroundTasks.DidNotReceiveWithAnyArgs().RunStoppedTurnTasks(default, default!, default!, default!, default!, default!);
    }

    [Test]
    public async Task AStopThatArrivesWhileTheFinalizerIsWriting_DoesNotCutItsWrites()
    {
        using var stop = new CancellationTokenSource();
        _context.StopToken = stop.Token;
        MidTurn();
        var gate = HoldTheFirstHistoryWrite();

        var net = Task.Run(() => _finalizer.FinalizeAsync(UserId, _turnId, endedInError: false));
        await gate.FirstWriteEntered.Task.WaitAsync(Patience);
        stop.Cancel();
        gate.Release();

        (await net.WaitAsync(Patience)).ShouldNotBeNull();
        _saveCalls.ShouldBe(MessagesPerTurn);
        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        _turnPreparation.ReceivedWithAnyArgs(1).RecordLastAction(default!, default!, default!, default!, default);
        await _cleanup.Received(1).CleanUpAsync(UserId, _turnId, Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
        _backgroundTasks.Received(1).RunStoppedTurnTasks(
            Arg.Any<Agent?>(), _conversation, _context, Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>(),
            Arg.Any<string>());
    }

    private HistoryGate HoldTheFirstHistoryWrite()
    {
        var gate = new HistoryGate();
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns(async call =>
            {
                var index = Interlocked.Increment(ref _saveCalls);
                if (index == 1)
                {
                    gate.FirstWriteEntered.TrySetResult();
                    await gate.Released.Task;
                }

                return call.Arg<RepositoryLLMMessage>();
            });
        return gate;
    }

    private void MidTurn()
    {
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns(call =>
            {
                Interlocked.Increment(ref _saveCalls);
                return Task.FromResult(call.Arg<RepositoryLLMMessage>());
            });
        _turnState.Begin(_context);
        _turnState.Attach(_conversation, _model, providerSupportsToolChoice: true);
        _turnState.RegisterCalls([new LLMFunctionCall { FunctionName = WriteSkill, Success = true, Result = "Done." }]);
        _turnState.StreamedContent.Append(Partial);
    }

    private sealed class HistoryGate
    {
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => Released.TrySetResult();
    }
}
