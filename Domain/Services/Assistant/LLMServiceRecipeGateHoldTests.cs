// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression guard for the gate-hold zombie plan: when the autonomy gate holds a recipe-forced step
/// for confirmation, the pending recipe must be cleared from the store. Left behind, the row still
/// carries the step index of the LAST ask pause — so the next turn resumes there, fills the user's
/// "yes" (the answer to the gate question) into that ask slot, and re-forces already executed steps
/// with the repeated-write guard disabled for forced recipes.
/// LLMFunctionExecutor is used for real (its methods are not virtual, so it cannot be substituted);
/// the hold is injected one layer deeper through ILLMSkillBridge, which is what really decides it.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BridgeLLMFunctionCall = Klacks.Api.Domain.Services.Assistant.Providers.LLMFunctionCall;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceRecipeGateHoldTests
{
    private const string ConversationId = "conv-gate-hold";
    private const string MutateSkill = "add_employee_note";
    private const string AskReply = "Verträgt keine Nachtschichten";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe AskThenMutateRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "add-employee-note",
        Goal = "Add a note to an employee.",
        TriggerJson = """{"allOf":[{"anyWordStart":["notiz"]}],"noneOf":[]}""",
        StepsJson = $$"""[{"kind":"ask","slot":"note","prompt":"What note?"},{"kind":"mutate","skill":"{{MutateSkill}}"}]""",
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
            .Returns(new List<AgentRecipe> { AskThenMutateRecipe });
        _recipeRepository.GetByNameAsync(AskThenMutateRecipe.Name, Arg.Any<CancellationToken>())
            .Returns(AskThenMutateRecipe);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _skillBridge = Substitute.For<ILLMSkillBridge>();

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

        // A null default agent leaves the executor's skill cache empty, so GetExecutionTypeAsync
        // returns null — neither UiPassthrough nor FrontendOnly — and every call reaches the bridge.
        var agentRepository = Substitute.For<IAgentRepository>();
        agentRepository.GetDefaultAgentAsync().Returns((Agent?)null);

        var functionExecutor = new LLMFunctionExecutor(
            Substitute.For<ILogger<LLMFunctionExecutor>>(),
            Substitute.For<IAgentSkillRepository>(),
            agentRepository,
            Substitute.For<IPendingConfirmationStore>(),
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
            pendingConfirmationStore: Substitute.For<IPendingConfirmationStore>(),
            recipeEngine: recipeEngine,
            slotExtractor: new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!);
    }

    private static MultiTurnContext BuildContext(ILLMProvider provider) => new(
        new LLMContext
        {
            Message = AskReply,
            UserId = UserId.ToString(),
            AvailableFunctions = new List<LLMFunction> { new() { Name = MutateSkill } }
        },
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew());

    private void ResumeAtAskStep() =>
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = AskThenMutateRecipe.Name,
            AwaitingConfirmation = false,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

    private void BridgeReturns(SkillBridgeResult result) =>
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<BridgeLLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private void GateHoldsTheStep() => BridgeReturns(new SkillBridgeResult
    {
        Success = false,
        ResultType = nameof(SkillResultType.Confirmation),
        Message = "User confirmation required."
    });

    private static ILLMProvider ProviderCallingTheMutateStepThenStopping()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new LLMProviderResponse
                {
                    Success = true,
                    Content = string.Empty,
                    FunctionCalls = new List<BridgeLLMFunctionCall>
                    {
                        new() { FunctionName = MutateSkill, Parameters = new Dictionary<string, object>() }
                    }
                },
                _ => new LLMProviderResponse
                {
                    Success = true,
                    Content = "Soll ich die Notiz wirklich speichern?",
                    FunctionCalls = new List<BridgeLLMFunctionCall>()
                });
        return provider;
    }

    [Test]
    public async Task GateHeldRecipeStep_ClearsThePendingRecipe_SoTheNextTurnCannotResumeAStaleAskStep()
    {
        ResumeAtAskStep();
        GateHoldsTheStep();

        await _service.ExecuteMultiTurnLoopAsync(BuildContext(ProviderCallingTheMutateStepThenStopping()));

        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
        _pendingRecipeStore.DidNotReceive().Save(Arg.Any<PendingRecipe>());
    }

    [Test]
    public async Task GateHeldRecipeStep_TellsTheModelTheFlowEnded()
    {
        ResumeAtAskStep();
        GateHoldsTheStep();
        var provider = ProviderCallingTheMutateStepThenStopping();

        await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        await provider.Received().ProcessAsync(
            Arg.Is<LLMProviderRequest>(r => r.Message.Contains(
                Klacks.Api.Domain.Constants.RecipeEngineDefaults.GateHoldEndsRecipeNote, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SucceedingRecipeStep_IsStillObservedAndAdvancesTheFlow()
    {
        // Counterpart: the deactivation must be caused by the gate hold, not by every recipe turn.
        // A forced step that succeeds is observed normally and the flow runs to its end.
        ResumeAtAskStep();
        BridgeReturns(new SkillBridgeResult { Success = true, ResultType = "Data", Message = "Note added." });

        var (_, _, _, allFunctionCalls, _) = await _service.ExecuteMultiTurnLoopAsync(
            BuildContext(ProviderCallingTheMutateStepThenStopping()));

        allFunctionCalls.ShouldContain(c => c.FunctionName == MutateSkill && c.Success);
        allFunctionCalls.ShouldNotContain(c => c.RequiresConfirmation);
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }
}
