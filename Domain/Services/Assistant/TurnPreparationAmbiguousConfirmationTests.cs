// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A reply made of a word that languages disagree about (Polish "tak" is a yes, Malay "tak" a no) must neither
/// redeem a held action nor abort a recipe gate as if the user had refused. The UI language settles it where it can
/// (a Polish UI confirms, a Malay UI refuses); anywhere else the turn is not forced, a held gate-replay or stale
/// scenario token stays alive, the model is told to ask for an unambiguous yes or no, and a paused recipe gate stays
/// pending and is asked again. The real language packs and the generated collision table are installed.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationAmbiguousConfirmationTests
{
    private const string PendingSkillName = "delete_system_user";
    private const string PendingToken = "token-abc";
    private const string UndoToken = "token-undo";
    private const string ConversationId = "conv-ambiguous";
    private const string RecipeName = "seal-customer-order";
    private const string TakWord = "tak";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly AgentRecipe GatedRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = RecipeName,
        Goal = "Seal a customer order.",
        TriggerJson = """{"allOf":[],"noneOf":[]}""",
        StepsJson =
            "[{\"kind\":\"ask\",\"slot\":\"orderName\",\"prompt\":\"Which order should be sealed?\"}," +
            "{\"kind\":\"mutate\",\"skill\":\"seal_order\",\"inject\":{\"name\":\"$orderName\"}}]",
        IsEnabled = true,
    };

    private IPendingConfirmationStore _confirmationStore = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IRecipeRunRecorder _recipeRunRecorder = null!;
    private TurnPreparationService _service = null!;

    [OneTimeSetUp]
    public void InstallThePacks() => ShippedConversationSignals.Install();

    [OneTimeTearDown]
    public void UninstallThePacks() => ShippedConversationSignals.Uninstall();

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { GatedRecipe });
        recipeRepository.GetByNameAsync(RecipeName, Arg.Any<CancellationToken>()).Returns(GatedRecipe);

        _confirmationStore = Substitute.For<IPendingConfirmationStore>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _recipeRunRecorder = Substitute.For<IRecipeRunRecorder>();

        var scope = Substitute.For<IServiceScope>();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepositoryStub = Klacks.UnitTest.TestHelpers.SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepositoryStub);
        scopedProvider.GetService(typeof(IKnowledgeRetrievalService)).Returns(Substitute.For<IKnowledgeRetrievalService>());
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _service = new TurnPreparationService(
            _confirmationStore,
            new RecipeEngineService(scopeFactory, _pendingRecipeStore, Substitute.For<ILogger<RecipeEngineService>>()),
            _recipeRunRecorder,
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            Substitute.For<IAssistantLastActionStore>(),
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());
    }

    private void Hold(string purpose, string token = PendingToken) =>
        _confirmationStore
            .PeekLatestForUser(UserId, Arg.Any<TimeSpan>(), purpose)
            .Returns(new PendingConfirmationHandle(token, PendingSkillName));

    private static LLMContext Context(string message, string? language) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        Language = language,
        AvailableFunctions = [new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }]
    };

    [TestCase("pl")]
    [TestCase("pl-PL")]
    public void APolishUi_ConfirmsTheHeldActionWithTak(string language)
    {
        Hold(PendingConfirmationPurposes.GateReplay);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(TakWord, language));

        force.ShouldBeTrue();
        confirmFunction!.Name.ShouldBe(AutonomyDefaults.ConfirmPendingActionSkillName);
        note.ShouldContain(PendingToken);
    }

    [TestCase("de")]
    [TestCase("en")]
    [TestCase("")]
    [TestCase(null)]
    public void AnUiThatDoesNotSettleTak_DoesNotForceButTellsTheModelToAskAgain(string? language)
    {
        Hold(PendingConfirmationPurposes.GateReplay);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(TakWord, language));

        force.ShouldBeFalse();
        confirmFunction.ShouldBeNull();
        note.ShouldNotBeNull();
        note.ShouldContain($"'{TakWord}'");
        note.ShouldContain("ms, pl");
        note.ShouldContain("language of the interface");
        note.ShouldContain("unambiguous yes or no");
        note.ShouldNotContain(PendingToken);
    }

    [Test]
    public void AMalayUi_ReadsTakAsARefusal_NeitherForcedNorNoted()
    {
        Hold(PendingConfirmationPurposes.GateReplay);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(TakWord, "ms"));

        force.ShouldBeFalse();
        confirmFunction.ShouldBeNull();
        note.ShouldBeNull();
    }

    [Test]
    public void AnAmbiguousReplyWithNothingHeld_AddsNoNote()
    {
        _confirmationStore
            .PeekLatestForUser(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<string>())
            .Returns((PendingConfirmationHandle?)null);

        var (force, _, note) = _service.ResolvePendingConfirmation(Context(TakWord, "de"));

        force.ShouldBeFalse();
        note.ShouldBeNull();
    }

    [Test]
    public void AnAmbiguousReply_AnswersNothing_SoAHeldCorrectionUndoIsKeptAndTheModelAsksAgain()
    {
        Hold(PendingConfirmationPurposes.CorrectionUndo, UndoToken);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(TakWord, "de"));

        force.ShouldBeFalse();
        confirmFunction.ShouldBeNull();
        note.ShouldNotBeNull();
        note.ShouldContain("unambiguous yes or no");
        note.ShouldContain("language of the interface");
        note.ShouldNotContain(UndoToken);
        _confirmationStore.DidNotReceive().DiscardCorrectionUndo(Arg.Any<Guid>());
    }

    [Test]
    public void AnAmbiguousReplyWithNoUndoHeld_StillLeavesTheUndoSlotAlone()
    {
        _confirmationStore
            .PeekLatestForUser(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<string>())
            .Returns((PendingConfirmationHandle?)null);

        var (_, _, note) = _service.ResolvePendingConfirmation(Context(TakWord, "de"));

        note.ShouldBeNull();
        _confirmationStore.DidNotReceive().DiscardCorrectionUndo(Arg.Any<Guid>());
    }

    [Test]
    public void AnAmbiguousReply_NeverLooksAtTheUndoThisVeryTurnOffered()
    {
        Hold(PendingConfirmationPurposes.CorrectionUndo, UndoToken);
        var context = Context(TakWord, "de");
        context.CorrectionUndoOffered = true;

        var (force, _, note) = _service.ResolvePendingConfirmation(context);

        force.ShouldBeFalse();
        note.ShouldBeNull();
        _confirmationStore.DidNotReceive().DiscardCorrectionUndo(Arg.Any<Guid>());
    }

    [Test]
    public void ANonConfirmingReply_StillDiscardsTheCorrectionUndo()
    {
        Hold(PendingConfirmationPurposes.CorrectionUndo, UndoToken);

        var (force, _, note) = _service.ResolvePendingConfirmation(Context("mach etwas anderes", "de"));

        force.ShouldBeFalse();
        note.ShouldBeNull();
        _confirmationStore.Received(1).DiscardCorrectionUndo(UserId);
    }

    [Test]
    public void ARealYesAfterTheAmbiguousQuestion_RedeemsTheKeptUndo()
    {
        Hold(PendingConfirmationPurposes.CorrectionUndo, UndoToken);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context("ja", "de"));

        force.ShouldBeTrue();
        confirmFunction.ShouldNotBeNull();
        note.ShouldNotBeNull();
        note.ShouldContain(UndoToken);
    }

    [Test]
    public void AnAmbiguousReply_AlsoCoversAHeldStaleScenarioOverwrite()
    {
        Hold(PendingConfirmationPurposes.StaleScenarioOverwrite);

        var (force, _, note) = _service.ResolvePendingConfirmation(Context(TakWord, "de"));

        force.ShouldBeFalse();
        note.ShouldNotBeNull();
        note.ShouldContain("unambiguous yes or no");
    }

    [Test]
    public async Task APausedRecipeGate_StaysPendingAndCarriesTheNote_WhenTheReplyIsAmbiguous()
    {
        var context = Context(TakWord, "de");
        ResumeAwaitingConfirmation();

        var plan = await Resolve(context);

        plan.ShouldNotBeNull();
        plan.NeedsConfirmation.ShouldBeTrue();
        plan.ConfirmationInstruction.ShouldContain("unambiguous yes or no");
        context.RecipeConfirmationAccepted.ShouldBeFalse();
        context.RecipeConfirmationDeclined.ShouldBeFalse();
        await _recipeRunRecorder.DidNotReceiveWithAnyArgs()
            .AbortRunningAsync(default!, default, default!, default!, default);
    }

    [Test]
    public async Task APausedRecipeGate_IsClearedByAPolishTak_AndAbandonedByAMalayTak()
    {
        var polish = Context(TakWord, "pl");
        ResumeAwaitingConfirmation();
        var plan = await Resolve(polish);
        plan.ShouldNotBeNull();
        plan.NeedsConfirmation.ShouldBeFalse();
        plan.AmbiguousReplyNote.ShouldBeNull();
        polish.RecipeConfirmationAccepted.ShouldBeTrue();

        var malay = Context(TakWord, "ms");
        ResumeAwaitingConfirmation();
        await Resolve(malay);
        malay.RecipeConfirmationDeclined.ShouldBeTrue();
        await _recipeRunRecorder.Received(1).AbortRunningAsync(
            RecipeName, UserId, ConversationId, RecipeAbortReasons.ConfirmationDeclined, Arg.Any<CancellationToken>());
    }

    [Test]
    public void TheAmbiguityNote_IsAppendedToTheConfirmationInstructionOnly()
    {
        var plan = new RecipeExecutionPlan(RecipeName, [], needsConfirmation: true);
        plan.NoteAmbiguousReply("ask again");

        plan.ConfirmationInstruction.ShouldEndWith(" ask again");
        new RecipeExecutionPlan(RecipeName, [], needsConfirmation: true).ConfirmationInstruction.ShouldNotContain("ask again");
    }

    [Test]
    public async Task TheTurnStateKeepsTheAmbiguityNoteApartFromTheForcedConfirmationNote()
    {
        var preparation = Substitute.For<ITurnPreparationService>();
        preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new TurnPreparation(null, false, null, "ambiguous"),
                new TurnPreparation(null, true, new LLMFunction { Name = "confirm_pending_action" }, "forced"));

        async Task<RecipeTurnState> Begin() => await RecipeTurnState.BeginAsync(
            preparation, _recipeRunRecorder, null!, Substitute.For<ILogger>(), Context("tak", "de"),
            Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId, CancellationToken.None);

        var notForced = await Begin();
        notForced.AmbiguousReplyNote.ShouldBe("ambiguous");
        notForced.PendingNote.ShouldBeNull();

        var forced = await Begin();
        forced.PendingNote.ShouldBe("forced");
        forced.AmbiguousReplyNote.ShouldBeNull();
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

    private Task<RecipeExecutionPlan?> Resolve(LLMContext context) =>
        _service.ResolveOrResumeRecipeAsync(
            context, Substitute.For<ILLMProvider>(), new LLMModel(), ConversationId,
            pendingConfirmationForced: false, CancellationToken.None);
}
