// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the persistence of a turn the user stopped or whose connection dropped, read from the turn's run
/// state: the outcome is claimed first, then the history with the partial answer and the interruption marker,
/// the usage row and the correction anchor (both from the calls the server really ran), the cleanup of the
/// confirmations and UiAction rows, and the background tasks that stay allowed. A failure of one part never
/// stops the next, and a turn that already has an outcome is never written a second time.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;
using RepositoryLLMUsage = Klacks.Api.Domain.Models.Assistant.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnCompletionRecorderStoppedTests
{
    private const string UserId = "3f6c1a52-58f1-4e0f-9d6a-1b2c3d4e5f60";
    private const string ConversationKey = "conv-stopped";
    private const string ModelKey = "model-1";
    private const string UserMessage = "Create the employee Anna Meier.";
    private const string Partial = "Sure, I am creating";
    private const string WriteSkill = "create_employee";
    private const string ReadSkill = "search_employees";
    private const int TurnGapMs = 15;
    private const int StreamedChars = 400;
    private const int ReportedOutputTokens = 37;

    private ILLMRepository _repository = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private IAgentRepository _agentRepository = null!;
    private ILLMBackgroundTaskService _backgroundTasks = null!;
    private IStoppedTurnCleanup _cleanup = null!;
    private RecordingLogger<TurnCompletionRecorder> _logger = null!;
    private TurnRunState _turnState = null!;
    private TurnCompletionRecorder _recorder = null!;
    private LLMConversation _conversation = null!;
    private LLMModel _model = null!;
    private LLMContext _context = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _turnPreparation = Substitute.For<ITurnPreparationService>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _backgroundTasks = Substitute.For<ILLMBackgroundTaskService>();
        _cleanup = Substitute.For<IStoppedTurnCleanup>();
        _logger = new RecordingLogger<TurnCompletionRecorder>();
        _turnState = new TurnRunState();
        _agent = new Agent();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);

        _recorder = new TurnCompletionRecorder(
            _logger,
            new LLMConversationManager(Substitute.For<ILogger<LLMConversationManager>>(), _repository),
            _turnPreparation,
            _agentRepository,
            _backgroundTasks,
            _turnState,
            _cleanup);

        _conversation = new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationKey, UserId = UserId };
        _model = new LLMModel { Id = Guid.NewGuid(), ModelId = ModelKey };
        _context = new LLMContext { Message = UserMessage, UserId = UserId, TurnId = Guid.NewGuid() };
    }

    [Test]
    public async Task AStoppedTurn_RunsHistoryUsageAnchorCleanupAndBackgroundTasksInOrder()
    {
        var write = BeginTurnWithARunWrite();

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            _repository.SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m => m.Role == "user"));
            _repository.SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m => m.Role == "assistant"));
            _repository.TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
            _turnPreparation.RecordLastAction(
                _context, ConversationKey, Arg.Any<string>(), Arg.Any<IReadOnlyList<LLMFunctionCall>>(), false);
            _cleanup.CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>());
            _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>());
            _backgroundTasks.RunStoppedTurnTasks(
                _agent, _conversation, _context, Arg.Any<string>(),
                Arg.Is<List<LLMFunctionCall>>(calls => calls.SequenceEqual(new[] { write })),
                InterruptedTurnPhases.DuringText);
        });
    }

    [Test]
    public async Task TheStoredAnswer_IsThePartialTextFollowedByTheInterruptionMarker()
    {
        BeginTurnWithARunWrite();

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m =>
            m.Role == "assistant" && m.Content == Partial + "\n" + TurnInterruptionDefaults.InterruptedMarker));
    }

    [Test]
    public async Task ATurnStoppedBeforeAnyText_StoresTheMarkerAlone()
    {
        BeginTurn();

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m =>
            m.Role == "assistant" && m.Content == TurnInterruptionDefaults.InterruptedMarker));
    }

    [Test]
    public async Task TheUsageRow_NamesOnlyTheCallsTheServerRan()
    {
        BeginTurn();
        var ran = Ran(WriteSkill);
        var skipped = Ran(ReadSkill);
        skipped.SkippedByStop = true;
        var neverRan = new LLMFunctionCall { FunctionName = "list_groups" };
        _turnState.RegisterCalls([ran, skipped, neverRan]);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u =>
            u.Id == _context.TurnId
            && u.UserId == UserId
            && u.FunctionsCalled == "[\"create_employee\"]"
            && u.ToolCallReturned
            && !u.HasError));
    }

    [Test]
    public async Task TheCorrectionAnchor_IsRecordedForTheCallsTheServerRanOnly()
    {
        BeginTurn();
        var ran = Ran(WriteSkill);
        var skipped = Ran(ReadSkill);
        skipped.SkippedByStop = true;
        var uiAction = Ran("open_settings");
        uiAction.UiActionSteps = "[]";
        _turnState.RegisterCalls([ran, skipped, uiAction]);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        _turnPreparation.Received(1).RecordLastAction(
            _context, ConversationKey, Arg.Any<string>(),
            Arg.Is<IReadOnlyList<LLMFunctionCall>>(calls => calls.SequenceEqual(new[] { ran })), false);
    }

    [Test]
    public async Task ARecipeThatWaitsOnAnAsk_IsPassedToTheAnchorSoItSupersedesInsteadOfSaving()
    {
        BeginTurn();
        _context.RecipePausedOnAsk = true;

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        _turnPreparation.Received(1).RecordLastAction(
            _context, ConversationKey, Arg.Any<string>(), Arg.Any<IReadOnlyList<LLMFunctionCall>>(), true);
    }

    [Test]
    public async Task TheOutcome_IsClaimedBeforeAnythingIsWritten()
    {
        BeginTurn();
        TurnOutcome? outcomeWhenTheHistoryWasWritten = null;
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>()).Returns(call =>
        {
            outcomeWhenTheHistoryWasWritten ??= _turnState.Outcome;
            return call.Arg<RepositoryLLMMessage>();
        });

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        outcomeWhenTheHistoryWasWritten.ShouldBe(TurnOutcome.Stopped);
    }

    [Test]
    public async Task ATurnThatAlreadyHasAnOutcome_IsNotWrittenAgainAndNothingIsCleanedUp()
    {
        BeginTurn();
        _turnState.TrySetOutcome(TurnOutcome.Completed);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        _turnState.Outcome.ShouldBe(TurnOutcome.Completed);
        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
        _backgroundTasks.DidNotReceiveWithAnyArgs().RunStoppedTurnTasks(default, default!, default!, default!, default!, default!);
    }

    [Test]
    public async Task CalledTwice_TheTurnIsWrittenExactlyOnce()
    {
        BeginTurn();

        await _recorder.RecordStoppedAsync(CancellationToken.None);
        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        await _cleanup.Received(1).CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AStorageFailure_IsLoggedAndStillLeavesTheCleanupAndTheBackgroundTasks()
    {
        BeginTurn();
        var failure = new InvalidOperationException("db down");
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>()).Returns<RepositoryLLMMessage>(_ => throw failure);

        await Should.NotThrowAsync(() => _recorder.RecordStoppedAsync(CancellationToken.None));

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        await _cleanup.Received(1).CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>());
        _backgroundTasks.Received(1).RunStoppedTurnTasks(
            _agent, _conversation, _context, Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>(), Arg.Any<string>());
    }

    [Test]
    public async Task AFailingHistoryWrite_DoesNotKeepTheUsageRowAndTheAnchorFromBeingWritten()
    {
        BeginTurn();
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns<RepositoryLLMMessage>(_ => throw new InvalidOperationException("db down"));

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        _turnPreparation.Received(1).RecordLastAction(
            _context, ConversationKey, Arg.Any<string>(), Arg.Any<IReadOnlyList<LLMFunctionCall>>(), false);
    }

    [Test]
    public async Task AFailingUsageWrite_IsLoggedAndDoesNotKeepTheAnchorFromBeingWritten()
    {
        BeginTurn();
        var failure = new InvalidOperationException("usage down");
        _repository.TrackUsageAsync(Arg.Any<RepositoryLLMUsage>()).Returns<RepositoryLLMUsage>(_ => throw failure);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
        _turnPreparation.Received(1).RecordLastAction(
            _context, ConversationKey, Arg.Any<string>(), Arg.Any<IReadOnlyList<LLMFunctionCall>>(), false);
    }

    [Test]
    public async Task AFailingAnchorWrite_IsLoggedAndStillLeavesTheCleanupAndTheBackgroundTasks()
    {
        BeginTurn();
        var failure = new InvalidOperationException("anchor down");
        _turnPreparation
            .When(preparation => preparation.RecordLastAction(
                Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<LLMFunctionCall>>(), Arg.Any<bool>()))
            .Do(_ => throw failure);

        await Should.NotThrowAsync(() => _recorder.RecordStoppedAsync(CancellationToken.None));

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
        await _cleanup.Received(1).CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFailingCleanup_IsLoggedAndStillLeavesTheBackgroundTasks()
    {
        BeginTurnWithARunWrite();
        var failure = new InvalidOperationException("tracker down");
        _cleanup.CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));

        await Should.NotThrowAsync(() => _recorder.RecordStoppedAsync(CancellationToken.None));

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
        _backgroundTasks.Received(1).RunStoppedTurnTasks(
            _agent, _conversation, _context, Arg.Any<string>(), Arg.Any<List<LLMFunctionCall>>(), Arg.Any<string>());
    }

    [Test]
    public async Task AFailingCleanup_OfATurnWithoutAConversation_IsLoggedAndNotThrown()
    {
        _turnState.Begin(_context);
        var failure = new InvalidOperationException("tracker down");
        _cleanup.CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));

        await Should.NotThrowAsync(() => _recorder.RecordStoppedAsync(CancellationToken.None));

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
    }

    [Test]
    public async Task AStorageFailureOfAStoppedTurn_IsLoggedAsStoppedAndOfAnErroredTurnAsErrored()
    {
        BeginTurnWithARunWrite();
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns<RepositoryLLMMessage>(_ => throw new InvalidOperationException("db down"));

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        var stoppedEntry = _logger.Entries.Single(e => e.Level == LogLevel.Error && e.Message.Contains("history"));
        stoppedEntry.Message.ShouldContain("stopped");
        stoppedEntry.Message.ShouldNotContain("errored");

        var erroredLogger = new RecordingLogger<TurnCompletionRecorder>();
        var erroredState = new TurnRunState();
        var erroredContext = new LLMContext { Message = UserMessage, UserId = UserId, TurnId = Guid.NewGuid() };
        erroredState.Begin(erroredContext);
        erroredState.Attach(_conversation, _model, providerSupportsToolChoice: true);
        erroredState.RegisterCalls([Ran(WriteSkill)]);
        erroredState.TrySetOutcome(TurnOutcome.Errored);
        var erroredRecorder = new TurnCompletionRecorder(
            erroredLogger,
            new LLMConversationManager(Substitute.For<ILogger<LLMConversationManager>>(), _repository),
            _turnPreparation, _agentRepository, _backgroundTasks, erroredState, _cleanup);

        await erroredRecorder.RecordErroredAsync(CancellationToken.None);

        var erroredEntry = erroredLogger.Entries.Single(e => e.Level == LogLevel.Error && e.Message.Contains("history"));
        erroredEntry.Message.ShouldContain("errored");
        erroredEntry.Message.ShouldNotContain("stopped");
    }

    [Test]
    public async Task ANewerAnchorInTheConversation_IsLeftAloneWhileHistoryAndUsageAreStillWritten()
    {
        BeginTurn();
        _turnPreparation.HasLastActionSince(_context, ConversationKey, _turnState.StartedAtUtc).Returns(true);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        _turnPreparation.DidNotReceiveWithAnyArgs().RecordLastAction(default!, default!, default!, default!, default);
        await _repository.Received(2).SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.Received(1).TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
    }

    [Test]
    public async Task TheHistoryRows_AreStampedWithTheTimeTheTurnBegan()
    {
        BeginTurn();
        await Task.Delay(TurnGapMs);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m =>
            m.Role == "user" && m.CreateTime == _turnState.StartedAtUtc));
    }

    [Test]
    public async Task ATurnThatNeverGotAConversation_IsOnlyCleanedUp()
    {
        _turnState.Begin(_context);

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        _backgroundTasks.DidNotReceiveWithAnyArgs().RunStoppedTurnTasks(default, default!, default!, default!, default!, default!);
        await _cleanup.Received(1).CleanUpAsync(UserId, _context.TurnId!.Value, Arg.Any<CancellationToken>());
        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
    }

    [Test]
    public async Task ATurnThatNeverBegan_WritesNothingAndReportsNothing()
    {
        var summary = await _recorder.RecordStoppedAsync(CancellationToken.None);

        summary.ExecutedCount.ShouldBe(0);
        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _cleanup.DidNotReceiveWithAnyArgs().CleanUpAsync(default!, default, default);
    }

    [Test]
    public async Task TheSummary_ListsTheWriteActionsThatRan()
    {
        BeginTurn();
        _turnState.RegisterCalls([Ran(WriteSkill), Ran(ReadSkill)]);

        var summary = await _recorder.RecordStoppedAsync(CancellationToken.None);

        summary.ExecutedCount.ShouldBe(1);
    }

    [Test]
    public async Task ATurnStoppedMidTextWithoutProviderUsage_RecordsTheOutputTokensEstimatedFromTheStreamedText()
    {
        BeginTurn();
        _turnState.StreamedContent.Append(new string('a', StreamedChars));

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u =>
            u.OutputTokens == StreamedChars / LLMService.CharsPerToken
            && u.InputTokens == 0
            && u.Cost == 0m));
    }

    [Test]
    public async Task ATurnStoppedWithProviderUsage_KeepsTheReportedOutputTokens()
    {
        BeginTurn();
        _turnState.StreamedContent.Append(new string('a', StreamedChars));
        _turnState.Usage.OutputTokens = ReportedOutputTokens;

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u => u.OutputTokens == ReportedOutputTokens));
    }

    [Test]
    public async Task ATurnStoppedBeforeAnyText_RecordsNoOutputTokens()
    {
        BeginTurn();

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u => u.OutputTokens == 0));
    }

    [Test]
    public async Task TheEstimate_LeavesTheTurnsOwnUsageUntouched()
    {
        BeginTurn();
        _turnState.StreamedContent.Append(new string('a', StreamedChars));

        await _recorder.RecordStoppedAsync(CancellationToken.None);

        _turnState.Usage.OutputTokens.ShouldBe(0);
    }

    private LLMFunctionCall BeginTurnWithARunWrite()
    {
        BeginTurn();
        var write = Ran(WriteSkill);
        _turnState.RegisterCalls([write]);
        _turnState.StreamedContent.Append(Partial);
        return write;
    }

    private void BeginTurn()
    {
        _turnState.Begin(_context);
        _turnState.Attach(_conversation, _model, providerSupportsToolChoice: true);
    }

    private static LLMFunctionCall Ran(string skill) => new()
    {
        FunctionName = skill,
        Success = true,
        Result = "Done."
    };
}
