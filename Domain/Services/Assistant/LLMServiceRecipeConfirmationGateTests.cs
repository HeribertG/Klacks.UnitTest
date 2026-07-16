// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMService's recipe confirmation gate: ResolveOrResumeRecipeAsync resumes a recipe
/// paused on the confirmation question by checking the next message for an affirmation (proceed) or
/// anything else (discard the pending recipe and re-resolve fresh against the same message), while a
/// keyword-matched recipe still starts directly and an ordinary ask-step resume is unaffected.
/// ExecuteMultiTurnLoopAsync (the non-streaming multi-turn loop) is covered directly too, proving the
/// actual pause behavior: a semantic match asks its confirmation question with no tool available,
/// persists the pending recipe, and stops the turn without any function call.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProviderLLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderLLMUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceRecipeConfirmationGateTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string ConversationId = "conv-1";

    private static readonly AgentRecipe OnboardRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "onboard-employee",
        Goal = "Onboard a new employee end to end.",
        TriggerJson = """{"allOf":[{"anyWordStart":["onboard","einstell"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe });
        _recipeRepository.GetByNameAsync("onboard-employee", Arg.Any<CancellationToken>())
            .Returns(OnboardRecipe);

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IKnowledgeRetrievalService)).Returns(_retrieval);
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        provider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        // Every dependency besides RecipeEngineService/RecipeSlotExtractor is unused by
        // ResolveOrResumeRecipeAsync, and the confirmation branch of ExecuteMultiTurnLoopAsync breaks
        // out of the loop before touching any of them either (verified by reading both method bodies)
        // — null! keeps this test focused on the recipe-resolution seam instead of standing up the
        // whole LLMService graph. IPendingConfirmationStore is a real substitute rather than null!
        // because ResolvePendingConfirmation (an unrelated, pre-existing gate) is consulted for every
        // message that both affirms and is not a mutation intent — a message shape this test does not
        // control for, so a null! store would risk an unrelated NullReferenceException.
        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: null!,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            pendingConfirmationStore: Substitute.For<IPendingConfirmationStore>(),
            recipeEngine: recipeEngine,
            slotExtractor: new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            suggestionEntityNameReader: null!);
    }

    private static LLMContext Context(string message) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
    };

    private static void SetPendingConfirmation(IPendingRecipeStore store) =>
        store.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = "onboard-employee",
            AwaitingConfirmation = true,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

    [Test]
    public async Task PendingConfirmation_AffirmativeReply_ProceedsAndClearsNeedsConfirmation()
    {
        SetPendingConfirmation(_pendingRecipeStore);

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context("ja"), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeFalse();
        plan.CurrentSkill.ShouldBe("create_employee");
        _pendingRecipeStore.DidNotReceive().Clear(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task PendingConfirmation_RejectionReply_DiscardsPendingAndFallsThroughToNoMatch()
    {
        SetPendingConfirmation(_pendingRecipeStore);
        var message = "Nein, das brauche ich nicht.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldBeNull();
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }

    [Test]
    public async Task PendingConfirmation_OffTopicReply_DiscardsPending_NotOnlyOnExplicitNegation()
    {
        // The gate must not merely check for a negation token — any reply that is not a clear
        // affirmation (including an unrelated question) discards the pending recipe.
        SetPendingConfirmation(_pendingRecipeStore);
        var message = "Zeig mir die Gruppenliste.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldBeNull();
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }

    [Test]
    public async Task FreshMessage_KeywordMatch_ResolvesWithoutConfirmation()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var message = "Bitte onboard einen neuen Mitarbeiter";

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeFalse();
        plan.CurrentSkill.ShouldBe("create_employee");
    }

    [Test]
    public async Task FreshMessage_SemanticMatch_ResolvesButNeedsConfirmation()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([new RetrievalCandidate(
                new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "onboard-employee", Text = "irrelevant" },
                0.82)]));

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeTrue();
    }

    [Test]
    public async Task OrdinaryAskResume_IsUnaffectedByTheConfirmationGate()
    {
        // Regression guard: a recipe paused on a normal ask step (AwaitingConfirmation = false) must
        // keep filling the current slot from the reply, exactly as before this feature existed.
        var askThenMutateRecipe = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "add-employee-note",
            Goal = "Add a note to an employee.",
            TriggerJson = """{"allOf":[{"anyWordStart":["notiz"]}],"noneOf":[]}""",
            StepsJson = """[{"kind":"ask","slot":"note","prompt":"What note?"},{"kind":"mutate","skill":"add_employee_note"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetByNameAsync("add-employee-note", Arg.Any<CancellationToken>())
            .Returns(askThenMutateRecipe);
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = "add-employee-note",
            AwaitingConfirmation = false,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

        var plan = await _service.ResolveOrResumeRecipeAsync(
            Context("Verträgt keine Nachtschichten"), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeFalse();
        plan.Slots["note"].ShouldBe("Verträgt keine Nachtschichten");
        plan.CurrentSkill.ShouldBe("add_employee_note");
    }

    private static MultiTurnContext BuildContext(LLMContext context, ILLMProvider provider) => new(
        context,
        new LLMModel(),
        provider,
        SystemPrompt: "system prompt",
        TruncatedHistory: new List<ProviderLLMMessage>(),
        TotalUsage: new ProviderLLMUsage(),
        Conversation: new LLMConversation { ConversationId = ConversationId },
        Stopwatch: Stopwatch.StartNew());

    [Test]
    public async Task ExecuteMultiTurnLoopAsync_SemanticMatch_AsksConfirmation_PersistsPending_NoToolCall()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([new RetrievalCandidate(
                new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "onboard-employee", Text = "irrelevant" },
                0.82)]));

        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Soll ich den Mitarbeiter anlegen?" });

        var context = new LLMContext
        {
            Message = message,
            UserId = UserId.ToString(),
            AvailableFunctions = new List<LLMFunction> { new() { Name = "create_employee" } }
        };

        var (responseContent, _, iterationsUsed, allFunctionCalls, _) =
            await _service.ExecuteMultiTurnLoopAsync(BuildContext(context, provider));

        responseContent.ShouldBe("Soll ich den Mitarbeiter anlegen?");
        allFunctionCalls.ShouldBeEmpty();
        iterationsUsed.ShouldBe(1);
        _pendingRecipeStore.Received(1).Save(Arg.Is<PendingRecipe>(p =>
            p.AwaitingConfirmation && p.RecipeName == "onboard-employee"
            && p.UserId == UserId && p.ConversationId == ConversationId));
        await provider.Received(1).ProcessAsync(
            Arg.Is<LLMProviderRequest>(r => r.AvailableFunctions.Count == 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteMultiTurnLoopAsync_KeywordMatch_PausesOnAskStep_ReturnsAskedSlot()
    {
        // Regression guard for the suggestion-grounding fix: LLMService.ApplySuggestionGroundingAsync
        // relies on this exact return value to know which entity (contract/group) the just-asked
        // question refers to, so a real assign-contract-shaped recipe pausing on its first ask step
        // must surface that step's slot name, not just an ask having happened.
        var assignContractRecipe = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "assign-contract",
            Goal = "Assign a contract to an employee.",
            TriggerJson = """{"allOf":[{"anyWordStart":["vertrag zuweisen"]}],"noneOf":[]}""",
            StepsJson = """[{"kind":"ask","slot":"contractName","prompt":"Welchen Vertrag?"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe, assignContractRecipe });
        _recipeRepository.GetByNameAsync("assign-contract", Arg.Any<CancellationToken>())
            .Returns(assignContractRecipe);
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);

        var message = "Bitte Vertrag zuweisen";
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Welchen Vertrag möchtest du zuweisen?" });

        var context = new LLMContext
        {
            Message = message,
            UserId = UserId.ToString(),
            AvailableFunctions = new List<LLMFunction>()
        };

        var (responseContent, _, _, allFunctionCalls, askedSlot) =
            await _service.ExecuteMultiTurnLoopAsync(BuildContext(context, provider));

        responseContent.ShouldBe("Welchen Vertrag möchtest du zuweisen?");
        allFunctionCalls.ShouldBeEmpty();
        askedSlot.ShouldBe("contractName");
    }
}
