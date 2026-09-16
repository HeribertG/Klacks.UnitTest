// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TurnPreparationService.ResolvePendingConfirmation (moved there out of LLMService on
/// 2026-09-16, assertions unchanged), the seam that resurfaces an outstanding autonomy-gate
/// confirmation token in the turn after the gate asked for it. The token itself never
/// survives in the conversation history (only user/assistant text is persisted), so this is the only
/// path by which a held sensitive action can ever be confirmed. The load-bearing case is a reply that
/// affirms AND restates the mutation ("ja, lösch den Benutzer"): it must still resurface the token,
/// because a mutation-intent veto there made the model re-call the skill, which produced a fresh hold
/// and left the user confirming the same action over and over.
/// Since 2026-09-16 the same seam also carries the correction undo of rule 3, which is held as its own
/// purpose precisely so it can expire differently: the offer is made once, so the token it leaves behind
/// is answerable by the immediately following turn and by no other.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationPendingConfirmationForceTests
{
    private const string PendingSkillName = "delete_system_user";
    private const string PendingToken = "token-abc";

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly TimeSpan ForceWindow =
        TimeSpan.FromSeconds(AutonomyDefaults.ConfirmationForceWindowSeconds);

    private IPendingConfirmationStore _confirmationStore = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _confirmationStore = Substitute.For<IPendingConfirmationStore>();

        // Every other dependency is untouched by ResolvePendingConfirmation, which reads only the
        // context, the pending-confirmation store and AutonomyDefaults.
        _service = new TurnPreparationService(
            _confirmationStore,
            recipeEngine: null!,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            slotExtractor: null!,
            lastActionStore: Substitute.For<IAssistantLastActionStore>(),
            routeProbe: Substitute.For<IDeterministicRouteProbe>(),
            inverseResolver: Substitute.For<ISkillInverseResolver>(),
            logger: Substitute.For<ILogger<TurnPreparationService>>());
    }

    private void SetPending(string purpose = PendingConfirmationPurposes.GateReplay) =>
        _confirmationStore
            .PeekLatestForUser(UserId, Arg.Any<TimeSpan>(), purpose)
            .Returns(new PendingConfirmationHandle(PendingToken, PendingSkillName));

    private static LLMContext Context(string message, bool correctionApplied = false) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        GracefulCorrectionApplied = correctionApplied,
        AvailableFunctions =
        [
            new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }
        ]
    };

    // The regression case: the reply confirms AND names the mutation again. Both detectors fire, and
    // the pending gate-replay row has to win, or the confirmation can never be answered.
    [TestCase("ja, lösch den Benutzer")]
    [TestCase("ja, benutzer löschen")]
    [TestCase("yes, delete the user")]
    public void ResolvePendingConfirmation_AffirmationThatRestatesTheMutation_StillForcesConfirmation(string message)
    {
        SetPending();

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(MutationIntentDetector.IsMutationIntent(message), Is.True,
            "test would not cover the veto if the message carried no mutation intent");
        Assert.That(force, Is.True);
        Assert.That(confirmFunction!.Name, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(note, Does.Contain(PendingToken));
        Assert.That(note, Does.Contain(PendingSkillName));
    }

    [TestCase("ja")]
    [TestCase("ok")]
    [TestCase("yes")]
    public void ResolvePendingConfirmation_PlainAffirmation_ForcesConfirmation(string message)
    {
        SetPending();

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(force, Is.True);
        Assert.That(confirmFunction!.Name, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(note, Does.Contain(PendingToken));
    }

    [TestCase("nein, lass es")]
    [TestCase("nicht löschen")]
    [TestCase("was kostet das?")]
    public void ResolvePendingConfirmation_NoAffirmation_DoesNotForce(string message)
    {
        SetPending();

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(force, Is.False);
        Assert.That(confirmFunction, Is.Null);
        Assert.That(note, Is.Null);
    }

    [Test]
    public void ResolvePendingConfirmation_NoPendingRow_DoesNotForce()
    {
        _confirmationStore
            .PeekLatestForUser(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<string>())
            .Returns((PendingConfirmationHandle?)null);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context("ja"));

        Assert.That(force, Is.False);
        Assert.That(confirmFunction, Is.Null);
        Assert.That(note, Is.Null);
    }

    [Test]
    public void ResolvePendingConfirmation_ConfirmSkillOutOfScope_DoesNotForce()
    {
        SetPending();
        var context = Context("ja");
        context.AvailableFunctions = [new LLMFunction { Name = "some_other_skill" }];

        var (force, _, _) = _service.ResolvePendingConfirmation(context);

        Assert.That(force, Is.False);
    }

    // A correction undo is the more recent offer by construction - it was written by the turn that just
    // ended - so it is read first and a gate-replay row is only reached when there is none. Proposal hints
    // are a different reader's rows and are never touched here.
    [Test]
    public void ResolvePendingConfirmation_ReadsTheCorrectionUndoBeforeTheGateReplayRow()
    {
        SetPending();

        _service.ResolvePendingConfirmation(Context("ja"));

        Received.InOrder(() =>
        {
            _confirmationStore.PeekLatestForUser(
                UserId, ForceWindow, PendingConfirmationPurposes.CorrectionUndo);
            _confirmationStore.PeekLatestForUser(
                UserId, ForceWindow, PendingConfirmationPurposes.GateReplay);
        });

        _confirmationStore.DidNotReceive().PeekLatestForUser(
            Arg.Any<Guid>(), Arg.Any<TimeSpan>(), PendingConfirmationPurposes.ProposalHint);
    }

    [Test]
    public void ResolvePendingConfirmation_AnAffirmation_RedeemsAnOutstandingCorrectionUndo()
    {
        SetPending(PendingConfirmationPurposes.CorrectionUndo);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context("ja"));

        Assert.That(force, Is.True);
        Assert.That(confirmFunction!.Name, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(note, Does.Contain(PendingToken));
    }

    // Rule 3: the offer is made once and never as a separate dialogue. So the token answers the turn that
    // immediately follows it and nothing else - a user who ignores the offer and then affirms something
    // the model asked next must not have the undo carried out instead.
    [Test]
    [TestCase("nein, lass es")]
    [TestCase("was kostet das?")]
    public void ResolvePendingConfirmation_AMessageThatDoesNotAffirm_DiscardsTheCorrectionUndo(string message)
    {
        SetPending(PendingConfirmationPurposes.CorrectionUndo);

        var (force, _, _) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(force, Is.False);
        _confirmationStore.Received(1).DiscardCorrectionUndo(UserId);
    }

    // The discard is scoped to the undo purpose alone: a gate-replay hold is answered whenever the user
    // gets round to it, inside its own window, and an unrelated message must not silently drop it.
    [Test]
    public void ResolvePendingConfirmation_AMessageThatDoesNotAffirm_LeavesAGateReplayHoldAlone()
    {
        SetPending();

        _service.ResolvePendingConfirmation(Context("was kostet das?"));

        _confirmationStore.Received(1).DiscardCorrectionUndo(UserId);
        _confirmationStore.DidNotReceiveWithAnyArgs().Consume(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>());
    }

    [Test]
    public void ResolvePendingConfirmation_AnAffirmation_DiscardsNothing()
    {
        SetPending();

        _service.ResolvePendingConfirmation(Context("ja"));

        _confirmationStore.DidNotReceiveWithAnyArgs().DiscardCorrectionUndo(Arg.Any<Guid>());
    }

    // The entry points write the undo token immediately before the model call this method runs inside, so
    // on the offering turn the row in the store is the one this very turn just created. The correction
    // message is not an affirmation, so without the exclusion the offer would be discarded before the
    // user ever read it.
    [Test]
    public void ResolvePendingConfirmation_OnTheTurnThatMakesTheOffer_KeepsTheFreshUndo()
    {
        SetPending(PendingConfirmationPurposes.CorrectionUndo);

        _service.ResolvePendingConfirmation(
            Context("Nein, ich meinte alle Mitarbeitenden.", correctionApplied: true));

        _confirmationStore.DidNotReceiveWithAnyArgs().DiscardCorrectionUndo(Arg.Any<Guid>());
    }

    // A correction may open with an affirmation ("ja, ich meinte ..."), which AffirmationDetector reads
    // as one. The offering turn must not redeem its own fresh token, or the undo is carried out before it
    // was ever offered.
    [Test]
    public void ResolvePendingConfirmation_OnTheTurnThatMakesTheOffer_DoesNotRedeemItsOwnUndo()
    {
        SetPending(PendingConfirmationPurposes.CorrectionUndo);

        var (force, _, _) = _service.ResolvePendingConfirmation(
            Context("ja, ich meinte alle Mitarbeitenden", correctionApplied: true));

        Assert.That(AffirmationDetector.IsAffirmation("ja, ich meinte alle Mitarbeitenden"), Is.True,
            "test would not cover the self-redemption if the correction carried no affirmation");
        Assert.That(force, Is.False);
    }

    // The exclusion is scoped to the undo alone: a gate hold from an earlier turn is still answerable on
    // a correction turn, exactly as it was before the undo existed.
    [Test]
    public void ResolvePendingConfirmation_OnACorrectionTurn_StillRedeemsAGateReplayHold()
    {
        SetPending();

        var (force, _, note) = _service.ResolvePendingConfirmation(
            Context("ja", correctionApplied: true));

        Assert.That(force, Is.True);
        Assert.That(note, Does.Contain(PendingToken));
    }

    [Test]
    public void ResolvePendingConfirmation_AnUnparsableUserId_TouchesTheStoreNotAtAll()
    {
        var context = Context("was kostet das?");
        context.UserId = "not-a-guid";

        var (force, _, _) = _service.ResolvePendingConfirmation(context);

        Assert.That(force, Is.False);
        _confirmationStore.DidNotReceiveWithAnyArgs().DiscardCorrectionUndo(Arg.Any<Guid>());
    }
}
