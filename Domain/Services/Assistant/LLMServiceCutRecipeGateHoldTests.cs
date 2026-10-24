// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The hard-coded recipe "dienst-aus-bestellung-schneiden" (create_shift, then cut_shift) ends on the autonomy-gate hold
/// of its cut step, since cutting always needs the user's confirmation (owner decision 2026-10-23): the cut is called once,
/// the forcing is released (the next iteration is neither narrowed to cut_shift nor forced) and the turn ends with the
/// model's question to the user. A forcing that kept pushing the held step would loop until the iteration budget and the
/// user would never see the confirmation question.
/// LLMFunctionExecutor runs for real; the hold is injected one layer deeper through ILLMSkillBridge.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using BridgeLLMFunctionCall = Klacks.Api.Domain.Services.Assistant.Providers.LLMFunctionCall;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceCutRecipeGateHoldTests
{
    private const string ConversationId = "conv-cut-gate-hold";
    private const string ClientId = "11111111-1111-1111-1111-111111111111";
    private const string Question = "Soll ich den Dienst so in drei Teile schneiden?";
    private static readonly string Message =
        $"Erstelle eine 24/7-Bestellung fuer clientId={ClientId} und schneide sie in 3 Dienste auf.";

    private static readonly Guid UserId = Guid.NewGuid();

    private ILLMSkillBridge _skillBridge = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<BridgeLLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<BridgeLLMFunctionCall>().FunctionName == RecipeConstants.CutShiftSkill
                ? new SkillBridgeResult
                {
                    Success = false,
                    ResultType = nameof(SkillResultType.Confirmation),
                    Message = "User confirmation required: 'cut_shift' is classified as Sensitive."
                }
                : new SkillBridgeResult { Success = true, ResultType = "Data", Message = "Created the order. ShiftId=abc" });

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepositoryStub = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepositoryStub);
        var candidateRetriever = new KnowledgeIndexRecipeCandidateRetriever(Substitute.For<IKnowledgeRetrievalService>());
        scopedProvider.GetService(typeof(IRecipeCandidateRetriever)).Returns(candidateRetriever);
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        var recipeEngine = new RecipeEngineService(
            scopeFactory, pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

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
            Message = Message,
            UserId = UserId.ToString(),
            AvailableFunctions = new List<LLMFunction>
            {
                new() { Name = RecipeConstants.CreateShiftSkill },
                new() { Name = RecipeConstants.CutShiftSkill },
                new() { Name = "search_shifts" }
            }
        },
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew());

    private static ILLMProvider ProviderCreatingThenCuttingThenAsking()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Calls(RecipeConstants.CreateShiftSkill),
                _ => Calls(RecipeConstants.CutShiftSkill),
                _ => new LLMProviderResponse { Success = true, Content = Question, FunctionCalls = [] },
                _ => Calls(RecipeConstants.CutShiftSkill));
        return provider;
    }

    private static LLMProviderResponse Calls(string skill) => new()
    {
        Success = true,
        Content = string.Empty,
        FunctionCalls = [new BridgeLLMFunctionCall { FunctionName = skill, Parameters = new Dictionary<string, object>() }]
    };

    [Test]
    public async Task HeldCutStep_IsCalledOnce_ReleasesTheForcing_AndTheTurnEndsWithTheQuestion()
    {
        var provider = ProviderCreatingThenCuttingThenAsking();

        var (_, _, _, allFunctionCalls, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(provider));

        allFunctionCalls.Count(c => c.FunctionName == RecipeConstants.CutShiftSkill).ShouldBe(1);
        allFunctionCalls.ShouldContain(c => c.FunctionName == RecipeConstants.CutShiftSkill && c.RequiresConfirmation);
        var requests = provider.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILLMProvider.ProcessAsync))
            .Select(c => (LLMProviderRequest)c.GetArguments()[0]!)
            .ToList();
        requests.Count.ShouldBe(3, "the turn ends with the model's question instead of forcing the held cut again");
        requests[1].AvailableFunctions.Select(f => f.Name).ShouldBe([RecipeConstants.CutShiftSkill], "the cut step is forced");
        requests[2].ToolChoice.ShouldNotBe(MutationGuardConstants.ToolChoiceRequired);
        requests[2].AvailableFunctions.Count.ShouldBe(3, "after the hold the toolset is no longer narrowed to the cut step");
    }
}
