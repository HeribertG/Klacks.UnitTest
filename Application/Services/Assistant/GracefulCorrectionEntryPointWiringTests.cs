// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Both chat entry points must prepare a correction identically: route the toolset assembly on the
/// composite of the corrected request and the correction, exclude the skills the corrected turn called,
/// and carry the resulting note onto the context. A divergence here would make the streaming and the
/// non-streaming chat answer the same correction differently, which is invisible in production.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
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

    private ISkillToolsetAssembler _assembler = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private ILLMService _llmService = null!;
    private ISkillCacheService _skillCache = null!;
    private IAssistantLastActionStore _lastActionStore = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private LLMContext? _capturedContext;

    [SetUp]
    public void SetUp()
    {
        _capturedContext = null;
        _lastActionStore = Substitute.For<IAssistantLastActionStore>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();

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
