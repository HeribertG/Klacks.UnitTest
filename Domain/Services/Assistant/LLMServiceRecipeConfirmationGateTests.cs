// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the recipe confirmation gate. The fixture keeps its LLMService name because it still
/// drives the loop; the resolve it exercises lives in TurnPreparationService since 2026-09-16.
/// ResolveOrResumeRecipeAsync resumes a recipe
/// paused on the confirmation question by checking the next message for an affirmation (proceed) or
/// anything else (discard the pending recipe and re-resolve fresh against the same message, with the
/// declined recipe excluded), while a keyword-matched recipe is proposed like a semantic one (it never
/// starts without the user's yes), a recipe whose step skills the user may not call is not offered at all,
/// and an ordinary ask-step resume is unaffected.
/// ExecuteMultiTurnLoopAsync (the non-streaming multi-turn loop) is covered directly too, proving the
/// actual pause behavior: a semantic match asks its confirmation question with no tool available,
/// persists the pending recipe, and stops the turn without any function call; a keyword match with an
/// authored goal translation streams the deterministic proposal without a model call.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Klacks.UnitTest.TestHelpers;
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
    private IAgentSkillRepository _skillRepository = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private RecipeEngineService _recipeEngine = null!;
    private LLMService _service = null!;
    private TurnPreparationService _turnPreparation = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { OnboardRecipe });
        _recipeRepository.GetByNameAsync("onboard-employee", Arg.Any<CancellationToken>())
            .Returns(OnboardRecipe);

        _skillRepository = Substitute.For<IAgentSkillRepository>();
        _skillRepository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string?>());

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([]));
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        provider.GetService(typeof(IAgentSkillRepository)).Returns(_skillRepository);
        provider.GetService(typeof(IRecipeCandidateRetriever)).Returns(new KnowledgeIndexRecipeCandidateRetriever(_retrieval));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());
        _recipeEngine = recipeEngine;

        // Every dependency besides RecipeEngineService/RecipeSlotExtractor is unused by
        // ResolveOrResumeRecipeAsync, and the confirmation branch of ExecuteMultiTurnLoopAsync breaks
        // out of the loop before touching any of them either (verified by reading both method bodies)
        // — null! keeps this test focused on the recipe-resolution seam instead of standing up the
        // whole LLMService graph. IPendingConfirmationStore is a real substitute rather than null!
        // because ResolvePendingConfirmation (an unrelated, pre-existing gate) is consulted for every
        // message that both affirms and is not a mutation intent — a message shape this test does not
        // control for, so a null! store would risk an unrelated NullReferenceException.
        // Both objects are needed since the resolve moved into TurnPreparationService (2026-09-16): the
        // seam tests call it directly, the loop test reaches it through the LLMService that owns it.
        _turnPreparation = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            recipeEngine,
            Substitute.For<IRecipeRunRecorder>(),
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            Substitute.For<IAssistantLastActionStore>(),
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());

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
            recipeEngine: recipeEngine,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!,
            turnPreparation: _turnPreparation,
            turnCompletionRecorder: InertTurnCompletionRecorder.Create(),
            turnState: new TurnRunState());
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

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context("ja"), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

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
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([]));

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldBeNull();
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }

    [Test]
    public async Task PendingConfirmation_OffTopicReply_DiscardsPending_NotOnlyOnExplicitNegation()
    {
        // The gate must not merely check for a negation token — any reply that is not a clear
        // affirmation (including an unrelated question) discards the pending recipe. Only a negation
        // additionally EXCLUDES the recipe from the re-match (see the two tests below).
        SetPendingConfirmation(_pendingRecipeStore);
        var message = "Zeig mir die Gruppenliste.";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([]));

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldBeNull();
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }

    [Test]
    public async Task PendingConfirmation_FullerRerequestWithoutNegation_IsProposedAgain_NotExcluded()
    {
        // The reply is not a yes, but it is no refusal either: it restates the request with the data the flow
        // needs. Excluding the recipe here would dead-end the guided flow the user is asking for.
        SetPendingConfirmation(_pendingRecipeStore);
        var message = "Es sind zwei Leute: onboard Anna Meier und Berta Huber";

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldNotBeNull("a reply without a negation must not exclude the recipe from the re-match");
        plan!.Name.ShouldBe("onboard-employee");
        plan.NeedsConfirmation.ShouldBeTrue();
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }

    [Test]
    public async Task PendingConfirmation_DataReplyNamingTheRecipeSubject_IsNotTakenAsADecline()
    {
        var context = Context("Einstellen: Anna Meier, 1.1.2000");
        SetPendingConfirmation(_pendingRecipeStore);

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldNotBeNull();
        context.RecipeConfirmationDeclined.ShouldBeTrue("the gate was abandoned, which the learning pipeline still needs to know");
    }

    [Test]
    public async Task FreshMessage_KeywordMatch_ResolvesButNeedsConfirmation()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var message = "Bitte onboard einen neuen Mitarbeiter";

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.NeedsConfirmation.ShouldBeTrue("a recipe never starts without the user's yes, whatever found it");
        plan.MatchedByMeaning.ShouldBeFalse();
        plan.CurrentSkill.ShouldBe("create_employee");
    }

    [Test]
    public async Task FreshMessage_KeywordMatch_UserLackingAStepSkillRight_ResolvesNothingAndRemembersTheDenial()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        _skillRepository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string?> { ["create_employee"] = Permissions.CanCreateClients });
        var context = Context("Bitte onboard einen neuen Mitarbeiter");
        context.UserRights = [Permissions.CanViewClients];

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldBeNull();
        context.RecipeDenial.ShouldNotBeNull().MissingRights.ShouldBe([Permissions.CanCreateClients]);
    }

    [Test]
    public async Task PrepareAsync_RecipeRefusedForRights_CarriesAnHonestNoteForTheModel_AndNoPlan()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        _skillRepository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string?> { ["create_employee"] = Permissions.CanCreateClients });
        var context = Context("Bitte onboard einen neuen Mitarbeiter");
        context.UserRights = [Permissions.CanViewClients];

        var preparation = await _turnPreparation.PrepareAsync(
            new TurnPreparationRequest(context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId));

        preparation.Plan.ShouldBeNull();
        preparation.ForceConfirm.ShouldBeFalse();
        preparation.VolatileNote.ShouldNotBeNull();
        preparation.VolatileNote!.ShouldContain("GUIDED FLOW NOT AVAILABLE");
        preparation.VolatileNote.ShouldContain("Onboard a new employee end to end.");
        preparation.VolatileNote.ShouldContain(Permissions.CanCreateClients);
    }

    [Test]
    public async Task PrepareAsync_RecipeWithinTheRights_CarriesNoDenialNote()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);

        var preparation = await _turnPreparation.PrepareAsync(
            new TurnPreparationRequest(Context("Bitte onboard einen neuen Mitarbeiter"), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId));

        preparation.Plan.ShouldNotBeNull();
        preparation.VolatileNote.ShouldBeNull();
    }

    [Test]
    public async Task FreshMessage_KeywordMatch_AdministratorPassesTheRightsCheckForAnyStepSkill()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        _skillRepository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string?> { ["create_employee"] = Permissions.CanEditSettings });
        var context = Context("Bitte onboard einen neuen Mitarbeiter");
        context.UserRights = [Roles.Admin];

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldNotBeNull();
    }

    [Test]
    public async Task PendingConfirmation_Declined_ExcludesThatRecipeFromTheRematchOfTheSameMessage()
    {
        SetPendingConfirmation(_pendingRecipeStore);
        var message = "Nein, lieber onboard ich den Mitarbeiter anders";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([]));

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

        plan.ShouldBeNull("the keyword that raised the declined proposal is still in the message and must not raise it again");
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
    }

    [Test]
    public async Task FreshMessage_SemanticMatch_ResolvesButNeedsConfirmation()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var message = "Kannst du bitte einen komplett neuen Mitarbeiter im System anlegen und alles erledigen?";
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([new RetrievalCandidate(
                new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = "onboard-employee", Text = "irrelevant" },
                0.82)]));

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

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

        var plan = await _turnPreparation.ResolveOrResumeRecipeAsync(
            Context("Verträgt keine Nachtschichten"), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, pendingConfirmationForced: false, CancellationToken.None);

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
        _retrieval.RetrieveAsync(message, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
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

        responseContent.ShouldStartWith("Soll ich den Mitarbeiter anlegen?");
        responseContent.ShouldContain(LlmRepliesFormat.BlockPrefix);
        allFunctionCalls.ShouldBeEmpty();
        iterationsUsed.ShouldBe(1);
        _pendingRecipeStore.Received(1).Save(Arg.Is<PendingRecipe>(p =>
            p.AwaitingConfirmation && p.RecipeName == "onboard-employee"
            && p.UserId == UserId && p.ConversationId == ConversationId));
        await provider.Received(1).ProcessAsync(
            Arg.Is<LLMProviderRequest>(r => r.AvailableFunctions.Count == 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteMultiTurnLoopAsync_KeywordMatch_WithAuthoredGoalTranslation_ProposesWithoutAModelCall()
    {
        var recipe = new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "assign-contract",
            Goal = "Assign a contract to an employee.",
            GoalTranslations = new Dictionary<string, string> { ["de"] = "Einem Mitarbeiter einen Vertrag zuweisen" },
            TriggerJson = """{"allOf":[{"anyWordStart":["vertrag zuweisen"]}],"noneOf":[]}""",
            StepsJson = """[{"kind":"mutate","skill":"assign_contract"}]""",
            IsEnabled = true,
        };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { recipe });
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var provider = Substitute.For<ILLMProvider>();
        var context = new LLMContext
        {
            Message = "Bitte Vertrag zuweisen",
            UserId = UserId.ToString(),
            Language = "de",
            AvailableFunctions = new List<LLMFunction>()
        };

        var (responseContent, _, _, allFunctionCalls, askedSlot) =
            await _service.ExecuteMultiTurnLoopAsync(BuildContext(context, provider));

        responseContent.ShouldContain("Einem Mitarbeiter einen Vertrag zuweisen");
        responseContent.ShouldEndWith("\"Nein=no\"]");
        responseContent.ShouldContain(LlmRepliesFormat.BlockPrefix);
        allFunctionCalls.ShouldBeEmpty();
        askedSlot.ShouldBeNull("the turn ended on the proposal, not on the recipe's first ask");
        await provider.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);
        _pendingRecipeStore.Received(1).Save(Arg.Is<PendingRecipe>(p =>
            p.AwaitingConfirmation && p.RecipeName == "assign-contract"));
    }

    [Test]
    public async Task ExecuteMultiTurnLoopAsync_KeywordMatch_WithoutGoalTranslationForTheLanguage_LetsTheModelPhraseTheProposal()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "Soll ich den Mitarbeiter einstellen?" });
        var context = new LLMContext
        {
            Message = "Bitte onboard einen neuen Mitarbeiter",
            UserId = UserId.ToString(),
            Language = "de",
            AvailableFunctions = new List<LLMFunction>()
        };

        var (responseContent, _, _, _, _) = await _service.ExecuteMultiTurnLoopAsync(BuildContext(context, provider));

        responseContent.ShouldStartWith("Soll ich den Mitarbeiter einstellen?");
        await provider.Received(1).ProcessAsync(
            Arg.Is<LLMProviderRequest>(r => r.VolatileSystemPrompt!.Contains("matches a guided flow")
                && !r.VolatileSystemPrompt.Contains("by meaning")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteMultiTurnLoopAsync_ConfirmedKeywordProposal_ThenPausesOnAskStep_ReturnsAskedSlot()
    {
        // Regression guard for the suggestion-grounding fix: LLMService.ApplySuggestionGroundingAsync
        // relies on this exact return value to know which entity (contract/group) the just-asked
        // question refers to, so a real assign-contract-shaped recipe pausing on its first ask step
        // must surface that step's slot name, not just an ask having happened. The recipe is reached
        // through its proposal now: the user's "ja" releases the first ask.
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
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = "assign-contract",
            AwaitingConfirmation = true,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

        var message = "ja";
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
