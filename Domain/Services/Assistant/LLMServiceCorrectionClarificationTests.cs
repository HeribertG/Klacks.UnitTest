// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A correction whose re-routing produced two equally plausible candidates is answered with the authored
/// question and with NO provider call at all - the one case in which the assistant asks before acting.
/// Both chat paths are pinned here because a divergence between them is invisible in production: the same
/// correction would then be answered deterministically in one client and by the model in the other.
/// The fixture runs the turn in German, which is now a language the question genuinely exists in -
/// which language it can be asked in is decided by CorrectionOutcomeComposer and tested there, while
/// LLMService has to relay whatever reply the context carries.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using RepositoryLLMMessage = Klacks.Api.Domain.Models.Assistant.LLMMessage;
using RepositoryLLMUsage = Klacks.Api.Domain.Models.Assistant.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceCorrectionClarificationTests
{
    private const string ConversationId = "conv-correction-clarification";
    private const string ModelId = "model-under-test";
    private const string CorrectionMessage = "Nein, ich meinte alle Mitarbeitenden.";
    private const string Clarification =
        "I searched for customers. Did you mean adding clients to a group, or listing the clients of a group?";
    private const string ModelAnswer = "Die Mitarbeitenden sind eingetragen.";

    private ILLMRepository _repository = null!;
    private ILLMProvider _provider = null!;
    private ILLMBackgroundTaskService _backgroundTaskService = null!;
    private IAgentRepository _agentRepository = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _repository.GetModelByIdAsync(ModelId).Returns(new LLMModel
        {
            ModelId = ModelId,
            ApiModelId = ModelId,
            IsEnabled = true,
            MaxTokens = 4096
        });
        _repository.GetOrCreateConversationAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationId });
        _repository.GetConversationMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new List<RepositoryLLMMessage>());
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns(call => call.Arg<RepositoryLLMMessage>());
        _repository.TrackUsageAsync(Arg.Any<RepositoryLLMUsage>())
            .Returns(call => call.Arg<RepositoryLLMUsage>());

        _provider = Substitute.For<ILLMProvider>();
        _provider.SupportsStreaming.Returns(false);
        _provider.GetEffectiveInputTokenLimit(Arg.Any<LLMModel>()).Returns(64_000);
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = ModelAnswer });

        var providerFactory = Substitute.For<ILLMProviderFactory>();
        providerFactory.GetProviderForModelAsync(ModelId).Returns(_provider);

        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync().Returns((Agent?)null);
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        _backgroundTaskService = Substitute.For<ILLMBackgroundTaskService>();

        _turnPreparation = Substitute.For<ITurnPreparationService>();
        _turnPreparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(null, false, null, null));

        var contextBudgetPolicy = Substitute.For<IContextBudgetPolicy>();
        contextBudgetPolicy.Resolve(Arg.Any<ILLMProvider>(), Arg.Any<LLMModel>())
            .Returns(new ContextBudgetProfile(20, 30, 5, 5, 8_000));

        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetNowAsync(Arg.Any<CancellationToken>()).Returns(DateTimeOffset.UtcNow);
        companyClock.GetTimeZoneResolutionAsync(Arg.Any<CancellationToken>())
            .Returns(new CompanyTimeZoneResolution(TimeZoneInfo.Utc, CompanyTimeZoneSource.Utc));

        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var conversationManager = new LLMConversationManager(
            Substitute.For<ILogger<LLMConversationManager>>(), _repository);

        var turnState = new TurnRunState();

        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: new LLMProviderOrchestrator(
                Substitute.For<ILogger<LLMProviderOrchestrator>>(), providerFactory, _repository),
            conversationManager: conversationManager,
            functionExecutor: new LLMFunctionExecutor(
                Substitute.For<ILogger<LLMFunctionExecutor>>(),
                Substitute.For<IAgentSkillRepository>(),
                _agentRepository,
                Substitute.For<IPendingConfirmationStore>(),
                Substitute.For<ITurnConfirmationScope>(),
                Substitute.For<ILLMSkillBridge>()),
            responseBuilder: new LLMResponseBuilder(),
            promptBuilder: new LLMSystemPromptBuilder(
                new PromptTranslationProvider(
                    scopeFactory, Substitute.For<ILogger<PromptTranslationProvider>>()),
                companyClock),
            agentRepository: _agentRepository,
            contextAssemblyPipeline: null!,
            backgroundTaskService: _backgroundTaskService,
            recipeEngine: new RecipeEngineService(
                scopeFactory,
                Substitute.For<IPendingRecipeStore>(),
                Substitute.For<ILogger<RecipeEngineService>>()),
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            suggestionEntityNameReader: Substitute.For<ISuggestionEntityNameReader>(),
            contextBudgetPolicy: contextBudgetPolicy,
            turnPreparation: _turnPreparation,
            turnCompletionRecorder: new TurnCompletionRecorder(
                Substitute.For<ILogger<TurnCompletionRecorder>>(),
                conversationManager, _turnPreparation, _agentRepository, _backgroundTaskService, turnState),
            turnState: turnState);
    }

    private static LLMContext Context(string? clarificationReply) => new()
    {
        Message = CorrectionMessage,
        UserId = Guid.NewGuid().ToString(),
        ConversationId = ConversationId,
        ModelId = ModelId,
        Language = "de",
        TurnId = Guid.NewGuid(),
        AvailableFunctions = new List<LLMFunction>(),
        CorrectionNote = "CORRECTION - the previous turn searched for customers.",
        CorrectionClarificationReply = clarificationReply,
        GracefulCorrectionApplied = clarificationReply != null
    };

    private async Task<List<SseChunk>> Stream(LLMContext context)
    {
        var chunks = new List<SseChunk>();
        await foreach (var chunk in _service.ProcessStreamAsync(context))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private Task ProviderWasNotCalled() =>
        _provider.DidNotReceiveWithAnyArgs().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());

    private void TheClarificationWasStoredAsTheAnswer() =>
        _repository.Received(1).SaveMessageAsync(
            Arg.Is<RepositoryLLMMessage>(m => m.Content == Clarification));

    private void TheTurnWasTrackedWithoutAToolCall() =>
        _repository.Received(1).TrackUsageAsync(
            Arg.Is<RepositoryLLMUsage>(u => !u.ToolCallReturned && u.ToolIterations == 0));

    private void TheBackgroundTasksRanWithAnEmptyCallList() =>
        _backgroundTaskService.Received(1).RunBackgroundTasks(
            Arg.Any<Agent?>(), Arg.Any<LLMConversation>(), Arg.Any<LLMContext>(), Clarification,
            Arg.Is<List<LLMFunctionCall>>(calls => calls.Count == 0));

    [Test]
    public async Task NonStreaming_WithAClarification_AnswersWithItAndCallsNoProvider()
    {
        var response = await _service.ProcessAsync(Context(Clarification));

        response.Message.ShouldBe(Clarification);
        response.ConversationId.ShouldBe(ConversationId);
        await ProviderWasNotCalled();
    }

    [Test]
    public async Task NonStreaming_WithAClarification_PersistsTheTurnLikeAnOrdinaryOne()
    {
        await _service.ProcessAsync(Context(Clarification));

        TheClarificationWasStoredAsTheAnswer();
        TheTurnWasTrackedWithoutAToolCall();
        TheBackgroundTasksRanWithAnEmptyCallList();
    }

    // This turn executed nothing, so there is nothing to record: the call is skipped because it would
    // only redundantly mark the record superseded, not because the pins on it would be at risk.
    [Test]
    public async Task NonStreaming_WithAClarification_LeavesThePreviousActionRecordAlone()
    {
        await _service.ProcessAsync(Context(Clarification));

        _turnPreparation.DidNotReceiveWithAnyArgs().RecordLastAction(
            Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<LLMFunctionCall>>(), Arg.Any<bool>());
    }

    // The question is fully computed before the turn ever reaches the store. Losing it to a storage
    // outage would turn a recoverable persistence failure into a lost answer.
    [Test]
    public async Task NonStreaming_WhenThePersistenceFails_TheQuestionIsStillAnswered()
    {
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns<RepositoryLLMMessage>(_ => throw new InvalidOperationException("store down"));

        var response = await _service.ProcessAsync(Context(Clarification));

        response.Message.ShouldBe(Clarification);
        response.ConversationId.ShouldBe(ConversationId);
    }

    [Test]
    public async Task NonStreaming_WithoutAClarification_TheTurnReachesTheProvider()
    {
        var response = await _service.ProcessAsync(Context(null));

        response.Message.ShouldBe(ModelAnswer);
        await _provider.ReceivedWithAnyArgs(1).ProcessAsync(
            Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Streaming_WithAClarification_EmitsItOnceAndCallsNoProvider()
    {
        var chunks = await Stream(Context(Clarification));

        chunks.Where(c => c.Type == SseChunkType.Content).Select(c => c.Text).ShouldBe(new[] { Clarification });
        chunks[^2].Type.ShouldBe(SseChunkType.Metadata);
        chunks[^1].Type.ShouldBe(SseChunkType.Done);
        await ProviderWasNotCalled();
    }

    // SseChunk.Metadata carries no message text by design - the answer has already streamed as Content -
    // so the payload is pinned through the fields it does carry: a zero usage and an empty call list are
    // the fingerprint of the clarification response, which no model turn of this fixture produces.
    [Test]
    public async Task Streaming_WithAClarification_TheMetadataCarriesTheClarificationPayload()
    {
        var chunks = await Stream(Context(Clarification));

        var metadata = chunks.Single(c => c.Type == SseChunkType.Metadata);
        metadata.Usage.ShouldNotBeNull();
        metadata.Usage!.TotalTokens.ShouldBe(0);
        metadata.Usage.Cost.ShouldBe(0);
        metadata.ActionPerformed.ShouldBeFalse();
        metadata.FunctionCalls.ShouldBeEmpty();
        metadata.NavigateTo.ShouldBeNull();
    }

    [Test]
    public async Task Streaming_WithAClarification_PersistsTheTurnLikeTheNonStreamingPath()
    {
        await Stream(Context(Clarification));

        TheClarificationWasStoredAsTheAnswer();
        TheTurnWasTrackedWithoutAToolCall();
        TheBackgroundTasksRanWithAnEmptyCallList();
    }

    [Test]
    public async Task Streaming_WithAClarification_LeavesThePreviousActionRecordAlone()
    {
        await Stream(Context(Clarification));

        _turnPreparation.DidNotReceiveWithAnyArgs().RecordLastAction(
            Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<LLMFunctionCall>>(), Arg.Any<bool>());
    }

    // A persistence failure must not swallow the question the user is waiting for: the stream has already
    // put it on screen, so the turn still ends with its metadata and its done event.
    [Test]
    public async Task Streaming_WhenThePersistenceFails_TheQuestionStillCompletesTheStream()
    {
        _repository.SaveMessageAsync(Arg.Any<RepositoryLLMMessage>())
            .Returns<RepositoryLLMMessage>(_ => throw new InvalidOperationException("store down"));

        var chunks = await Stream(Context(Clarification));

        chunks.Where(c => c.Type == SseChunkType.Content).Select(c => c.Text).ShouldBe(new[] { Clarification });
        chunks[^1].Type.ShouldBe(SseChunkType.Done);
    }

    [Test]
    public async Task Streaming_WithoutAClarification_TheTurnReachesTheProvider()
    {
        await Stream(Context(null));

        await _provider.ReceivedWithAnyArgs(1).ProcessAsync(
            Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }
}
