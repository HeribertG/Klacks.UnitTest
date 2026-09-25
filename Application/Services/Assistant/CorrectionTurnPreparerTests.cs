// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The correction/toolset pipeline both chat entry points share: peek the anchor, plan a correction,
/// assemble the toolset on the composite with the exclusion, complete the correction against the
/// assembled toolset, pin its clarification candidates, and hold its undo offer as a one-time token.
/// Exercised once here, against ICorrectionTurnPreparer directly, instead of once per entry point - the
/// entry points only need a thin wiring test proving they call this and thread its result into their
/// LLMContext identically (see GracefulCorrectionEntryPointWiringTests).
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class CorrectionTurnPreparerTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string ConversationId = "conv-1";
    private const string CorrectionMessage = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";
    private const string PreviousMessage = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.";
    private const string Composite = "composite of both messages";
    private const string ExcludedSkillName = "find_customer_candidates";
    private const string ContextNote = "CORRECTION - the previous turn searched for customers.";

    private const string PinnedSkillName = "add_clients_to_group";

    private const string ClarificationReply =
        "I searched for customers. Did you mean adding clients to a group, or listing them?";
    private const string FirstCandidate = "add_clients_to_group";
    private const string SecondCandidate = "list_group_clients";

    private const string UndoSkillName = "remove_shift_from_group";
    private const string UndoToken = "undo-token";
    private const string UndoneSkillLabel = "Assigns a shift to a group";
    private const string UndoArgumentName = "shiftId";
    private const string InversePermission = Permissions.CanEditSettings;

    private static readonly IReadOnlyDictionary<string, object> UndoArguments =
        new Dictionary<string, object> { [UndoArgumentName] = "shift-1", ["groupId"] = "group-1" };

    private static readonly SkillDescriptor InverseDescriptor = new(
        UndoSkillName,
        "Removes a shift from a group",
        SkillCategory.Crud,
        Array.Empty<SkillParameter>(),
        new[] { InversePermission },
        Array.Empty<LLMCapability>(),
        null);

    private ISkillToolsetAssembler _assembler = null!;
    private ITurnPreparationService _turnPreparation = null!;
    private IAssistantLastActionStore _lastActionStore = null!;
    private IPendingRecipeStore _pendingRecipeStore = null!;
    private IPendingConfirmationStore _pendingConfirmationStore = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillPermissionGate _permissionGate = null!;

    [SetUp]
    public void SetUp()
    {
        _lastActionStore = Substitute.For<IAssistantLastActionStore>();
        _pendingRecipeStore = Substitute.For<IPendingRecipeStore>();
        _pendingConfirmationStore = Substitute.For<IPendingConfirmationStore>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _permissionGate = Substitute.For<ISkillPermissionGate>();

        _assembler = Substitute.For<ISkillToolsetAssembler>();
        _assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new SkillToolsetResult());

        _turnPreparation = Substitute.For<ITurnPreparationService>();
    }

    private AssistantLastAction GivenAStoredAnchor(params string[] clarificationSkillNames)
    {
        var lastAction = new AssistantLastAction
        {
            UserId = Guid.Parse(UserId),
            ConversationId = ConversationId,
            UserMessage = PreviousMessage,
            CreateTimeUtc = DateTime.UtcNow,
            Calls = [new AssistantLastActionCall { SkillName = ExcludedSkillName, Success = true }],
            ClarificationSkillNames = clarificationSkillNames
        };

        _lastActionStore.Peek(Guid.Parse(UserId), ConversationId).Returns(lastAction);
        return lastAction;
    }

    private void GivenACorrectionIsPlanned()
    {
        var lastAction = GivenAStoredAnchor();

        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GracefulCorrectionPlan(
                lastAction, CorrectionMessage, Composite, new[] { ExcludedSkillName }));

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(new GracefulCorrectionOutcome(ContextNote, null, []));
    }

    private void GivenAClarificationIsPlanned()
    {
        var lastAction = GivenAStoredAnchor();

        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GracefulCorrectionPlan(
                lastAction, CorrectionMessage, Composite, new[] { ExcludedSkillName }));

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(new GracefulCorrectionOutcome(
                ContextNote, ClarificationReply, [FirstCandidate, SecondCandidate]));
    }

    /// <summary>
    /// An undo the real service would resolve, with the registry and the gate answering as they do for a
    /// caller who holds the inverse skill's right. The completion stub honours undoIsPermitted the same
    /// way TurnPreparationService does, so a suppressed offer is visible in the outcome and not only in
    /// the argument the preparer passed.
    /// </summary>
    private void GivenAnUndoIsOffered()
    {
        var lastAction = GivenAStoredAnchor();

        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(new GracefulCorrectionPlan(
                lastAction, CorrectionMessage, Composite, new[] { ExcludedSkillName }));

        _turnPreparation.PeekUndo(Arg.Any<GracefulCorrectionPlan>())
            .Returns(new SkillUndoInvocation(UndoSkillName, UndoArguments));
        _skillRegistry.GetSkillByName(UndoSkillName).Returns(InverseDescriptor);
        _permissionGate.HoldsAsync(UserId, Arg.Any<IReadOnlyCollection<string>>()).Returns(true);

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(call => call.ArgAt<bool>(3)
                ? new GracefulCorrectionOutcome(
                    ContextNote, null, [],
                    new SkillUndoInvocation(UndoSkillName, UndoArguments),
                    UndoneSkillLabel)
                : new GracefulCorrectionOutcome(ContextNote, null, []));
    }

    private CorrectionTurnPreparer CreatePreparer(ITurnConfirmationScope? turnScope = null) => new(
        _lastActionStore, _pendingRecipeStore, _turnPreparation, _assembler, _pendingConfirmationStore,
        _skillRegistry, _permissionGate,
        Substitute.For<ILogger<CorrectionTurnPreparer>>(),
        turnScope);

    private Task<CorrectionTurnPreparation> Prepare(string message = CorrectionMessage, ITurnConfirmationScope? turnScope = null) =>
        CreatePreparer(turnScope).PrepareAsync(
            new Agent { Id = Guid.NewGuid(), Name = "Klacksy" }, new List<string>(), message, ConversationId,
            UserId, language: "de", currentRoute: null, maxToolsForProvider: 10, CancellationToken.None);

    private Task AssembledOn(string message, bool withExclusion) =>
        _assembler.Received(1).AssembleAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), message, Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Is<IReadOnlyCollection<string>?>(
                excluded => withExclusion
                    ? excluded != null && excluded.Contains(ExcludedSkillName)
                    : excluded == null),
            Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>());

    private Task AssemblerPinned(string skillName) =>
        _assembler.Received(1).AssembleAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
            Arg.Is<IReadOnlyCollection<string>?>(pinned => pinned != null && pinned.Contains(skillName)),
            Arg.Any<CancellationToken>());

    private void TheCandidatesWerePinned() =>
        _lastActionStore.Received(1).SaveClarificationCandidates(
            Guid.Parse(UserId), ConversationId,
            Arg.Is<IReadOnlyList<string>>(
                names => names.Contains(FirstCandidate) && names.Contains(SecondCandidate)));

    private void NothingWasPinned() =>
        _lastActionStore.DidNotReceiveWithAnyArgs().SaveClarificationCandidates(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>());

    private void TheUndoWasHeldOnce() =>
        _pendingConfirmationStore.Received(1).Create(
            Guid.Parse(UserId), UndoSkillName,
            Arg.Is<IReadOnlyDictionary<string, object>>(
                arguments => arguments.ContainsKey(UndoArgumentName)),
            PendingConfirmationPurposes.CorrectionUndo);

    private void NoConfirmationWasHeld() =>
        _pendingConfirmationStore.DidNotReceiveWithAnyArgs().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(),
            Arg.Any<string>());

    [Test]
    public async Task WithAPlannedCorrection_AssemblesOnTheCompositeWithTheExclusion()
    {
        GivenACorrectionIsPlanned();

        var result = await Prepare();

        await AssembledOn(Composite, withExclusion: true);
        result.Correction.ShouldNotBeNull();
        result.Correction!.ContextNote.ShouldBe(ContextNote);
    }

    [Test]
    public async Task WithoutACorrection_AssemblesOnThePlainMessage()
    {
        var result = await Prepare();

        await AssembledOn(CorrectionMessage, withExclusion: false);
        result.Correction.ShouldBeNull();
    }

    [Test]
    public async Task ClarificationPinsOfTheAnchor_ReachTheAssembler()
    {
        GivenAStoredAnchor(PinnedSkillName);

        await Prepare();

        await AssemblerPinned(PinnedSkillName);
    }

    [Test]
    public async Task WithAClarification_PinsTheTwoCandidates()
    {
        GivenAClarificationIsPlanned();

        var result = await Prepare();

        TheCandidatesWerePinned();
        result.Correction!.ClarificationReply.ShouldBe(ClarificationReply);
    }

    // The pin is a convenience of the NEXT turn. A store outage may cost it, never the question this turn
    // has already computed and is about to return.
    [Test]
    public async Task WhenThePinWriteThrows_TheTurnStillRuns()
    {
        GivenAClarificationIsPlanned();
        _lastActionStore
            .When(store => store.SaveClarificationCandidates(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>()))
            .Do(_ => throw new InvalidOperationException("store down"));

        var result = await Prepare();

        result.Correction!.ClarificationReply.ShouldBe(ClarificationReply);
    }

    [Test]
    public async Task WithACorrectionButNoClarification_PinsNothing()
    {
        GivenACorrectionIsPlanned();

        var result = await Prepare();

        NothingWasPinned();
        result.Correction.ShouldNotBeNull();
        result.UndoWasHeld.ShouldBeFalse();
    }

    [Test]
    public async Task WithoutACorrection_PinsNothing()
    {
        await Prepare();

        NothingWasPinned();
    }

    // Gate G1 only matters once an anchor could carry a correction at all. Without one the pending-recipe
    // row is irrelevant, and reading it would cost a query on every ordinary turn of every conversation.
    [Test]
    public async Task WithoutAnAnchor_ThePendingRecipeStoreIsNotRead()
    {
        await Prepare();

        _pendingRecipeStore.DidNotReceive().Peek(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task WithAnAnchorThatCanCorrect_ThePendingRecipeStoreIsRead()
    {
        GivenAStoredAnchor();

        await Prepare();

        _pendingRecipeStore.Received(1).Peek(Guid.Parse(UserId), ConversationId);
    }

    // The note is a convenience of the turn, never its purpose: a failure while building it must cost the
    // correction, not the answer the user is waiting for.
    [Test]
    public async Task WhenTheCompletionThrows_TheTurnDegradesToAnOrdinaryOne()
    {
        GivenACorrectionIsPlanned();
        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns<GracefulCorrectionOutcome>(_ => throw new InvalidOperationException("note build failed"));

        var result = await Prepare();

        await AssembledOn(Composite, withExclusion: true);
        result.Correction.ShouldBeNull();
    }

    // The undo offer of rule 3 is a yes/no question, so the invocation it offers has to be held as a
    // pending confirmation the affirmation can redeem.
    [Test]
    public async Task WithAnOfferedUndo_HoldsItAsExactlyOnePendingConfirmation()
    {
        GivenAnUndoIsOffered();

        var result = await Prepare();

        TheUndoWasHeldOnce();
        result.UndoWasHeld.ShouldBeTrue();
    }

    [Test]
    public async Task WithAClarificationInsteadOfAnUndo_HoldsNothing()
    {
        GivenAClarificationIsPlanned();

        await Prepare();

        NoConfirmationWasHeld();
    }

    [Test]
    public async Task WithoutAnUndo_HoldsNothing()
    {
        GivenACorrectionIsPlanned();

        await Prepare();

        NoConfirmationWasHeld();
    }

    [Test]
    public async Task TheHeldUndoToken_IsRecordedOnTheTurnScopeSoAStoppedTurnCanDropIt()
    {
        GivenAnUndoIsOffered();
        _pendingConfirmationStore.Create(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(),
                PendingConfirmationPurposes.CorrectionUndo)
            .Returns(UndoToken);
        var turnScope = Substitute.For<ITurnConfirmationScope>();

        await Prepare(CorrectionMessage, turnScope);

        turnScope.Received(1).MarkIssued(UndoToken);
    }

    [Test]
    public async Task WhenNoUndoWasHeld_NoTokenIsRecordedOnTheTurnScope()
    {
        GivenACorrectionIsPlanned();
        var turnScope = Substitute.For<ITurnConfirmationScope>();

        await Prepare(CorrectionMessage, turnScope);

        turnScope.DidNotReceiveWithAnyArgs().MarkIssued(default!);
    }

    // Same trade as the pin write: losing the token costs the user one convenient "yes", while a thrown
    // store call would cost the answer the turn has already produced.
    [Test]
    public async Task WhenTheUndoWriteThrows_TheTurnStillRuns()
    {
        GivenAnUndoIsOffered();
        _pendingConfirmationStore
            .When(store => store.Create(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("store down"));

        var result = await Prepare();

        result.Correction!.ContextNote.ShouldBe(ContextNote);
        result.UndoWasHeld.ShouldBeFalse();
    }

    // The redemption runs SkillExecutorService.ValidatePermissions before the autonomy gate, so an undo
    // the caller may not release is refused after it was promised. create_group is reversed by
    // delete_group, which stays Admin-only: a Supervisor was offered an undo Klacksy then denied.
    [Test]
    public async Task WithAnUndoTheCallerMayNotRelease_OffersNothingAndHoldsNothing()
    {
        GivenAnUndoIsOffered();
        _permissionGate.HoldsAsync(UserId, Arg.Any<IReadOnlyCollection<string>>()).Returns(false);

        var result = await Prepare();

        NoConfirmationWasHeld();
        result.UndoWasHeld.ShouldBeFalse();
        result.Correction!.Undo.ShouldBeNull();
        result.Correction!.ContextNote.ShouldBe(ContextNote);
    }

    [Test]
    public async Task TheUndoOffer_IsCheckedAgainstTheInverseSkillsOwnRights()
    {
        GivenAnUndoIsOffered();

        await Prepare();

        await _permissionGate.Received(1).HoldsAsync(
            UserId,
            Arg.Is<IReadOnlyCollection<string>>(permissions => permissions.Contains(InversePermission)));
    }

    // An inverse the registry no longer knows would answer "skill not found" on redemption, so it is
    // not an offer either.
    [Test]
    public async Task WithAnUnregisteredInverseSkill_OffersNoUndo()
    {
        GivenAnUndoIsOffered();
        _skillRegistry.GetSkillByName(UndoSkillName).Returns((SkillDescriptor?)null);

        var result = await Prepare();

        NoConfirmationWasHeld();
        result.Correction!.Undo.ShouldBeNull();
        await _permissionGate.DidNotReceive().HoldsAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    // Fails closed: a right that cannot be read is not a right that is held, and the correction itself
    // must still reach the user.
    [Test]
    public async Task WhenThePermissionCheckThrows_OffersNoUndoAndTheTurnStillRuns()
    {
        GivenAnUndoIsOffered();
        _permissionGate.HoldsAsync(UserId, Arg.Any<IReadOnlyCollection<string>>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("identity store down"));

        var result = await Prepare();

        NoConfirmationWasHeld();
        result.Correction!.ContextNote.ShouldBe(ContextNote);
        result.Correction!.Undo.ShouldBeNull();
    }

    // The store reads sit in front of the planning; a store outage must degrade the turn to an ordinary
    // one rather than fail the chat.
    [Test]
    public async Task WhenThePlanningThrows_TheTurnStillRuns()
    {
        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<GracefulCorrectionPlan?>>(_ => throw new InvalidOperationException("store down"));

        var result = await Prepare();

        await AssembledOn(CorrectionMessage, withExclusion: false);
        result.Correction.ShouldBeNull();
    }
}
