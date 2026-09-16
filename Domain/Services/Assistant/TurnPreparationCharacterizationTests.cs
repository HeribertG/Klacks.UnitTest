// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Characterization of the turn-preparation block before and after it leaves LLMService: a recipe
/// resumed on an ask step, an affirmation clearing the confirmation gate, a rejection discarding it, an
/// explicit cancellation during an ask step, and a correction during an ask step re-resolving on the
/// composite. These tests describe today's behaviour, not a wish - if one of them turns red during the
/// move, the move changed behaviour and must be undone, not the test.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationCharacterizationTests
{
    private const string ConversationId = "conv-1";
    private const string RecipeName = "add-employee-to-group";
    private const string AskSlot = "groupName";

    private readonly Guid _userId = Guid.NewGuid();

    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private IRecipeRunRecorder _runRecorder = null!;
    private IAgentRecipeRepository _recipeRepository = null!;
    private RecipeEngineService _recipeEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _confirmationStore = Substitute.For<IPendingConfirmationStore>();
        _runRecorder = Substitute.For<IRecipeRunRecorder>();
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        _recipeRepository.GetByNameAsync(RecipeName, Arg.Any<CancellationToken>()).Returns(Recipe());

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(_recipeRepository);
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _recipeEngine = new RecipeEngineService(
            scopeFactory, _pendingRecipeStore, NullLogger<RecipeEngineService>.Instance);
    }

    private static AgentRecipe Recipe() => new()
    {
        Name = RecipeName,
        IsEnabled = true,
        Goal = "Add an employee to a group.",
        TriggerJson = "{\"allOf\":[\"mitarbeiter\",\"gruppe\"]}",
        StepsJson = "[{\"kind\":\"ask\",\"slot\":\"" + AskSlot + "\",\"prompt\":\"Which group?\"}]"
    };

    private void PendingOnAsk(bool awaitingConfirmation = false)
    {
        _pendingRecipeStore.Peek(_userId, ConversationId).Returns(new PendingRecipe
        {
            UserId = _userId,
            ConversationId = ConversationId,
            RecipeName = RecipeName,
            StepIndex = 0,
            Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(RecipeEngineDefaults.PendingRecipeTtlMinutes),
            AwaitingConfirmation = awaitingConfirmation,
            TriggerMessage = "Trag alle Mitarbeitenden in die Gruppe ein."
        });
    }

    private LLMContext Context(string message) => new()
    {
        Message = message,
        UserId = _userId.ToString(),
        ConversationId = ConversationId,
        Language = "de",
        AvailableFunctions = [new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }]
    };

    private Task<RecipeExecutionPlan?> Resolve(string message) =>
        Subject().ResolveOrResumeRecipeAsync(
            Context(message), Substitute.For<ILLMProvider>(), new LLMModel { ApiModelId = "m" },
            ConversationId, CancellationToken.None);

    // The single seam of this fixture. Before the move it returned the LLMService; now it returns the
    // service the block lives in. Every assertion above is unchanged, which is the proof that the move
    // changed no behaviour.
    private TurnPreparationService Subject() => new(
        _confirmationStore,
        _recipeEngine,
        _runRecorder,
        new RecipeSlotExtractor(NullLogger<RecipeSlotExtractor>.Instance),
        Substitute.For<IAssistantLastActionStore>(),
        NullLogger<TurnPreparationService>.Instance);

    [Test]
    public async Task ResumedAskStep_RawFillsTheSlotAndKeepsTheRecipe()
    {
        PendingOnAsk();

        var plan = await Resolve("Zürich");

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(RecipeName);
        plan.Slots[AskSlot].ShouldBe("Zürich");
    }

    [Test]
    public async Task ConfirmationGate_Affirmation_ProceedsWithTheSameRecipe()
    {
        PendingOnAsk(awaitingConfirmation: true);

        var plan = await Resolve("ja, bitte");

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(RecipeName);
    }

    [Test]
    public async Task ConfirmationGate_Rejection_DiscardsThePendingRecipe()
    {
        PendingOnAsk(awaitingConfirmation: true);

        await Resolve("nein");

        _pendingRecipeStore.Received().Clear(_userId, ConversationId);
    }

    [Test]
    public async Task Cancellation_DuringAnAskStep_EndsTheRecipeWithoutFillingTheSlot()
    {
        PendingOnAsk();

        var plan = await Resolve("abbrechen");

        plan.ShouldBeNull();
        _pendingRecipeStore.Received().Clear(_userId, ConversationId);
        await _runRecorder.Received().AbortRunningAsync(
            RecipeName, _userId, ConversationId, "cancelled during ask step", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Correction_DuringAnAskStep_AbortsWithTheCorrectedReasonAndReResolves()
    {
        PendingOnAsk();

        var plan = await Resolve("Nein, ich meinte nicht die Gruppe, sondern den Vertrag.");

        plan.ShouldBeNull();
        await _runRecorder.Received().AbortRunningAsync(
            RecipeName, _userId, ConversationId, RecipeAbortReasons.CorrectedDuringAskStep, Arg.Any<CancellationToken>());
    }

    [Test]
    public void PendingConfirmation_IsForcedOnAnAffirmationWithinTheWindow()
    {
        _confirmationStore.PeekLatestForUser(_userId, Arg.Any<TimeSpan>(), PendingConfirmationPurposes.GateReplay)
            .Returns(new PendingConfirmationHandle("token-1", "delete_group"));

        var (force, function, note) = Subject().ResolvePendingConfirmation(Context("ja"));

        force.ShouldBeTrue();
        function!.Name.ShouldBe(AutonomyDefaults.ConfirmPendingActionSkillName);
        note.ShouldContain("token-1");
    }

    [Test]
    public void PendingConfirmation_WithoutAnAffirmation_DoesNotForce()
    {
        _confirmationStore.PeekLatestForUser(_userId, Arg.Any<TimeSpan>(), PendingConfirmationPurposes.GateReplay)
            .Returns(new PendingConfirmationHandle("token-1", "delete_group"));

        Subject().ResolvePendingConfirmation(Context("lösch die Gruppe Bern")).Force.ShouldBeFalse();
    }
}
