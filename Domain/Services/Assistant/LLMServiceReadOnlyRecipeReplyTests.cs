// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Loop-level regression guard for the reply that follows a read-only recipe (live 2026-09-26,
/// period-close-schedule): the final search step's note says what the NEXT reply must contain, but the note
/// used to reach only the forced call, never the reply call, and the reply call got the full toolset. The model
/// then took the user's slot answer as an order to store and called the Sensitive store skill (a waiting
/// confirmation token), or claimed "stored" without any write. Pinned here on the real loop: the reply call
/// carries the step note, a write call in it never reaches the skill bridge, and a completion claim gets the
/// nothing-stored notice. A mutating recipe keeps its previous behaviour.
/// LLMFunctionExecutor is used for real; the skill layer is replaced one level deeper through ILLMSkillBridge.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BridgeLLMFunctionCall = Klacks.Api.Domain.Services.Assistant.Providers.LLMFunctionCall;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceReadOnlyRecipeReplyTests
{
    private const string ConversationId = "conv-read-only-reply";
    private const string ReadSkill = "get_period_close_schedule";
    private const string StoreSkill = "set_period_close_lag";
    private const string StepNote = "Your NEXT reply lists the close date of every group.";
    private const string SlotAnswer = "5 Tage";
    private const string FalseClaim = "Erledigt – ich habe 5 Tage als Abschlussfrist gespeichert.";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe ReadOnlyRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "period-close-schedule",
        Goal = "Find out when periods are closed.",
        TriggerJson = """{"allOf":[{"anyWordStart":["abschluss"]}],"noneOf":[]}""",
        StepsJson = $$"""[{"kind":"ask","slot":"lagDays","prompt":"How many days?"},{"kind":"search","skill":"{{ReadSkill}}","note":"{{StepNote}}"}]""",
        IsEnabled = true,
    };

    private static readonly AgentRecipe MutatingRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "store-close-lag",
        Goal = "Store the lag.",
        TriggerJson = """{"allOf":[{"anyWordStart":["nachlauf"]}],"noneOf":[]}""",
        StepsJson = $$"""[{"kind":"ask","slot":"lagDays","prompt":"How many days?"},{"kind":"mutate","skill":"{{StoreSkill}}","note":"{{StepNote}}"}]""",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private ILLMSkillBridge _skillBridge = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { ReadOnlyRecipe, MutatingRecipe });
        _recipeRepository.GetByNameAsync(ReadOnlyRecipe.Name, Arg.Any<CancellationToken>()).Returns(ReadOnlyRecipe);
        _recipeRepository.GetByNameAsync(MutatingRecipe.Name, Arg.Any<CancellationToken>()).Returns(MutatingRecipe);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<BridgeLLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, ResultType = "Data", Message = "Close dates computed." });

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        scopedProvider.GetService(typeof(IKnowledgeRetrievalService))
            .Returns(Substitute.For<IKnowledgeRetrievalService>());
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        var agentRepository = Substitute.For<IAgentRepository>();
        agentRepository.GetDefaultAgentAsync().Returns((Agent?)null);

        var functionExecutor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            Substitute.For<IAgentSkillRepository>(),
            agentRepository,
            Substitute.For<IPendingConfirmationStore>(),
            Substitute.For<ITurnConfirmationScope>(),
            Substitute.For<ICancellableSkillPolicy>(),
            _skillBridge);

        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: functionExecutor,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            recipeEngine: recipeEngine,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!,
            turnPreparation: new TurnPreparationService(
                Substitute.For<IPendingConfirmationStore>(),
                recipeEngine,
                Substitute.For<IRecipeRunRecorder>(),
                new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
                Substitute.For<IAssistantLastActionStore>(),
                Substitute.For<IDeterministicRouteProbe>(),
                Substitute.For<ISkillInverseResolver>(),
                Substitute.For<ILogger<TurnPreparationService>>()),
            turnCompletionRecorder: InertTurnCompletionRecorder.Create(),
            turnState: new TurnRunState());
    }

    private static MultiTurnContext BuildContext(ILLMProvider provider) => new(
        new LLMContext
        {
            Message = SlotAnswer,
            Language = "de",
            UserId = UserId.ToString(),
            AvailableFunctions = new List<LLMFunction> { new() { Name = ReadSkill }, new() { Name = StoreSkill } }
        },
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew());

    private void ResumeAtAskStep(AgentRecipe recipe) =>
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = recipe.Name,
            AwaitingConfirmation = false,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

    private static LLMProviderResponse Calls(string skill) => new()
    {
        Success = true,
        Content = string.Empty,
        FunctionCalls = new List<BridgeLLMFunctionCall>
        {
            new() { FunctionName = skill, Parameters = new Dictionary<string, object> { ["lagDays"] = 5 } }
        }
    };

    private static LLMProviderResponse Text(string content) => new()
    {
        Success = true,
        Content = content,
        FunctionCalls = new List<BridgeLLMFunctionCall>()
    };

    private static ILLMProvider Provider(params LLMProviderResponse[] responses)
    {
        var provider = Substitute.For<ILLMProvider>();
        var queue = new Queue<LLMProviderResponse>(responses);
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        return provider;
    }

    private Task ReceivedBridgeCallFor(string skill, int times) =>
        _skillBridge.Received(times).ExecuteSkillFromLLMCallAsync(
            Arg.Is<BridgeLLMFunctionCall>(call => call.FunctionName == skill),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());

    [Test]
    public async Task ReplyAfterReadOnlyRecipe_CarriesTheFinalStepNote()
    {
        ResumeAtAskStep(ReadOnlyRecipe);
        var provider = Provider(Calls(ReadSkill), Text("Bern schliesst am 05.10."));

        await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        await provider.Received().ProcessAsync(
            Arg.Is<LLMProviderRequest>(request =>
                request.VolatileSystemPrompt != null
                && request.VolatileSystemPrompt.Contains(
                    RecipeEngineDefaults.ReadOnlyRecipeCompletedNotePrefix, StringComparison.Ordinal)
                && request.VolatileSystemPrompt.Contains(StepNote, StringComparison.Ordinal)
                && request.AvailableFunctions.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WriteCallAfterReadOnlyRecipe_IsRejectedAndNeverReachesTheSkill()
    {
        ResumeAtAskStep(ReadOnlyRecipe);
        var provider = Provider(Calls(ReadSkill), Calls(StoreSkill), Text("Soll ich 5 Tage speichern?"));

        var (_, _, _, allFunctionCalls, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        await ReceivedBridgeCallFor(ReadSkill, 1);
        await ReceivedBridgeCallFor(StoreSkill, 0);
        var rejected = allFunctionCalls.Single(call => call.FunctionName == StoreSkill);
        rejected.Success.ShouldBeFalse();
        rejected.RequiresConfirmation.ShouldBeFalse();
        rejected.Result.ShouldBe(LLMLoopConstants.ReadOnlyRecipeWriteRejectedResult);
    }

    [Test]
    public async Task ConfirmPendingActionAfterReadOnlyRecipe_IsRejected()
    {
        ResumeAtAskStep(ReadOnlyRecipe);
        var provider = Provider(Calls(ReadSkill), Calls(AutonomyDefaults.ConfirmPendingActionSkillName), Text("Bern: 05.10."));

        await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        await ReceivedBridgeCallFor(AutonomyDefaults.ConfirmPendingActionSkillName, 0);
    }

    [Test]
    public async Task CompletionClaimAfterReadOnlyRecipe_GetsTheNothingStoredNotice()
    {
        ResumeAtAskStep(ReadOnlyRecipe);
        var provider = Provider(Calls(ReadSkill), Text(FalseClaim));

        var (response, _, _, _, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        GracefulCorrectionTexts.TryGetText(GracefulCorrectionTexts.RecipeNothingStoredNotice, "de", out var notice)
            .ShouldBeTrue();
        response.ShouldStartWith(FalseClaim);
        response.ShouldEndWith(notice);
    }

    [Test]
    public async Task HonestOfferAfterReadOnlyRecipe_GetsNoNotice()
    {
        ResumeAtAskStep(ReadOnlyRecipe);
        const string offer = "Bern schliesst am 05.10. Soll ich 5 Tage speichern?";
        var provider = Provider(Calls(ReadSkill), Text(offer));

        var (response, _, _, _, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        response.ShouldBe(offer);
    }

    [Test]
    public async Task MutatingRecipe_KeepsItsBehaviour_NoCompletionNoteAndItsWriteRuns()
    {
        ResumeAtAskStep(MutatingRecipe);
        var provider = Provider(Calls(StoreSkill), Text("Gespeichert."));

        await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        await ReceivedBridgeCallFor(StoreSkill, 1);
        await provider.DidNotReceive().ProcessAsync(
            Arg.Is<LLMProviderRequest>(request =>
                request.VolatileSystemPrompt != null
                && request.VolatileSystemPrompt.Contains(
                    RecipeEngineDefaults.ReadOnlyRecipeCompletedNotePrefix, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }
}
