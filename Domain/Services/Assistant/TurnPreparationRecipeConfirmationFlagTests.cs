// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Seam tests for the two confirmation-gate flags on the turn context. The engine is the only place that
/// knows whether a paused recipe's gate was cleared or abandoned this turn, and the learning pipeline must
/// not re-derive that from the message: TrajectoryCaptureService reads
/// LLMContext.RecipeConfirmationAccepted to resolve the preceding turn's pending gate as confirmed, and
/// LLMContext.RecipeConfirmationDeclined to resolve it as declined or redirected. What is covered here is
/// the wiring - each flag is set exactly where the engine takes that decision, never both at once, and
/// neither on a turn that resumed no gate - plus the abort reason the abandoned run is recorded with.
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
public class TurnPreparationRecipeConfirmationFlagTests
{
    private const string ConversationId = "conv-confirmation";
    private const string RecipeName = "seal-customer-order";
    private const string OrderSlot = "orderName";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe GatedRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = RecipeName,
        Goal = "Seal a customer order.",
        TriggerJson = """{"allOf":[],"noneOf":[]}""",
        StepsJson =
            "[" +
            "{\"kind\":\"ask\",\"slot\":\"" + OrderSlot + "\",\"prompt\":\"Which order should be sealed?\"}," +
            "{\"kind\":\"mutate\",\"skill\":\"seal_order\",\"inject\":{\"name\":\"$" + OrderSlot + "\"}}" +
            "]",
        IsEnabled = true,
    };

    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IRecipeRunRecorder _recipeRunRecorder = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { GatedRecipe });
        recipeRepository.GetByNameAsync(RecipeName, Arg.Any<CancellationToken>())
            .Returns(GatedRecipe);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _recipeRunRecorder = Substitute.For<IRecipeRunRecorder>();

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
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

        _service = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            recipeEngine,
            _recipeRunRecorder,
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            Substitute.For<IAssistantLastActionStore>(),
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
    public async Task AnAffirmationClearingTheGate_MarksTheTurnAsAConfirmedGate()
    {
        var context = Context("Ja, bitte");
        ResumeAwaitingConfirmation();

        var plan = await Resolve(context);

        plan.ShouldNotBeNull();
        context.RecipeConfirmationAccepted.ShouldBeTrue();
        context.RecipeConfirmationDeclined.ShouldBeFalse();
    }

    [Test]
    public async Task AReplyThatDoesNotAffirm_LeavesTheTurnWithoutAConfirmedGate()
    {
        var context = Context("Nein");
        ResumeAwaitingConfirmation();

        await Resolve(context);

        context.RecipeConfirmationAccepted.ShouldBeFalse();
        await _recipeRunRecorder.ReceivedWithAnyArgs(1)
            .AbortRunningAsync(default!, default, default!, default!, default);
    }

    // The mirror flag, and the reason it cannot be re-derived downstream: the abandoned gate is the
    // engine's own decision, and a bare refusal is indistinguishable from an ordinary negation once the
    // turn is over. Both shapes of a non-affirmation set it - which of them it was is what
    // TrajectoryCaptureService decides from the message.
    [TestCase("Nein")]
    [TestCase("Nein, zeig mir stattdessen die Kunden")]
    public async Task AReplyThatDoesNotAffirm_MarksTheTurnAsAnAbandonedGate(string message)
    {
        var context = Context(message);
        ResumeAwaitingConfirmation();

        await Resolve(context);

        context.RecipeConfirmationDeclined.ShouldBeTrue();
    }

    // The abort reason is a constant now, not a literal: RecipeRunRecorder.Truncate cuts with a hard
    // slice, so a reason nobody can filter on later is the failure mode RecipeAbortReasonsGuardTests
    // exists for - and it can only guard what the class actually holds.
    [Test]
    public async Task AnAbandonedGate_AbortsTheRunWithTheConfirmationDeclinedReason()
    {
        var context = Context("Nein");
        ResumeAwaitingConfirmation();

        await Resolve(context);

        await _recipeRunRecorder.Received(1).AbortRunningAsync(
            RecipeName,
            UserId,
            ConversationId,
            RecipeAbortReasons.ConfirmationDeclined,
            Arg.Any<CancellationToken>());
    }

    // An ordinary turn that never resumed a gated recipe must leave both flags alone, or the learning
    // pipeline would resolve the preceding gate off a turn that had nothing to do with it.
    [Test]
    public async Task ATurnWithoutAPendingGate_CarriesNeitherGateFlag()
    {
        var context = Context("Wie viele Mitarbeiter haben wir?");

        await Resolve(context);

        context.RecipeConfirmationAccepted.ShouldBeFalse();
        context.RecipeConfirmationDeclined.ShouldBeFalse();
    }

    private void ResumeAwaitingConfirmation() =>
        _pendingRecipeStore.Peek(UserId, ConversationId).Returns(new PendingRecipe
        {
            UserId = UserId,
            ConversationId = ConversationId,
            RecipeName = RecipeName,
            AwaitingConfirmation = true,
            StepIndex = 0,
            Slots = new Dictionary<string, string>()
        });

    private static LLMContext Context(string message) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        Language = "de",
        AvailableFunctions = new List<LLMFunction>()
    };

    private Task<RecipeExecutionPlan?> Resolve(LLMContext context) =>
        _service.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId,
            pendingConfirmationForced: false, CancellationToken.None);
}
