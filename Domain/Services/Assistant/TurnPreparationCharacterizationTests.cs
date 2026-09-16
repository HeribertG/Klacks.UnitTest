// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Characterization of the turn-preparation block before and after it leaves LLMService: a recipe
/// resumed on an ask step, an affirmation clearing the confirmation gate, a rejection discarding it, an
/// explicit cancellation during an ask step, and a correction during an ask step re-resolving on the
/// composite. These tests describe today's behaviour, not a wish - if one of them turns red during the
/// move, the move changed behaviour and must be undone, not the test.
///
/// The previous-action record is new behaviour rather than moved behaviour, so its one test is stated
/// as an expectation: it is keyed by the conversation id the caller resolved, never by the one the
/// client sent, which is null on the first turn of every new conversation.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationCharacterizationTests
{
    private const string ConversationId = "conv-1";
    private const string ResolvedConversationId = "conv-resolved-by-the-loop";
    private const string RecipeName = "add-employee-to-group";
    private const string AskSlot = "groupName";

    private readonly Guid _userId = Guid.NewGuid();

    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private IRecipeRunRecorder _runRecorder = null!;
    private IAgentRecipeRepository _recipeRepository = null!;
    private IAssistantLastActionStore _lastActionStore = null!;
    private ILogger<TurnPreparationService> _logger = null!;
    private RecipeEngineService _recipeEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _lastActionStore = Substitute.For<IAssistantLastActionStore>();
        _logger = Substitute.For<ILogger<TurnPreparationService>>();
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
        _lastActionStore,
        Substitute.For<IDeterministicRouteProbe>(),
        Substitute.For<ISkillInverseResolver>(),
        _logger);

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

    /// <summary>
    /// The first turn of a new conversation: the client sent no id, so the context carries none, and the
    /// id the loop resolved is the only one there is. Reading the context here would drop the anchor of
    /// exactly the turn a user is most likely to correct.
    /// </summary>
    [Test]
    public void RecordLastAction_IsKeyedByTheResolvedConversationId_NotTheClientOne()
    {
        var context = Context("Trag Müller in die Gruppe Bern ein");
        context.ConversationId = null;

        Subject().RecordLastAction(
            context,
            ResolvedConversationId,
            "Erledigt.",
            [new LLMFunctionCall { FunctionName = "add_client_to_group", Success = true }],
            recipePaused: false);

        _lastActionStore.Received(1).Save(Arg.Is<AssistantLastAction>(
            action => action.ConversationId == ResolvedConversationId && action.UserId == _userId));
    }

    /// <summary>
    /// A call that ran and failed stays in the record. The user corrects what the assistant DID, and a
    /// failed attempt is just as much an interpretation of their request as a successful one; dropping
    /// it would leave the turn without an anchor exactly when the assistant got it wrong. Success is
    /// carried so the undo path can offer a rollback only for a write that actually landed.
    /// </summary>
    [Test]
    public void RecordLastAction_KeepsAFailedCall_AndTheRecordStillAnchors()
    {
        var saved = CaptureSave();

        Record(Call("add_client_to_group", success: false));

        saved().ShouldNotBeNull();
        saved()!.Calls.Count.ShouldBe(1);
        saved()!.Calls[0].Success.ShouldBeFalse();
        saved()!.CanAnchorCorrection(DateTime.UtcNow).ShouldBeTrue();
    }

    /// <summary>
    /// A recipe left waiting on an ask: the user's next message answers the recipe question, so this
    /// turn must not be correctable. The record is devalued rather than replaced.
    /// </summary>
    [Test]
    public void RecordLastAction_APausedRecipe_SupersedesTheRecordAndSavesNothing()
    {
        Record(recipePaused: true, calls: Call("add_client_to_group"));

        _lastActionStore.Received(1).MarkSuperseded(_userId, ResolvedConversationId);
        _lastActionStore.DidNotReceiveWithAnyArgs().Save(default!);
    }

    [Test]
    public void RecordLastAction_WithoutAnyCall_SupersedesInsteadOfReplacing()
    {
        Record();

        _lastActionStore.Received(1).MarkSuperseded(_userId, ResolvedConversationId);
        _lastActionStore.DidNotReceiveWithAnyArgs().Save(default!);
    }

    /// <summary>
    /// Neither a rejected repeat nor a call held for confirmation ever reached a skill, so neither is
    /// something the user could be correcting.
    /// </summary>
    [Test]
    public void RecordLastAction_RejectedRepeatsAndHeldCalls_AreNotRecorded()
    {
        var saved = CaptureSave();

        Record(
            Call("delete_group", rejectedRepeat: true),
            Call("delete_client", requiresConfirmation: true),
            Call("add_client_to_group"));

        saved()!.Calls.Count.ShouldBe(1);
        saved()!.Calls[0].SkillName.ShouldBe("add_client_to_group");
    }

    [Test]
    public void RecordLastAction_OnlyRejectedOrHeldCalls_SupersedeInsteadOfReplacing()
    {
        Record(Call("delete_group", rejectedRepeat: true), Call("delete_client", requiresConfirmation: true));

        _lastActionStore.Received(1).MarkSuperseded(_userId, ResolvedConversationId);
        _lastActionStore.DidNotReceiveWithAnyArgs().Save(default!);
    }

    /// <summary>
    /// A missing anchor costs one correction; a thrown store call would cost the answer the user is
    /// already waiting for. The write is best-effort by design.
    /// </summary>
    [Test]
    public void RecordLastAction_AStoreFailure_IsSwallowedAndLogged()
    {
        _lastActionStore.When(store => store.Save(Arg.Any<AssistantLastAction>()))
            .Do(_ => throw new InvalidOperationException("store is down"));

        Should.NotThrow(() => Record(Call("add_client_to_group")));

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void RecordLastAction_TakesTheSkillLabelFromThisTurnsToolset()
    {
        var saved = CaptureSave();
        var context = ContextWithToolset(Function(
            "add_client_to_group",
            "Adds an employee to a group. Resolves the group by name.",
            "Mitarbeitende einer Gruppe zuweisen"));

        Subject().RecordLastAction(
            context, ResolvedConversationId, "Erledigt.", [Call("add_client_to_group")], recipePaused: false);

        saved()!.Calls[0].SkillDisplayLabel.ShouldBe("Mitarbeitende einer Gruppe zuweisen");
    }

    [Test]
    public void RecordLastAction_TruncatesTheSkillLabelAtItsOwnCap()
    {
        var saved = CaptureSave();
        var label = new string('a', GracefulCorrectionDefaults.SkillDisplayLabelMaxLength + 30);
        var context = ContextWithToolset(Function("add_client_to_group", "Adds an employee to a group.", label));

        Subject().RecordLastAction(
            context, ResolvedConversationId, "Erledigt.", [Call("add_client_to_group")], recipePaused: false);

        saved()!.Calls[0].SkillDisplayLabel!.Length
            .ShouldBe(GracefulCorrectionDefaults.SkillDisplayLabelMaxLength);
    }

    /// <summary>
    /// No label rather than the internal snake_case name, which must never reach a user.
    /// </summary>
    [Test]
    public void RecordLastAction_WithoutTheSkillInTheToolset_StoresNoLabel()
    {
        var saved = CaptureSave();
        var context = ContextWithToolset(Function("some_other_skill", "Does something else."));

        Subject().RecordLastAction(
            context, ResolvedConversationId, "Erledigt.", [Call("add_client_to_group")], recipePaused: false);

        saved()!.Calls[0].SkillDisplayLabel.ShouldBeNull();
        saved()!.Calls[0].SkillLabels.ShouldBeNull();
    }

    /// <summary>
    /// The fail-closed half of the store path: the skill IS in this turn's toolset, but nobody authored a
    /// label for the language the turn ran in. THIS turn resolves no label rather than one in a foreign
    /// language or the internal snake_case name - but the authored dictionary itself still travels with
    /// the record, French entry included, because a later correction in French must still be able to
    /// resolve it.
    /// </summary>
    [Test]
    public void RecordLastAction_WithoutALabelInTheTurnsLanguage_StoresNoResolvedLabel()
    {
        var saved = CaptureSave();
        var context = ContextWithToolset(Function(
            "add_client_to_group",
            "Adds an employee to a group.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["fr"] = "Affecter à un groupe" }));

        Subject().RecordLastAction(
            context, ResolvedConversationId, "Erledigt.", [Call("add_client_to_group")], recipePaused: false);

        saved()!.Calls[0].SkillDisplayLabel.ShouldBeNull();
        saved()!.Calls[0].SkillLabels.ShouldNotBeNull();
    }

    /// <summary>
    /// The authored labels travel with the record even when THIS turn's language is not among them, which
    /// is what lets a correction turn in another language still name the misunderstanding. The resolved
    /// label and the dictionary are not redundant: the first is the noun the assistant used in the answer
    /// the user is correcting, the second is what a differently-languaged correction resolves from.
    /// </summary>
    [Test]
    public void RecordLastAction_StoresTheAuthoredLabelsOfTheToolsetEntry()
    {
        var saved = CaptureSave();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "Mitarbeitende einer Gruppe zuweisen",
            ["fr"] = "Affecter un collaborateur à un groupe"
        };
        var context = ContextWithToolset(Function("add_client_to_group", "Adds an employee to a group.", labels));

        Subject().RecordLastAction(
            context, ResolvedConversationId, "Erledigt.", [Call("add_client_to_group")], recipePaused: false);

        saved()!.Calls[0].SkillDisplayLabel.ShouldBe("Mitarbeitende einer Gruppe zuweisen");
        saved()!.Calls[0].SkillLabels.ShouldBe(labels);
    }

    // The stored label is an AUTHORED label of the toolset entry, resolved in the turn's language
    // (the fixture runs in German), not the skill description any more.
    private static LLMFunction Function(string name, string description, string? germanLabel = null) =>
        Function(
            name,
            description,
            germanLabel == null
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["de"] = germanLabel });

    private static LLMFunction Function(
        string name, string description, IReadOnlyDictionary<string, string>? labels) => new()
    {
        Name = name,
        Description = description,
        Labels = labels
    };

    private LLMContext ContextWithToolset(params LLMFunction[] functions) => new()
    {
        Message = "Trag Müller in die Gruppe Bern ein",
        UserId = _userId.ToString(),
        Language = "de",
        AvailableFunctions = [.. functions]
    };

    private static LLMFunctionCall Call(
        string name,
        bool success = true,
        bool rejectedRepeat = false,
        bool requiresConfirmation = false) => new()
        {
            FunctionName = name,
            Success = success,
            IsRejectedRepeat = rejectedRepeat,
            RequiresConfirmation = requiresConfirmation
        };

    private void Record(params LLMFunctionCall[] calls) => Record(false, calls);

    private void Record(bool recipePaused, params LLMFunctionCall[] calls) =>
        Subject().RecordLastAction(
            Context("Trag Müller in die Gruppe Bern ein"),
            ResolvedConversationId,
            "Erledigt.",
            calls,
            recipePaused);

    private Func<AssistantLastAction?> CaptureSave()
    {
        AssistantLastAction? saved = null;
        _lastActionStore.When(store => store.Save(Arg.Any<AssistantLastAction>()))
            .Do(call => saved = call.Arg<AssistantLastAction>());
        return () => saved;
    }
}
