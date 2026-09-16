// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Both chat entry points must prepare a correction identically: route the toolset assembly on the
/// composite of the corrected request and the correction, exclude the skills the corrected turn called,
/// and carry the resulting note onto the context. A divergence here would make the streaming and the
/// non-streaming chat answer the same correction differently, which is invisible in production. The
/// undo offer is here for the same reason and for one more: its pending-confirmation token is the only
/// side effect of a correction turn, and it must be written on both paths, once, and never without an
/// offer in the note.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class GracefulCorrectionEntryPointWiringTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string ConversationId = "conv-1";
    private const string CorrectionMessage = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";
    private const string PreviousMessage = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.";
    private const string Composite = "composite of both messages";
    private const string ExcludedSkillName = "find_customer_candidates";
    private const string ContextNote = "CORRECTION - the previous turn searched for customers.";

    private const string PinnedSkillName = "add_clients_to_group";

    private const string ClarificationReply =
        "I searched for customers. Did you mean adding clients to a group, or listing them?";
    private const string FirstCandidate = "add_clients_to_group";
    private const string SecondCandidate = "list_group_clients";

    private const string UndoSkillName = "remove_shift_from_group";
    private const string UndoneSkillLabel = "Assigns a shift to a group";
    private const string UndoArgumentName = "shiftId";

    private static readonly IReadOnlyDictionary<string, object> UndoArguments =
        new Dictionary<string, object> { [UndoArgumentName] = "shift-1", ["groupId"] = "group-1" };

    private ISkillToolsetAssembler _assembler = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private ILLMService _llmService = null!;
    private ISkillCacheService _skillCache = null!;
    private IAssistantLastActionStore _lastActionStore = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IPendingConfirmationStore _pendingConfirmationStore = null!;
    private LLMContext? _capturedContext;

    [SetUp]
    public void SetUp()
    {
        _capturedContext = null;
        _lastActionStore = Substitute.For<IAssistantLastActionStore>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _pendingConfirmationStore = Substitute.For<IPendingConfirmationStore>();

        _assembler = Substitute.For<ISkillToolsetAssembler>();
        _assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new SkillToolsetResult());

        _turnPreparation = Substitute.For<ITurnPreparationService>();

        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>())
            .Returns(new Agent { Id = Guid.NewGuid(), Name = "Klacksy" });

        _llmService = Substitute.For<ILLMService>();
        _llmService.ProcessAsync(Arg.Do<LLMContext>(c => _capturedContext = c))
            .Returns(new LLMResponse());
        _llmService.ProcessStreamAsync(Arg.Do<LLMContext>(c => _capturedContext = c), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyStream());
    }

    private static async IAsyncEnumerable<SseChunk> EmptyStream()
    {
        await Task.Yield();
        yield break;
    }

    private AssistantLastAction GivenAStoredAnchor(params string[] clarificationSkillNames)
    {
        var lastAction = new AssistantLastAction
        {
            UserId = Guid.Parse(UserId),
            ConversationId = ConversationId,
            UserMessage = PreviousMessage,
            CreateTimeUtc = DateTime.UtcNow,
            Calls = [new AssistantLastActionCall { SkillName = ExcludedSkillName, Success = true }],
            ClarificationSkillNames = clarificationSkillNames
        };

        _lastActionStore.Peek(Guid.Parse(UserId), ConversationId).Returns(lastAction);
        return lastAction;
    }

    private void GivenACorrectionIsPlanned()
    {
        var lastAction = GivenAStoredAnchor();

        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GracefulCorrectionPlan(
                lastAction, CorrectionMessage, Composite, new[] { ExcludedSkillName }));

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>())
            .Returns(new GracefulCorrectionOutcome(ContextNote, null, []));
    }

    private void GivenAClarificationIsPlanned()
    {
        var lastAction = GivenAStoredAnchor();

        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GracefulCorrectionPlan(
                lastAction, CorrectionMessage, Composite, new[] { ExcludedSkillName }));

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>())
            .Returns(new GracefulCorrectionOutcome(
                ContextNote, ClarificationReply, [FirstCandidate, SecondCandidate]));
    }

    private ProcessLLMMessageCommandHandler CreateHandler()
    {
        var providerOrchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(),
            Substitute.For<ILLMProviderFactory>(),
            Substitute.For<ILLMRepository>());

        var agentRepository = Substitute.For<IAgentRepository>();

        return new ProcessLLMMessageCommandHandler(
            _llmService, agentRepository, _skillCache, _assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            Substitute.For<IContextBudgetPolicy>(),
            _lastActionStore,
            _pendingRecipeStore,
            _turnPreparation,
            _pendingConfirmationStore,
            Substitute.For<ILogger<ProcessLLMMessageCommandHandler>>());
    }

    private LLMStreamingOrchestrator CreateOrchestrator()
    {
        var providerOrchestrator = new LLMProviderOrchestrator(
            Substitute.For<ILogger<LLMProviderOrchestrator>>(),
            Substitute.For<ILLMProviderFactory>(),
            Substitute.For<ILLMRepository>());

        return new LLMStreamingOrchestrator(
            _llmService, _skillCache, _assembler,
            Substitute.For<IPlanningScopeEnricher>(),
            Substitute.For<IEntityCandidateGrounder>(),
            providerOrchestrator,
            Substitute.For<IContextBudgetPolicy>(),
            _lastActionStore,
            _pendingRecipeStore,
            _turnPreparation,
            _pendingConfirmationStore,
            Substitute.For<ILogger<LLMStreamingOrchestrator>>());
    }

    private static ProcessLLMMessageCommand Command() => new()
    {
        Message = CorrectionMessage,
        UserId = UserId,
        ConversationId = ConversationId,
        UserRights = new List<string>()
    };

    private static LLMStreamRequest StreamRequest() => new()
    {
        Message = CorrectionMessage,
        UserId = UserId,
        ConversationId = ConversationId,
        UserRights = new List<string>()
    };

    private async Task Drain(IAsyncEnumerable<SseChunk> source)
    {
        await foreach (var chunk in source)
        {
            _ = chunk;
        }
    }

    private Task AssembledOn(string message, bool withExclusion) =>
        _assembler.Received(1).AssembleAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), message, Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Is<IReadOnlyCollection<string>?>(
                excluded => withExclusion
                    ? excluded != null && excluded.Contains(ExcludedSkillName)
                    : excluded == null),
            Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>());

    [Test]
    public async Task NonStreaming_WithAPlannedCorrection_AssemblesOnTheCompositeWithTheExclusion()
    {
        GivenACorrectionIsPlanned();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        await AssembledOn(Composite, withExclusion: true);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBe(ContextNote);
        _capturedContext.GracefulCorrectionApplied.ShouldBeTrue();
    }

    [Test]
    public async Task NonStreaming_WithoutACorrection_AssemblesOnThePlainMessage()
    {
        await CreateHandler().Handle(Command(), CancellationToken.None);

        await AssembledOn(CorrectionMessage, withExclusion: false);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBeNull();
        _capturedContext.GracefulCorrectionApplied.ShouldBeFalse();
    }

    [Test]
    public async Task Streaming_WithAPlannedCorrection_AssemblesOnTheCompositeWithTheExclusion()
    {
        GivenACorrectionIsPlanned();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        await AssembledOn(Composite, withExclusion: true);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBe(ContextNote);
        _capturedContext.GracefulCorrectionApplied.ShouldBeTrue();
    }

    [Test]
    public async Task Streaming_WithoutACorrection_AssemblesOnThePlainMessage()
    {
        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        await AssembledOn(CorrectionMessage, withExclusion: false);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBeNull();
        _capturedContext.GracefulCorrectionApplied.ShouldBeFalse();
    }

    // The pins are the two options a clarification question offered on the previous turn. They are read
    // off the anchor even when it can no longer anchor a correction, so whichever option the user names
    // is in this turn's toolset regardless of retrieval.
    private Task AssemblerPinned(string skillName) =>
        _assembler.Received(1).AssembleAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
            Arg.Is<IReadOnlyCollection<string>?>(pinned => pinned != null && pinned.Contains(skillName)),
            Arg.Any<CancellationToken>());

    [Test]
    public async Task NonStreaming_ClarificationPinsOfTheAnchor_ReachTheAssembler()
    {
        GivenAStoredAnchor(PinnedSkillName);

        await CreateHandler().Handle(Command(), CancellationToken.None);

        await AssemblerPinned(PinnedSkillName);
    }

    [Test]
    public async Task Streaming_ClarificationPinsOfTheAnchor_ReachTheAssembler()
    {
        GivenAStoredAnchor(PinnedSkillName);

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        await AssemblerPinned(PinnedSkillName);
    }

    // The two options of a question just asked are pinned onto the anchor, so the turn that answers it has
    // both in its toolset no matter which one the user names. The write happens only when a question was
    // actually asked: an ordinary correction turn, and every turn whose language has no authored labels,
    // must leave the record untouched.
    private void TheCandidatesWerePinned() =>
        _lastActionStore.Received(1).SaveClarificationCandidates(
            Guid.Parse(UserId), ConversationId,
            Arg.Is<IReadOnlyList<string>>(
                names => names.Contains(FirstCandidate) && names.Contains(SecondCandidate)));

    private void NothingWasPinned() =>
        _lastActionStore.DidNotReceiveWithAnyArgs().SaveClarificationCandidates(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());

    [Test]
    public async Task NonStreaming_WithAClarification_PinsTheTwoCandidates()
    {
        GivenAClarificationIsPlanned();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        TheCandidatesWerePinned();
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionClarificationReply.ShouldBe(ClarificationReply);
    }

    [Test]
    public async Task Streaming_WithAClarification_PinsTheTwoCandidates()
    {
        GivenAClarificationIsPlanned();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        TheCandidatesWerePinned();
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionClarificationReply.ShouldBe(ClarificationReply);
    }

    // The pin is a convenience of the NEXT turn. A store outage may cost it, never the question this turn
    // has already computed and is about to send.
    [Test]
    public async Task NonStreaming_WhenThePinWriteThrows_TheTurnStillRuns()
    {
        GivenAClarificationIsPlanned();
        _lastActionStore
            .When(store => store.SaveClarificationCandidates(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()))
            .Do(_ => throw new InvalidOperationException("store down"));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionClarificationReply.ShouldBe(ClarificationReply);
    }

    [Test]
    public async Task Streaming_WhenThePinWriteThrows_TheTurnStillRuns()
    {
        GivenAClarificationIsPlanned();
        _lastActionStore
            .When(store => store.SaveClarificationCandidates(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()))
            .Do(_ => throw new InvalidOperationException("store down"));

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionClarificationReply.ShouldBe(ClarificationReply);
    }

    // A correction turn that holds no token of its own must NOT claim one. The two flags are separate
    // precisely here: the turn corrected (GracefulCorrectionApplied, which no production code reads yet
    // and which task 7 will read) but left nothing redeemable behind, so the next turn's settlement of a
    // predecessor's row has to run as on any other turn.
    [Test]
    public async Task NonStreaming_WithACorrectionButNoClarification_PinsNothing()
    {
        GivenACorrectionIsPlanned();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        NothingWasPinned();
        _capturedContext!.GracefulCorrectionApplied.ShouldBeTrue();
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public async Task Streaming_WithACorrectionButNoClarification_PinsNothing()
    {
        GivenACorrectionIsPlanned();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        NothingWasPinned();
        _capturedContext!.GracefulCorrectionApplied.ShouldBeTrue();
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public async Task NonStreaming_WithoutACorrection_PinsNothing()
    {
        await CreateHandler().Handle(Command(), CancellationToken.None);

        NothingWasPinned();
    }

    [Test]
    public async Task Streaming_WithoutACorrection_PinsNothing()
    {
        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        NothingWasPinned();
    }

    // Gate G1 only matters once an anchor could carry a correction at all. Without one the pending-recipe
    // row is irrelevant, and reading it would cost a query on every ordinary turn of every conversation.
    [Test]
    public async Task NonStreaming_WithoutAnAnchor_ThePendingRecipeStoreIsNotRead()
    {
        await CreateHandler().Handle(Command(), CancellationToken.None);

        _pendingRecipeStore.DidNotReceive().Peek(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Streaming_WithoutAnAnchor_ThePendingRecipeStoreIsNotRead()
    {
        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        _pendingRecipeStore.DidNotReceive().Peek(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task NonStreaming_WithAnAnchorThatCanCorrect_ThePendingRecipeStoreIsRead()
    {
        GivenAStoredAnchor();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _pendingRecipeStore.Received(1).Peek(Guid.Parse(UserId), ConversationId);
    }

    [Test]
    public async Task Streaming_WithAnAnchorThatCanCorrect_ThePendingRecipeStoreIsRead()
    {
        GivenAStoredAnchor();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        _pendingRecipeStore.Received(1).Peek(Guid.Parse(UserId), ConversationId);
    }

    // The note is a convenience of the turn, never its purpose: a failure while building it must cost the
    // correction, not the answer the user is waiting for.
    [Test]
    public async Task NonStreaming_WhenTheCompletionThrows_TheTurnDegradesToAnOrdinaryOne()
    {
        GivenACorrectionIsPlanned();
        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>())
            .Returns<GracefulCorrectionOutcome>(_ => throw new InvalidOperationException("note build failed"));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        await AssembledOn(Composite, withExclusion: true);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBeNull();
        _capturedContext.GracefulCorrectionApplied.ShouldBeFalse();
    }

    [Test]
    public async Task Streaming_WhenTheCompletionThrows_TheTurnDegradesToAnOrdinaryOne()
    {
        GivenACorrectionIsPlanned();
        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>())
            .Returns<GracefulCorrectionOutcome>(_ => throw new InvalidOperationException("note build failed"));

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        await AssembledOn(Composite, withExclusion: true);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBeNull();
        _capturedContext.GracefulCorrectionApplied.ShouldBeFalse();
    }

    // The undo offer of rule 3 is a yes/no question, so the invocation it offers has to be held as a
    // pending confirmation the affirmation can redeem. That write belongs to a real chat turn and to
    // these two entry points ALONE - the turn preparation resolves the undo as data, so the headless
    // replay can score the same offer without leaving a redeemable token in a user's account.
    private void GivenAnUndoIsOffered()
    {
        var lastAction = GivenAStoredAnchor();

        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GracefulCorrectionPlan(
                lastAction, CorrectionMessage, Composite, new[] { ExcludedSkillName }));

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>())
            .Returns(new GracefulCorrectionOutcome(
                ContextNote, null, [],
                new SkillUndoInvocation(UndoSkillName, UndoArguments),
                UndoneSkillLabel));
    }

    // Under its OWN purpose, not the gate's: an undo offer is answerable by the turn that immediately
    // follows it and by no other, and only a purpose of its own lets the next turn tell the two apart.
    private void TheUndoWasHeldOnce() =>
        _pendingConfirmationStore.Received(1).Create(
            Guid.Parse(UserId), UndoSkillName,
            Arg.Is<IReadOnlyDictionary<string, object>>(
                arguments => arguments.ContainsKey(UndoArgumentName)),
            PendingConfirmationPurposes.CorrectionUndo);

    private void NoConfirmationWasHeld() =>
        _pendingConfirmationStore.DidNotReceiveWithAnyArgs().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(),
            Arg.Any<string>());

    [Test]
    public async Task NonStreaming_WithAnOfferedUndo_HoldsItAsExactlyOnePendingConfirmation()
    {
        GivenAnUndoIsOffered();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        TheUndoWasHeldOnce();
        _capturedContext!.CorrectionUndoOffered.ShouldBeTrue();
    }

    [Test]
    public async Task Streaming_WithAnOfferedUndo_HoldsItAsExactlyOnePendingConfirmation()
    {
        GivenAnUndoIsOffered();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        TheUndoWasHeldOnce();
        _capturedContext!.CorrectionUndoOffered.ShouldBeTrue();
    }

    [Test]
    public async Task NonStreaming_WithAClarificationInsteadOfAnUndo_HoldsNothing()
    {
        GivenAClarificationIsPlanned();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        NoConfirmationWasHeld();
    }

    [Test]
    public async Task Streaming_WithAClarificationInsteadOfAnUndo_HoldsNothing()
    {
        GivenAClarificationIsPlanned();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        NoConfirmationWasHeld();
    }

    [Test]
    public async Task NonStreaming_WithoutAnUndo_HoldsNothing()
    {
        GivenACorrectionIsPlanned();

        await CreateHandler().Handle(Command(), CancellationToken.None);

        NoConfirmationWasHeld();
    }

    [Test]
    public async Task Streaming_WithoutAnUndo_HoldsNothing()
    {
        GivenACorrectionIsPlanned();

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        NoConfirmationWasHeld();
    }

    // Same trade as the pin write: losing the token costs the user one convenient "yes", while a thrown
    // store call would cost the answer the turn has already produced. The flag follows the write, not the
    // intent: nothing was held, so the next turn must settle whatever row is outstanding.
    [Test]
    public async Task NonStreaming_WhenTheUndoWriteThrows_TheTurnStillRuns()
    {
        GivenAnUndoIsOffered();
        _pendingConfirmationStore
            .When(store => store.Create(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("store down"));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBe(ContextNote);
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public async Task Streaming_WhenTheUndoWriteThrows_TheTurnStillRuns()
    {
        GivenAnUndoIsOffered();
        _pendingConfirmationStore
            .When(store => store.Create(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("store down"));

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.CorrectionNote.ShouldBe(ContextNote);
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    // The store reads sit in front of the planning on both paths; a store outage must degrade the turn
    // to an ordinary one rather than fail the chat.
    [Test]
    public async Task NonStreaming_WhenThePlanningThrows_TheTurnStillRuns()
    {
        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<GracefulCorrectionPlan?>>(_ => throw new InvalidOperationException("store down"));

        await CreateHandler().Handle(Command(), CancellationToken.None);

        await AssembledOn(CorrectionMessage, withExclusion: false);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.GracefulCorrectionApplied.ShouldBeFalse();
    }

    [Test]
    public async Task Streaming_WhenThePlanningThrows_TheTurnStillRuns()
    {
        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<GracefulCorrectionPlan?>>(_ => throw new InvalidOperationException("store down"));

        await Drain(CreateOrchestrator().ProcessStreamAsync(StreamRequest()));

        await AssembledOn(CorrectionMessage, withExclusion: false);
        _capturedContext.ShouldNotBeNull();
        _capturedContext!.GracefulCorrectionApplied.ShouldBeFalse();
    }
}
