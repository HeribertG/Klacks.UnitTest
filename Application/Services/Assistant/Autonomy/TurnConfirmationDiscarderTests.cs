// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A stopped turn drops the confirmations it issued - the question they belong to was never asked - and
/// nothing else: a token an earlier turn left stays redeemable, and the correction-undo offer of an earlier
/// turn is only dropped when this very turn made one. Runs against the real store on an in-memory database.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.Autonomy;

[TestFixture]
public class TurnConfirmationDiscarderTests
{
    private const string SkillName = "delete_group";
    private const string ApplySkill = "apply_proposal";
    private static readonly TimeSpan PeekWindow = TimeSpan.FromMinutes(30);
    private static readonly Dictionary<string, object> NoParameters = new();

    private IPendingConfirmationStore _store = null!;
    private TurnConfirmationScope _scope = null!;
    private RecordingLogger<TurnConfirmationDiscarder> _logger = null!;
    private TurnConfirmationDiscarder _discarder = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _scope = new TurnConfirmationScope();
        _logger = new RecordingLogger<TurnConfirmationDiscarder>();
        _discarder = new TurnConfirmationDiscarder(_scope, _store, _logger);
        _userId = Guid.NewGuid();
    }

    [Test]
    public void TheTokensThisTurnIssued_AreDropped_AndAnEarlierTurnsTokenStays()
    {
        var earlier = _store.Create(_userId, SkillName, NoParameters);
        var gateToken = _store.Create(_userId, SkillName, NoParameters);
        var planToken = _store.Create(_userId, "create_plan", NoParameters);
        _scope.MarkIssued(gateToken);
        _scope.MarkIssued(planToken);

        _discarder.DiscardIssuedThisTurn(_userId);

        _store.Consume(gateToken, _userId).ShouldBeNull();
        _store.Consume(planToken, _userId).ShouldBeNull();
        _store.Consume(earlier, _userId).ShouldNotBeNull();
    }

    [Test]
    public void AProposalHintThisTurnLeft_IsDropped()
    {
        _store.CreateProposalHint(_userId, ApplySkill);
        _scope.MarkProposalHint(ApplySkill);

        _discarder.DiscardIssuedThisTurn(_userId);

        _store.PeekLatestForUser(_userId, PeekWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public void AProposalHintOfAnEarlierTurn_StaysWhenThisTurnLeftNone()
    {
        _store.CreateProposalHint(_userId, ApplySkill);

        _discarder.DiscardIssuedThisTurn(_userId);

        _store.PeekLatestForUser(_userId, PeekWindow, PendingConfirmationPurposes.ProposalHint).ShouldNotBeNull();
    }

    [Test]
    public void TheCorrectionUndoOfferThisTurnMade_IsDroppedThroughItsToken()
    {
        var undoToken = _store.Create(_userId, SkillName, NoParameters, PendingConfirmationPurposes.CorrectionUndo);
        _scope.MarkIssued(undoToken);

        _discarder.DiscardIssuedThisTurn(_userId);

        _store.PeekLatestForUser(_userId, PeekWindow, PendingConfirmationPurposes.CorrectionUndo).ShouldBeNull();
    }

    [Test]
    public void ACorrectionUndoOfferOfAnEarlierTurn_StaysWhenThisTurnMadeNone()
    {
        _store.Create(_userId, SkillName, NoParameters, PendingConfirmationPurposes.CorrectionUndo);

        _discarder.DiscardIssuedThisTurn(_userId);

        _store.PeekLatestForUser(_userId, PeekWindow, PendingConfirmationPurposes.CorrectionUndo).ShouldNotBeNull();
    }

    [Test]
    public void ATurnThatIssuedNothing_TouchesTheStoreNotAtAll()
    {
        var store = Substitute.For<IPendingConfirmationStore>();
        var discarder = new TurnConfirmationDiscarder(_scope, store, _logger);

        discarder.DiscardIssuedThisTurn(_userId);

        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void AStoreFailure_IsLoggedAndNeverThrown()
    {
        var failure = new InvalidOperationException("store down");
        var store = Substitute.For<IPendingConfirmationStore>();
        store.When(s => s.DiscardByTokens(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>())).Do(_ => throw failure);
        _scope.MarkIssued("token");
        var discarder = new TurnConfirmationDiscarder(_scope, store, _logger);

        Should.NotThrow(() => discarder.DiscardIssuedThisTurn(_userId));

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && ReferenceEquals(e.Exception, failure));
    }
}
