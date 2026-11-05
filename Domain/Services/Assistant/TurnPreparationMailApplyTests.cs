// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The fresh recipe match on a request to enter what e-mails contain. Live 2026-11-04: after the assistant had read the
/// team's mails, "Trag das bitte alles ein: Verfügbarkeiten zu den Verfügbarkeiten, Wünsche als Keywords in den Plan."
/// matched the keyword trigger of record-availability-for-employee and the user was offered a guided flow that asks for
/// data the conversation already holds. With the previous turn's mail read (or a mail word in the message) such a request
/// starts no fresh recipe, whatever the trigger says; the same sentence without that context still does, and a recipe
/// already paused on an ask keeps its answer.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationMailApplyTests
{
    private const string ConversationId = "conv-mail-apply";
    private const string AvailabilityRecipeName = "record-availability-for-employee";
    private const string AskRecipeName = "fill-group";
    private const string Language = "de";
    private const string EnterEverything =
        "Trag das bitte alles ein: Verfügbarkeiten zu den Verfügbarkeiten, Wünsche als Keywords in den Plan.";
    private const string EnterWithMailWord = "Lies die Mails vom Team und trag die Verfügbarkeiten ein.";
    private const string EnterAvailability = "Trag die Verfügbarkeit von Priya Nair ein.";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe AvailabilityRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = AvailabilityRecipeName,
        Goal = "Record the availability of an employee.",
        TriggerJson = """{"allOf":[{"anyWordStart":["trag"]},{"anySubstring":["verfügbar"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"employee","prompt":"Which employee?"},{"kind":"mutate","skill":"set_client_availability"}]""",
        IsEnabled = true,
        SortOrder = 1
    };

    private static readonly AgentRecipe AskRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = AskRecipeName,
        Goal = "Fill a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["füll"]},{"anySubstring":["gruppe"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"groupName","prompt":"Which group?"},{"kind":"mutate","skill":"fill_group"}]""",
        IsEnabled = true,
        SortOrder = 2
    };

    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IAssistantLastActionStore _lastActionStore = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { AvailabilityRecipe, AskRecipe });
        recipeRepository.GetByNameAsync(AskRecipeName, Arg.Any<CancellationToken>()).Returns(AskRecipe);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns((PendingRecipe?)null);
        _lastActionStore = Substitute.For<IAssistantLastActionStore>();

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepository = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepository);
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>());

        _service = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            recipeEngine,
            Substitute.For<IRecipeRunRecorder>(),
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            _lastActionStore,
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());
    }

    [TearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
        DeclineDetector.Reset();
    }

    [Test]
    public async Task EnterEverything_AfterTheMailsWereRead_StartsNoRecipe()
    {
        PreviousTurnRan("list_emails", "read_email");

        var plan = await Resolve(EnterEverything);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task EnterAvailability_AfterTheMailsWereRead_StartsNoRecipe()
    {
        PreviousTurnRan("read_email");

        var plan = await Resolve(EnterAvailability);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task ReadAndEnter_InOneMessage_StartsNoRecipe_WithoutAnyPreviousTurn()
    {
        var plan = await Resolve(EnterWithMailWord);

        plan.ShouldBeNull();
    }

    [Test]
    public async Task TheSameSentence_WithoutAnyMailContext_StillStartsTheKeywordRecipe()
    {
        var plan = await Resolve(EnterEverything);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(AvailabilityRecipeName);
    }

    [Test]
    public async Task EnterAvailability_AfterAnUnrelatedWriteTurn_StillStartsTheKeywordRecipe()
    {
        PreviousTurnRan("add_client_note");

        var plan = await Resolve(EnterAvailability);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(AvailabilityRecipeName);
    }

    [Test]
    public async Task APausedRecipe_KeepsItsAskAnswer_EvenWhenTheAnswerLooksLikeAMailEntry()
    {
        PreviousTurnRan("read_email");
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = AskRecipeName,
            StepIndex = 0,
            AwaitingConfirmation = false,
            Slots = new Dictionary<string, string>()
        });

        var plan = await Resolve("Übernimm alles aus den Mails in die Gruppe Bern");

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(AskRecipeName);
    }

    [Test]
    public async Task AFailingPreviousActionRead_CostsOnlyTheMailContext_NotTheTurn()
    {
        _lastActionStore.Peek(UserId, ConversationId).Returns(_ => throw new InvalidOperationException("store down"));

        var plan = await Resolve(EnterAvailability);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(AvailabilityRecipeName);
    }

    [Test]
    public async Task MailEntryTurn_WithoutAPlan_CarriesTheBatchingNote()
    {
        PreviousTurnRan("read_email");

        var preparation = await Prepare(EnterEverything);

        preparation.Plan.ShouldBeNull();
        preparation.VolatileNote.ShouldBe(MailApplyDefaults.EntryTurnNote);
        preparation.VolatileNote!.ShouldContain("ONE tool-call batch");
    }

    [Test]
    public async Task OrdinaryTurn_CarriesNoMailEntryNote()
    {
        PreviousTurnRan("add_client_note");

        var preparation = await Prepare(EnterEverything);

        preparation.VolatileNote.ShouldBeNull();
        preparation.Plan.ShouldNotBeNull();
    }

    private void PreviousTurnRan(params string[] skillNames) =>
        _lastActionStore.Peek(UserId, ConversationId).Returns(new AssistantLastAction
        {
            UserId = UserId,
            ConversationId = ConversationId,
            UserMessage = "Was steht in den neuen E-Mails vom Team?",
            CreateTimeUtc = DateTime.UtcNow,
            Calls = skillNames.Select(name => new AssistantLastActionCall { SkillName = name, Success = true }).ToList()
        });

    private Task<TurnPreparation> Prepare(string message) =>
        _service.PrepareAsync(new TurnPreparationRequest(
            new LLMContext
            {
                Message = message,
                UserId = UserId.ToString(),
                Language = Language,
                AvailableFunctions = new List<LLMFunction>()
            },
            Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId));

    private Task<RecipeExecutionPlan?> Resolve(string message) =>
        _service.ResolveOrResumeRecipeAsync(
            new LLMContext
            {
                Message = message,
                UserId = UserId.ToString(),
                Language = Language,
                AvailableFunctions = new List<LLMFunction>()
            },
            Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId,
            pendingConfirmationForced: false, CancellationToken.None);
}
