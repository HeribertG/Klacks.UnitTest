// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Seam tests for the ask-step correction guard: proves the fourth branch in ResolveOrResumeRecipeAsync
/// actually fires, books the abort against the right run, clears the pending recipe, and does not fill
/// the slot. The detector's own gates are covered in RecipeCorrectionDetectorTests; what is covered here
/// is the wiring, which is where the branch could be silently unreachable - wrong ordering against the
/// cancellation and topic-switch checks, or a plan state the guard never admits.
///
/// The re-resolve is asserted to return null on purpose. Resolving on the correction alone finds nothing
/// when the message opens with a negation and carries no mutation verb, because the engine suppresses the
/// semantic fallback there by design; the intent sits in the message that triggered the recipe and nothing
/// persists it yet. Asserting a found recipe here would assert stage 2 behaviour that stage 1 does not
/// have, and would then hide the moment stage 2 lands.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceRecipeCorrectionTests
{
    private const string ConversationId = "conv-correction";
    private const string RecipeName = "add-extern-employee-to-nearest-group";
    private const string ClientNameSlot = "clientName";
    private const string CancelledDuringAskStep = "cancelled during ask step";

    /// <summary>
    /// The live incident message: 96 characters, opens with a negation, carries no mutation verb.
    /// </summary>
    private const string CorrectionMessage =
        "Nein du hast mich missverstanden, alle Mitarbeitern, Externen und Kunden. Plural nicht singular";

    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary>
    /// Shaped like the real recipe: an ask whose slot is injected into a search step that captures an id,
    /// so the answer has to resolve to exactly one employee. The trigger is deliberately inert - the
    /// tests resume from a pending recipe, and the re-resolve after the abort must not match anything.
    /// </summary>
    private static readonly AgentRecipe ExternNearestGroupLike = new()
    {
        Id = Guid.NewGuid(),
        Name = RecipeName,
        Goal = "Add an external employee to the nearest location group.",
        TriggerJson = """{"allOf":[],"noneOf":[]}""",
        StepsJson =
            "[" +
            "{\"kind\":\"ask\",\"slot\":\"" + ClientNameSlot +
            "\",\"prompt\":\"Which external employee should be added?\"}," +
            "{\"kind\":\"search\",\"skill\":\"search_employees\",\"inject\":{\"searchTerm\":\"$" + ClientNameSlot +
            "\"},\"capture\":\"Array[].Id as clientId\"}," +
            "{\"kind\":\"mutate\",\"skill\":\"add_client_to_nearest_group\",\"inject\":{\"clientId\":\"$clientId\"}}" +
            "]",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IRecipeRunRecorder _recipeRunRecorder = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { ExternNearestGroupLike });
        _recipeRepository.GetByNameAsync(RecipeName, Arg.Any<CancellationToken>())
            .Returns(ExternNearestGroupLike);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _recipeRunRecorder = Substitute.For<IRecipeRunRecorder>();

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

        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: new LLMFunctionExecutor(
                Substitute.For<ILogger<LLMFunctionExecutor>>(),
                Substitute.For<IAgentSkillRepository>(),
                agentRepository,
                Substitute.For<IPendingConfirmationStore>(),
                Substitute.For<ILLMSkillBridge>()),
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            pendingConfirmationStore: Substitute.For<IPendingConfirmationStore>(),
            recipeEngine: recipeEngine,
            recipeRunRecorder: _recipeRunRecorder,
            slotExtractor: new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!);
    }

    private static LLMContext Context(string message, string? language = "de") => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        Language = language,
        AvailableFunctions = new List<LLMFunction>()
    };

    private void ResumeAtClientNameAskStep() =>
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = RecipeName,
            AwaitingConfirmation = false,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

    private Task<RecipeExecutionPlan?> Resolve(string message) =>
        _service.ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

    [Test]
    public async Task Correction_AbortsTheRun_ClearsThePendingRecipe_AndDoesNotFillTheSlot()
    {
        ResumeAtClientNameAskStep();

        var plan = await Resolve(CorrectionMessage);

        plan.ShouldBeNull(
            "stage 1 resolves on the correction alone, which the engine declines by design; the composite " +
            "re-resolve that would find the intended recipe is stage 2");

        await _recipeRunRecorder.Received(1).AbortRunningAsync(
            RecipeName, UserId, ConversationId, RecipeAbortReasons.CorrectedDuringAskStep, Arg.Any<CancellationToken>());
        _pendingRecipeStore.Received(1).Clear(UserId, ConversationId);
        _pendingRecipeStore.DidNotReceiveWithAnyArgs().Save(default!);
    }

    /// <summary>
    /// The regression this branch must not cause: an ordinary short name still fills the slot and the
    /// plan advances to the search step that consumes it.
    /// </summary>
    [Test]
    public async Task OrdinaryEntityAnswer_StillFillsTheSlot_RegressionGuard()
    {
        ResumeAtClientNameAskStep();

        var plan = await Resolve("Müller");

        plan.ShouldNotBeNull();
        plan!.Slots[ClientNameSlot].ShouldBe("Müller");
        plan.CurrentSkill.ShouldBe("search_employees");
        _pendingRecipeStore.DidNotReceive().Clear(Arg.Any<Guid>(), Arg.Any<string>());
        await _recipeRunRecorder.DidNotReceiveWithAnyArgs()
            .AbortRunningAsync(default!, default, default!, default!, default);
    }

    /// <summary>
    /// Ordering against the cancellation check. Both would abort, so the observable difference is the
    /// recorded reason: an explicit cancellation must not be re-labelled as a correction.
    /// </summary>
    [Test]
    public async Task ExplicitCancellation_RecordsTheCancellationReason_NotTheCorrectionReason()
    {
        ResumeAtClientNameAskStep();

        var plan = await Resolve("nein, doch nicht");

        plan.ShouldBeNull();
        await _recipeRunRecorder.Received(1).AbortRunningAsync(
            RecipeName, UserId, ConversationId, CancelledDuringAskStep, Arg.Any<CancellationToken>());
        await _recipeRunRecorder.DidNotReceive().AbortRunningAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            RecipeAbortReasons.CorrectedDuringAskStep, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The branch lives inside the ask-step block, so a plan paused anywhere else is untouched no matter
    /// what the message says. The long-negation-at-a-free-text-slot case is a detector concern and is
    /// covered there; this asserts the seam does not widen beyond the ask step.
    /// </summary>
    [Test]
    public async Task APlanPausedOnAMutateStep_IsNotTouchedByTheCorrectionBranch()
    {
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = RecipeName,
            AwaitingConfirmation = false,
            StepIndex = 2,
            Slots = new Dictionary<string, string> { [ClientNameSlot] = "Müller" }
        });

        var plan = await Resolve("Nein, das ist nicht derselbe Mitarbeiter wie letztes Jahr im Einsatz gewesen");

        plan.ShouldNotBeNull();
        await _recipeRunRecorder.DidNotReceive().AbortRunningAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            RecipeAbortReasons.CorrectedDuringAskStep, Arg.Any<CancellationToken>());
    }
}
