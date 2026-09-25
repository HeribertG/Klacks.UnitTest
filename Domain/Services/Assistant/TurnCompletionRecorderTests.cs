// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the persistence tail of a finished streamed turn that LLMService.ProcessStreamAsync used to carry
/// inline: history, usage row, correction anchor and background tasks run in this order with the turn's
/// own values, and a storage failure is logged instead of thrown because the answer is already on screen.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;
using RepositoryLLMUsage = Klacks.Api.Domain.Models.Assistant.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnCompletionRecorderTests
{
    private const string UserId = "user-1";
    private const string ConversationKey = "conv-1";
    private const string ModelKey = "model-1";
    private const string UserMessage = "Create the employee";
    private const string Answer = "Done.";
    private const int ToolIterations = 2;
    private const long ElapsedMs = 1234;
    private const long TtftMs = 321;

    private ILLMRepository _repository = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private IAgentRepository _agentRepository = null!;
    private ILLMBackgroundTaskService _backgroundTasks = null!;
    private RecordingLogger<TurnCompletionRecorder> _logger = null!;
    private TurnCompletionRecorder _recorder = null!;
    private TurnRunState _turnState = null!;
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
            Substitute.For<IStoppedTurnCleanup>());

        _conversation = new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationKey, UserId = UserId };
        _model = new LLMModel { Id = Guid.NewGuid(), ModelId = ModelKey };
        _context = new LLMContext { Message = UserMessage, UserId = UserId, TurnId = Guid.NewGuid() };
    }

    [Test]
    public async Task RecordCompleted_RunsHistoryUsageAnchorAndBackgroundTasksInOrder()
    {
        var calls = new List<LLMFunctionCall> { new() { FunctionName = "create_employee" } };

        await _recorder.RecordCompletedAsync(Turn(calls), CancellationToken.None);

        Received.InOrder(() =>
        {
            _repository.SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m => m.Role == "user"));
            _repository.SaveMessageAsync(Arg.Is<RepositoryLLMMessage>(m => m.Role == "assistant"));
            _repository.TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
            _turnPreparation.RecordLastAction(
                _context, ConversationKey, Answer, calls, true);
            _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>());
            _backgroundTasks.RunBackgroundTasks(_agent, _conversation, _context, Answer, calls, true);
        });
    }

    [Test]
    public async Task RecordCompleted_WritesTheUsageRowWithTheTurnsOwnValues()
    {
        var calls = new List<LLMFunctionCall> { new() { FunctionName = "create_employee" } };

        await _recorder.RecordCompletedAsync(Turn(calls), CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u =>
            u.Id == _context.TurnId
            && u.UserId == UserId
            && u.ModelId == _model.Id
            && u.ConversationId == ConversationKey
            && u.InputTokens == 11
            && u.OutputTokens == 7
            && u.ResponseTimeMs == ElapsedMs
            && u.TtftMs == TtftMs
            && u.ToolIterations == ToolIterations
            && u.FunctionsCalled == "[\"create_employee\"]"
            && u.ToolChoiceRequested
            && u.ToolChoiceSupported
            && u.ToolCallReturned));
    }

    [Test]
    public async Task RecordCompleted_WithoutCalls_MarksNoToolCallReturned()
    {
        await _recorder.RecordCompletedAsync(Turn([]), CancellationToken.None);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<RepositoryLLMUsage>(u =>
            !u.ToolCallReturned && u.FunctionsCalled == "[]"));
    }

    [Test]
    public async Task RecordCompleted_PassesTheCancellationTokenToTheAgentLookup()
    {
        using var source = new CancellationTokenSource();

        await _recorder.RecordCompletedAsync(Turn([]), source.Token);

        await _agentRepository.Received(1).GetDefaultAgentAsync(source.Token);
    }

    [Test]
    public async Task RecordCompleted_WhenStorageThrows_LogsAndDoesNotThrowAndSkipsTheRest()
    {
        var failure = new InvalidOperationException("db down");
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>()).Returns<RepositoryLLMMessage>(_ => throw failure);

        await Should.NotThrowAsync(() => _recorder.RecordCompletedAsync(Turn([]), CancellationToken.None));

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, failure));
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
        _backgroundTasks.DidNotReceiveWithAnyArgs().RunBackgroundTasks(
            default, default!, default!, default!, default!, default);
    }

    [Test]
    public async Task RecordCompleted_ClaimsTheOutcomeBeforeAnythingIsWritten()
    {
        TurnOutcome? outcomeWhenTheHistoryWasWritten = null;
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>()).Returns(call =>
        {
            outcomeWhenTheHistoryWasWritten ??= _turnState.Outcome;
            return call.Arg<RepositoryLLMMessage>();
        });

        await _recorder.RecordCompletedAsync(Turn([]), CancellationToken.None);

        outcomeWhenTheHistoryWasWritten.ShouldBe(TurnOutcome.Completed);
    }

    [Test]
    public async Task RecordCompleted_WhenStorageThrows_TheOutcomeStaysCompleted()
    {
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns<RepositoryLLMMessage>(_ => throw new InvalidOperationException("db down"));

        await _recorder.RecordCompletedAsync(Turn([]), CancellationToken.None);

        _turnState.Outcome.ShouldBe(TurnOutcome.Completed);
    }

    [Test]
    public async Task RecordCompleted_WhenTheTurnAlreadyHasAnOutcome_WritesNothingAndKeepsIt()
    {
        _turnState.TrySetOutcome(TurnOutcome.Stopped);

        await _recorder.RecordCompletedAsync(Turn([]), CancellationToken.None);

        _turnState.Outcome.ShouldBe(TurnOutcome.Stopped);
        await _repository.DidNotReceive().SaveMessageAsync(Arg.Any<RepositoryLLMMessage>());
        await _repository.DidNotReceive().TrackUsageAsync(Arg.Any<RepositoryLLMUsage>());
    }

    private TurnCompletion Turn(List<LLMFunctionCall> calls) => new(
        _context,
        _conversation,
        _model,
        ProviderSupportsToolChoice: true,
        ResponseContent: Answer,
        Usage: new ProviderLLMUsage { InputTokens = 11, OutputTokens = 7 },
        ElapsedMs: ElapsedMs,
        TtftMs: TtftMs,
        ToolIterations: ToolIterations,
        FunctionCalls: calls,
        ToolChoiceRequested: true,
        RecipePausedOnAsk: true,
        AnsweredWithNotice: true);
}
