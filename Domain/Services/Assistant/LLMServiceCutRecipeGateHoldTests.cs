// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The hard-coded recipe "dienst-aus-bestellung-schneiden" runs over three turns, because sealing and cutting each need
/// the user's explicit confirmation (create_shift only creates an open order since 2026-10-24, and a held action can only
/// be confirmed in a later turn):
/// turn 1 forces create_shift and then seal_shift on the created order; the gate holds the seal, the forcing is released
/// (the held seal is called once, cut_shift is never called) and the recipe run stays open for the confirmation;
/// turn 2 ("Ja") redeems the seal through confirm_pending_action and then forces cut_shift on the plannable shift the
/// seal created; the gate holds the cut and the turn ends with the model's question;
/// turn 3 only redeems the cut (ordinary confirmation path, not covered here).
/// LLMFunctionExecutor runs for real; the holds and the replay are injected one layer deeper through ILLMSkillBridge.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
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
    private const string OrderId = "33333333-3333-3333-3333-333333333333";
    private const string PlannableShiftId = "44444444-4444-4444-4444-444444444444";
    private const string Token = "seal-token";
    private const string SealQuestion = "Soll ich die Bestellung so versiegeln?";
    private const string CutQuestion = "Soll ich den Dienst so in drei Teile schneiden?";
    private const string SearchShiftsSkill = "search_shifts";
    private static readonly string Message =
        $"Erstelle eine 24/7-Bestellung fuer clientId={ClientId} und schneide sie in 3 Dienste auf: " +
        "07:00-15:00, 15:00-23:00, 23:00-07:00.";

    private static readonly Guid UserId = Guid.NewGuid();

    private ILLMSkillBridge _skillBridge = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private IAssistantLastActionStore _lastActionStore = null!;
    private IRecipeRunRecorder _recorder = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        _skillBridge = Substitute.For<ILLMSkillBridge>();
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Any<BridgeLLMFunctionCall>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => BridgeResult(call.Arg<BridgeLLMFunctionCall>().FunctionName));

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

        _confirmationStore = Substitute.For<IPendingConfirmationStore>();
        _lastActionStore = Substitute.For<IAssistantLastActionStore>();
        _recorder = Substitute.For<IRecipeRunRecorder>();
        _recorder.BeginOrResumeAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new RecipeRunHandle(Guid.NewGuid(), call.ArgAt<string>(0), UserId, ConversationId));

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
            recipeRunRecorder: _recorder,
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!,
            turnPreparation: new TurnPreparationService(
                _confirmationStore,
                recipeEngine,
                Substitute.For<IRecipeRunRecorder>(),
                new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
                _lastActionStore,
                Substitute.For<IDeterministicRouteProbe>(),
                Substitute.For<ISkillInverseResolver>(),
                Substitute.For<ILogger<TurnPreparationService>>()),
            turnCompletionRecorder: InertTurnCompletionRecorder.Create(),
            turnState: new TurnRunState());
    }

    private static SkillBridgeResult BridgeResult(string functionName) => functionName switch
    {
        RecipeConstants.CreateShiftSkill => new SkillBridgeResult
        {
            Success = true,
            ResultType = "Data",
            Message = "Order created as an open order (status OriginalOrder).",
            Data = new { ShiftId = OrderId, Name = "X" }
        },
        RecipeConstants.SealShiftSkill => Held(RecipeConstants.SealShiftSkill),
        RecipeConstants.CutShiftSkill => Held(RecipeConstants.CutShiftSkill),
        AutonomyDefaults.ConfirmPendingActionSkillName => new SkillBridgeResult
        {
            Success = true,
            ResultType = "Data",
            Message = "Order 'X' sealed (status SealedOrder) and its plannable shift was created.",
            Data = new { OrderId, PlannableShiftId, Name = "X", Verified = true },
            ReplayedSkillName = RecipeConstants.SealShiftSkill
        },
        _ => new SkillBridgeResult { Success = true, ResultType = "Data", Message = "ok" }
    };

    private static SkillBridgeResult Held(string skill) => new()
    {
        Success = false,
        ResultType = nameof(SkillResultType.Confirmation),
        Message = $"User confirmation required: '{skill}' is classified as Sensitive."
    };

    private static MultiTurnContext BuildContext(ILLMProvider provider, string message, params string[] functions) => new(
        new LLMContext
        {
            Message = message,
            UserId = UserId.ToString(),
            Language = "de",
            ConversationId = ConversationId,
            AvailableFunctions = functions.Select(name => new LLMFunction { Name = name }).ToList()
        },
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew());

    private static ILLMProvider Provider(params Func<LLMProviderResponse>[] answers)
    {
        var provider = Substitute.For<ILLMProvider>();
        var replies = answers.Select(answer => (Func<NSubstitute.Core.CallInfo, LLMProviderResponse>)(_ => answer())).ToArray();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies.Skip(1).ToArray());
        return provider;
    }

    private static LLMProviderResponse Calls(string skill, Dictionary<string, object>? parameters = null) => new()
    {
        Success = true,
        Content = string.Empty,
        FunctionCalls = [new BridgeLLMFunctionCall { FunctionName = skill, Parameters = parameters ?? new Dictionary<string, object>() }]
    };

    private static LLMProviderResponse Answer(string text) =>
        new() { Success = true, Content = text, FunctionCalls = [] };

    private static List<LLMProviderRequest> Requests(ILLMProvider provider) => provider.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(ILLMProvider.ProcessAsync))
        .Select(c => (LLMProviderRequest)c.GetArguments()[0]!)
        .ToList();

    [Test]
    public async Task RecipeTurn_ForcesSealOnTheCreatedOrder_EndsOnItsHold_AndNeverCuts()
    {
        var provider = Provider(
            () => Calls(RecipeConstants.CreateShiftSkill),
            () => Calls(RecipeConstants.SealShiftSkill),
            () => Answer(SealQuestion),
            () => Calls(RecipeConstants.SealShiftSkill));

        var (_, _, _, allFunctionCalls, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(
            provider, Message,
            RecipeConstants.CreateShiftSkill, RecipeConstants.SealShiftSkill, RecipeConstants.CutShiftSkill, SearchShiftsSkill));

        var seal = allFunctionCalls.Where(c => c.FunctionName == RecipeConstants.SealShiftSkill).ToList();
        seal.Count.ShouldBe(1);
        seal[0].RequiresConfirmation.ShouldBeTrue();
        seal[0].Parameters[RecipeConstants.ShiftIdParam].ShouldBe(OrderId, "the created order is injected, not taken from the model");
        allFunctionCalls.ShouldNotContain(c => c.FunctionName == RecipeConstants.CutShiftSkill);

        var requests = Requests(provider);
        requests.Count.ShouldBe(3, "the turn ends with the model's question instead of forcing the held seal again");
        requests[1].AvailableFunctions.Select(f => f.Name).ShouldBe([RecipeConstants.SealShiftSkill], "the seal step is forced");
        requests[1].ToolChoice.ShouldBe(MutationGuardConstants.ToolChoiceRequired);
        requests[2].ToolChoice.ShouldNotBe(MutationGuardConstants.ToolChoiceRequired);
        requests[2].AvailableFunctions.Count.ShouldBe(4, "after the hold the toolset is no longer narrowed");

        await _recorder.DidNotReceive().AbortAsync(Arg.Any<RecipeRunHandle>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _recorder.DidNotReceive().CompleteAsync(Arg.Any<RecipeRunHandle>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecipeTurn_WhoseSealIsRefused_EndsTheForcing_AndRecordsWhy()
    {
        _skillBridge.ExecuteSkillFromLLMCallAsync(
                Arg.Is<BridgeLLMFunctionCall>(c => c.FunctionName == RecipeConstants.SealShiftSkill),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult
            {
                Success = false, ResultType = "Error", Message = "Cannot seal order 'X' yet - missing/invalid: at least one group."
            });
        var provider = Provider(
            () => Calls(RecipeConstants.CreateShiftSkill),
            () => Calls(RecipeConstants.SealShiftSkill),
            () => Answer("Die Bestellung braucht noch eine Gruppe."),
            () => Calls(RecipeConstants.SealShiftSkill));

        await _service.ExecuteMultiTurnLoopAsync(BuildContext(
            provider, Message,
            RecipeConstants.CreateShiftSkill, RecipeConstants.SealShiftSkill, RecipeConstants.CutShiftSkill, SearchShiftsSkill));

        var requests = Requests(provider);
        requests.Count.ShouldBe(3, "a refused seal is not forced again");
        requests[2].ToolChoice.ShouldNotBe(MutationGuardConstants.ToolChoiceRequired);
        await _recorder.Received(1).AbortAsync(
            Arg.Any<RecipeRunHandle>(), RecipeAbortReasons.CutRecipeStoppedAtSeal, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConfirmationTurn_RedeemsTheSeal_ThenForcesCutOnThePlannableShift_AndEndsOnTheCutHold()
    {
        _confirmationStore.PeekLatestForUser(
                UserId, Arg.Any<TimeSpan>(), PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat)
            .Returns(new PendingConfirmationHandle(Token, RecipeConstants.SealShiftSkill));
        _lastActionStore.Peek(UserId, ConversationId).Returns(RecipeTurnRecord());
        var provider = Provider(
            () => Calls(AutonomyDefaults.ConfirmPendingActionSkillName,
                new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = Token }),
            () => Calls(RecipeConstants.CutShiftSkill, new Dictionary<string, object>
            {
                [RecipeConstants.ShiftIdParam] = OrderId,
                ["parts"] = "07:00-15:00,15:00-23:00,23:00-07:00"
            }),
            () => Answer(CutQuestion),
            () => Calls(RecipeConstants.CutShiftSkill));

        var (_, _, _, allFunctionCalls, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(
            provider, "Ja",
            AutonomyDefaults.ConfirmPendingActionSkillName, RecipeConstants.CutShiftSkill, SearchShiftsSkill));

        var cut = allFunctionCalls.Where(c => c.FunctionName == RecipeConstants.CutShiftSkill).ToList();
        cut.Count.ShouldBe(1);
        cut[0].RequiresConfirmation.ShouldBeTrue();
        cut[0].Parameters[RecipeConstants.ShiftIdParam].ShouldBe(PlannableShiftId,
            "cut_shift is pinned to the plannable shift the confirmed seal created");

        var requests = Requests(provider);
        requests.Count.ShouldBe(3);
        requests[0].AvailableFunctions.Select(f => f.Name).ShouldBe([AutonomyDefaults.ConfirmPendingActionSkillName]);
        requests[1].AvailableFunctions.Select(f => f.Name).ShouldBe([RecipeConstants.CutShiftSkill], "the cut step is forced");
        requests[1].ToolChoice.ShouldBe(MutationGuardConstants.ToolChoiceRequired);
        requests[2].ToolChoice.ShouldNotBe(MutationGuardConstants.ToolChoiceRequired);

        await _recorder.Received().BeginOrResumeAsync(
            RecipeConstants.CutFromOrderRecipeName, UserId, ConversationId, Arg.Any<Guid?>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConfirmationTurn_OfASealOutsideTheRecipe_ForcesNothingAfterTheRedemption()
    {
        _confirmationStore.PeekLatestForUser(
                UserId, Arg.Any<TimeSpan>(), PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat)
            .Returns(new PendingConfirmationHandle(Token, RecipeConstants.SealShiftSkill));
        var record = RecipeTurnRecord();
        record.UserMessage = "Versiegle die Bestellung X.";
        _lastActionStore.Peek(UserId, ConversationId).Returns(record);
        var provider = Provider(
            () => Calls(AutonomyDefaults.ConfirmPendingActionSkillName,
                new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = Token }),
            () => Answer("Die Bestellung ist versiegelt."),
            () => Calls(RecipeConstants.CutShiftSkill));

        var (_, _, _, allFunctionCalls, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(
            provider, "Ja",
            AutonomyDefaults.ConfirmPendingActionSkillName, RecipeConstants.CutShiftSkill, SearchShiftsSkill));

        allFunctionCalls.ShouldNotContain(c => c.FunctionName == RecipeConstants.CutShiftSkill);
        var requests = Requests(provider);
        requests.Count.ShouldBe(2);
        requests[1].ToolChoice.ShouldNotBe(MutationGuardConstants.ToolChoiceRequired);
    }

    private static AssistantLastAction RecipeTurnRecord() => new()
    {
        UserId = UserId,
        ConversationId = ConversationId,
        UserMessage = Message,
        CreateTimeUtc = DateTime.UtcNow,
        Calls =
        [
            new AssistantLastActionCall
            {
                SkillName = RecipeConstants.CreateShiftSkill,
                Success = true,
                ResultDataJson = $"{{\"ShiftId\":\"{OrderId}\",\"Name\":\"X\"}}"
            }
        ]
    };
}
