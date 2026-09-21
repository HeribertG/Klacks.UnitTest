// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression tests for the collision between the pending-confirmation gate and a fresh recipe match.
/// The autonomy gate holds a sensitive skill and asks the user to confirm; the user answers with the
/// action restated ("Ja, Gruppe so anlegen"), which is exactly the phrase a recipe trigger matches. The
/// chat loop stops on a matched recipe's first ask step BEFORE it narrows the turn to
/// confirm_pending_action, so a fresh match on that message swallowed the redemption: the assistant
/// re-asked the recipe's opening question, the held action stayed unexecuted, and the user's next "ja"
/// was raw-filled into that ask slot. Reproduced live on 2026-09-21 with create-group. The suppression is
/// scoped to the FRESH match: resuming a recipe that is already running is a different decision and is
/// covered by LLMServiceRecipeConfirmationGateTests.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationGateReplayRecipeSuppressionTests
{
    private const string ConversationId = "conv-gate-replay";
    private const string ConfirmationMessage = "Ja, Gruppe so anlegen";
    private const string PendingSkillName = "create_group";
    private const string PendingToken = "token-gate-replay";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe CreateGroupRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "create-group",
        Goal = "Create a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["gruppe"]},{"anyWordStart":["anleg","erstell"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"ask","slot":"groupName","prompt":"What is the group called?"}]""",
        IsEnabled = true,
    };

    private IAgentRecipeRepository _recipeRepository = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { CreateGroupRecipe });
        _recipeRepository.GetByNameAsync(CreateGroupRecipe.Name, Arg.Any<CancellationToken>())
            .Returns(CreateGroupRecipe);

        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();

        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        serviceProvider.GetService(typeof(IKnowledgeRetrievalService))
            .Returns(Substitute.For<IKnowledgeRetrievalService>());
        serviceProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _confirmationStore = Substitute.For<IPendingConfirmationStore>();

        _service = new TurnPreparationService(
            _confirmationStore,
            new RecipeEngineService(
                scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>()),
            Substitute.For<IRecipeRunRecorder>(),
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            Substitute.For<IAssistantLastActionStore>(),
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());
    }

    private static LLMContext Context() => new()
    {
        Message = ConfirmationMessage,
        UserId = UserId.ToString(),
        AvailableFunctions =
        [
            new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }
        ]
    };

    private void SetOutstandingGateReplay() =>
        _confirmationStore
            .PeekLatestForUser(UserId, Arg.Any<TimeSpan>(), PendingConfirmationPurposes.GateReplay)
            .Returns(new PendingConfirmationHandle(PendingToken, PendingSkillName));

    private Task<RecipeExecutionPlan?> Resolve(bool pendingConfirmationForced) =>
        _service.ResolveOrResumeRecipeAsync(
            Context(), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId,
            pendingConfirmationForced, CancellationToken.None);

    // Guards the test below against becoming vacuous: the message really does match the recipe trigger,
    // so a null plan in the suppressed case can only come from the suppression.
    [Test]
    public async Task WithoutAnOutstandingConfirmation_TheSameMessageStillMatchesTheRecipe()
    {
        var plan = await Resolve(pendingConfirmationForced: false);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(CreateGroupRecipe.Name);
    }

    [Test]
    public async Task WhileAConfirmationIsBeingRedeemed_NoFreshRecipeIsMatched()
    {
        var plan = await Resolve(pendingConfirmationForced: true);

        plan.ShouldBeNull();
    }

    // Not just "no plan": the matcher must not run at all, or the turn pays for an embedding round and a
    // slot-extraction model call whose result is thrown away.
    [Test]
    public async Task WhileAConfirmationIsBeingRedeemed_TheRecipeMatcherIsNotConsultedAtAll()
    {
        await Resolve(pendingConfirmationForced: true);

        await _recipeRepository.DidNotReceiveWithAnyArgs().GetAllEnabledAsync(Arg.Any<CancellationToken>());
    }

    // The whole point, at the seam the chat loops actually call: the turn is narrowed to
    // confirm_pending_action and carries no recipe that could stop it on an ask step first.
    [Test]
    public async Task PrepareAsync_AnAffirmationThatRestatesTheAction_ForcesTheConfirmationAndCarriesNoPlan()
    {
        SetOutstandingGateReplay();

        var preparation = await _service.PrepareAsync(new TurnPreparationRequest(
            Context(), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId));

        preparation.ForceConfirm.ShouldBeTrue();
        preparation.ConfirmFunction!.Name.ShouldBe(AutonomyDefaults.ConfirmPendingActionSkillName);
        preparation.VolatileNote.ShouldContain(PendingToken);
        preparation.Plan.ShouldBeNull();
    }

    // The suppression follows the force, not the purpose behind it. A rule-3 correction undo is redeemed
    // through the same seam, and an affirmation answering it is just as little a fresh request as one
    // answering an autonomy-gate hold - so it suppresses the match too. Deliberate, and pinned here
    // because the two purposes are otherwise only ever tested apart.
    [Test]
    public async Task PrepareAsync_AnAffirmationRedeemingACorrectionUndo_AlsoCarriesNoPlan()
    {
        _confirmationStore
            .PeekLatestForUser(UserId, Arg.Any<TimeSpan>(), PendingConfirmationPurposes.CorrectionUndo)
            .Returns(new PendingConfirmationHandle(PendingToken, PendingSkillName));

        var preparation = await _service.PrepareAsync(new TurnPreparationRequest(
            Context(), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId));

        preparation.ForceConfirm.ShouldBeTrue();
        preparation.Plan.ShouldBeNull();
    }

    // With no token outstanding the same message is an ordinary request again, so the recipe engages.
    [Test]
    public async Task PrepareAsync_WithNoOutstandingConfirmation_StillEngagesTheRecipe()
    {
        var preparation = await _service.PrepareAsync(new TurnPreparationRequest(
            Context(), Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId));

        preparation.ForceConfirm.ShouldBeFalse();
        preparation.Plan.ShouldNotBeNull();
        preparation.Plan!.Name.ShouldBe(CreateGroupRecipe.Name);
    }
}
