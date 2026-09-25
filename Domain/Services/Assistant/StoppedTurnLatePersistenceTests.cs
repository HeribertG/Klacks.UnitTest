// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A turn that is persisted late (the user stopped it, or its connection dropped, while a newer turn of the
/// same conversation had already begun or finished) must not overtake that newer turn: its history rows carry
/// the time the turn began, so the conversation still reads in the order the user typed, and the correction
/// anchor of the newer turn is neither overwritten nor superseded. The scenario tests run the real recorder,
/// conversation manager and turn preparation against an in-memory history and anchor store.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class StoppedTurnLatePersistenceTests
{
    private const string ConversationKey = "conv-late";
    private const string WriteSkill = "create_employee";
    private const string OldRequest = "Create the employee Anna Meier.";
    private const string NewRequest = "No, create the employee Berta Frei.";
    private const string OldAnswer = "Sure, I am creating";
    private const string NewAnswer = "Berta Frei was created.";
    private const string EarlierRequest = "before";
    private const int TurnGapMs = 15;
    private const int EarlierMinutes = -1;

    private static readonly Guid UserId = Guid.NewGuid();

    private List<RepositoryLLMMessage> _messages = null!;
    private InMemoryLastActionStore _store = null!;
    private ILLMRepository _repository = null!;
    private TurnPreparationService _preparation = null!;
    private LLMConversationManager _conversationManager = null!;
    private LLMConversation _conversation = null!;
    private LLMModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _messages = [];
        _store = new InMemoryLastActionStore();
        _repository = Substitute.For<ILLMRepository>();
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>()).Returns(call =>
        {
            var message = call.Arg<RepositoryLLMMessage>();
            _messages.Add(message);
            return message;
        });

        _preparation = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            null!,
            Substitute.For<IRecipeRunRecorder>(),
            null!,
            _store,
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            NullLogger<TurnPreparationService>.Instance);
        _conversationManager = new LLMConversationManager(NullLogger<LLMConversationManager>.Instance, _repository);
        _conversation = new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationKey, UserId = UserId.ToString() };
        _model = new LLMModel { Id = Guid.NewGuid(), ModelId = "model-1" };
    }

    [Test]
    public async Task ANewerTurnThatFinishedFirst_StaysTheLatestInTheHistoryAndKeepsItsAnchor()
    {
        var older = NewTurn(OldRequest, out var olderRecorder);
        await Task.Delay(TurnGapMs);
        var newer = NewTurn(NewRequest, out var newerRecorder);
        newer.RegisterCalls([Ran(WriteSkill)]);
        await Task.Delay(TurnGapMs);
        await newerRecorder.RecordCompletedAsync(Completion(newer, NewAnswer), CancellationToken.None);

        await Task.Delay(TurnGapMs);
        older.StreamedContent.Append(OldAnswer);
        older.RegisterCalls([Ran(WriteSkill)]);
        await olderRecorder.RecordStoppedAsync(CancellationToken.None);

        _messages.OrderBy(m => m.CreateTime).Select(m => m.Content).ToList().ShouldBe(
        [
            OldRequest,
            OldAnswer + "\n" + TurnInterruptionDefaults.InterruptedMarker,
            NewRequest,
            NewAnswer
        ]);
        var anchor = _store.Peek(UserId, ConversationKey);
        anchor.ShouldNotBeNull();
        anchor!.UserMessage.ShouldBe(NewRequest);
        anchor.SupersededAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task ANewerTurnThatSupersededTheAnchor_IsNotOverwrittenByTheStoppedTurn()
    {
        var older = NewTurn(OldRequest, out var olderRecorder);
        _store.Save(new AssistantLastAction
        {
            UserId = UserId,
            ConversationId = ConversationKey,
            UserMessage = EarlierRequest,
            CreateTimeUtc = DateTime.UtcNow.AddMinutes(EarlierMinutes)
        });
        await Task.Delay(TurnGapMs);
        var newer = NewTurn(NewRequest, out var newerRecorder);
        await Task.Delay(TurnGapMs);
        await newerRecorder.RecordCompletedAsync(Completion(newer, NewAnswer), CancellationToken.None);

        await Task.Delay(TurnGapMs);
        older.RegisterCalls([Ran(WriteSkill)]);
        await olderRecorder.RecordStoppedAsync(CancellationToken.None);

        var anchor = _store.Peek(UserId, ConversationKey);
        anchor.ShouldNotBeNull();
        anchor!.UserMessage.ShouldBe(EarlierRequest);
        anchor.SupersededAtUtc.ShouldNotBeNull();
    }

    // Follow-up 4 (inverted anchor race), open and left to the owner: HasLastActionSince compares the WRITE
    // time of the anchor with the START of the persisting turn. The older stopped turn writes its anchor (write
    // time = now) while the newer stopped turn is still running; the newer one then finds an anchor that is
    // "newer than its start" and keeps it, so the anchor names the OLDER turn's action. The desired outcome is
    // the newer request; this test pins today's outcome and must flip when the owner decides on a fix.
    [Test]
    public async Task KnownLimit_TwoStoppedTurnsThatPersistInTheirStartOrder_LeaveTheAnchorOfTheOlderOne()
    {
        var older = NewTurn(OldRequest, out var olderRecorder);
        older.RegisterCalls([Ran(WriteSkill)]);
        await Task.Delay(TurnGapMs);
        var newer = NewTurn(NewRequest, out var newerRecorder);
        newer.RegisterCalls([Ran(WriteSkill)]);

        await Task.Delay(TurnGapMs);
        await olderRecorder.RecordStoppedAsync(CancellationToken.None);
        await Task.Delay(TurnGapMs);
        await newerRecorder.RecordStoppedAsync(CancellationToken.None);

        _store.Peek(UserId, ConversationKey)!.UserMessage.ShouldBe(OldRequest);
    }

    [Test]
    public async Task AStoppedTurnWithoutANewerTurn_RecordsItsAnchorAsBefore()
    {
        var older = NewTurn(OldRequest, out var olderRecorder);
        older.RegisterCalls([Ran(WriteSkill)]);

        await olderRecorder.RecordStoppedAsync(CancellationToken.None);

        var anchor = _store.Peek(UserId, ConversationKey);
        anchor.ShouldNotBeNull();
        anchor!.UserMessage.ShouldBe(OldRequest);
    }

    [Test]
    public async Task TheHistoryRowsOfAStoppedTurn_CarryTheTimeTheTurnBeganAndTheAnswerFollowsTheRequest()
    {
        var turn = NewTurn(OldRequest, out var recorder);
        await Task.Delay(TurnGapMs);

        await recorder.RecordStoppedAsync(CancellationToken.None);

        var user = _messages.Single(m => m.Role == "user");
        var assistant = _messages.Single(m => m.Role == "assistant");
        user.CreateTime.ShouldBe(turn.StartedAtUtc);
        assistant.CreateTime.ShouldNotBeNull();
        assistant.CreateTime!.Value.ShouldBeGreaterThan(user.CreateTime!.Value);
        (assistant.CreateTime.Value - user.CreateTime.Value).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMicroseconds(1));
        assistant.CreateTime.Value.ShouldBeLessThan(user.CreateTime.Value.AddMilliseconds(TurnGapMs));
    }

    [Test]
    public async Task ACompletedTurn_StillStampsItsHistoryRowsWithTheSaveTime()
    {
        var turn = NewTurn(OldRequest, out var recorder);
        await Task.Delay(TurnGapMs);
        var beforeSave = DateTime.UtcNow;

        await recorder.RecordCompletedAsync(Completion(turn, OldAnswer), CancellationToken.None);

        _messages.ShouldAllBe(m => m.CreateTime >= beforeSave);
    }

    [Test]
    public void TheAnchorCheck_IsFalseWithoutARecordAndForARecordOlderThanTheTurn()
    {
        var context = new LLMContext { Message = OldRequest, UserId = UserId.ToString() };
        var turnStart = DateTime.UtcNow;

        _preparation.HasLastActionSince(context, ConversationKey, turnStart).ShouldBeFalse();

        _store.Save(new AssistantLastAction
        {
            UserId = UserId, ConversationId = ConversationKey, CreateTimeUtc = turnStart.AddSeconds(-5)
        });
        _preparation.HasLastActionSince(context, ConversationKey, turnStart).ShouldBeFalse();
    }

    [Test]
    public void TheAnchorCheck_IsTrueForARecordWrittenAfterTheTurnBegan()
    {
        var context = new LLMContext { Message = OldRequest, UserId = UserId.ToString() };
        var turnStart = DateTime.UtcNow;
        _store.Save(new AssistantLastAction
        {
            UserId = UserId, ConversationId = ConversationKey, CreateTimeUtc = turnStart.AddSeconds(1)
        });

        _preparation.HasLastActionSince(context, ConversationKey, turnStart).ShouldBeTrue();
    }

    [Test]
    public void TheAnchorCheck_IsTrueForAnOlderRecordASupersedingTurnMarkedAfterTheTurnBegan()
    {
        var context = new LLMContext { Message = OldRequest, UserId = UserId.ToString() };
        var turnStart = DateTime.UtcNow;
        _store.Save(new AssistantLastAction
        {
            UserId = UserId, ConversationId = ConversationKey, CreateTimeUtc = turnStart.AddSeconds(-5)
        });
        _store.MarkSuperseded(UserId, ConversationKey);

        _preparation.HasLastActionSince(context, ConversationKey, turnStart).ShouldBeTrue();
    }

    [Test]
    public void TheAnchorCheck_ReadsAFailingStoreAsNoNewerRecord()
    {
        var failing = Substitute.For<IAssistantLastActionStore>();
        failing.Peek(Arg.Any<Guid>(), Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("db down"));
        var preparation = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(), null!, Substitute.For<IRecipeRunRecorder>(), null!,
            failing, Substitute.For<IDeterministicRouteProbe>(), Substitute.For<ISkillInverseResolver>(),
            NullLogger<TurnPreparationService>.Instance);

        preparation.HasLastActionSince(
            new LLMContext { Message = OldRequest, UserId = UserId.ToString() }, ConversationKey, DateTime.UtcNow)
            .ShouldBeFalse();
    }

    private TurnRunState NewTurn(string message, out TurnCompletionRecorder recorder)
    {
        var state = new TurnRunState();
        var context = new LLMContext
        {
            Message = message,
            UserId = UserId.ToString(),
            TurnId = Guid.NewGuid(),
            AvailableFunctions = [new LLMFunction { Name = WriteSkill }]
        };
        state.Begin(context);
        state.Attach(_conversation, _model, providerSupportsToolChoice: true);
        recorder = new TurnCompletionRecorder(
            NullLogger<TurnCompletionRecorder>.Instance,
            _conversationManager,
            _preparation,
            Substitute.For<IAgentRepository>(),
            Substitute.For<ILLMBackgroundTaskService>(),
            state,
            Substitute.For<IStoppedTurnCleanup>());
        return state;
    }

    private TurnCompletion Completion(TurnRunState state, string answer) => new(
        state.Context!,
        _conversation,
        _model,
        ProviderSupportsToolChoice: true,
        ResponseContent: answer,
        Usage: state.Usage,
        ElapsedMs: 1,
        TtftMs: null,
        ToolIterations: 1,
        FunctionCalls: state.Calls,
        ToolChoiceRequested: false,
        RecipePausedOnAsk: false,
        AnsweredWithNotice: false);

    private static LLMFunctionCall Ran(string skill) => new()
    {
        FunctionName = skill,
        Success = true,
        Result = "Done."
    };

    private sealed class InMemoryLastActionStore : IAssistantLastActionStore
    {
        private AssistantLastAction? _record;

        public void Save(AssistantLastAction action)
        {
            _record = new AssistantLastAction
            {
                UserId = action.UserId,
                ConversationId = action.ConversationId,
                UserMessage = action.UserMessage,
                Calls = action.Calls,
                AssistantAnswerExcerpt = action.AssistantAnswerExcerpt,
                CreateTimeUtc = action.CreateTimeUtc == default ? DateTime.UtcNow : action.CreateTimeUtc,
                SupersededAtUtc = action.SupersededAtUtc
            };
        }

        public AssistantLastAction? Peek(Guid userId, string conversationId) => _record;

        public void MarkSuperseded(Guid userId, string conversationId)
        {
            if (_record != null)
            {
                _record.SupersededAtUtc = DateTime.UtcNow;
            }
        }

        public void SaveClarificationCandidates(Guid userId, string conversationId, IReadOnlyList<string> skillNames)
        {
        }
    }
}
