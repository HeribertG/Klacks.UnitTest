// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the SINGLE pending-confirmation store carries both purposes without the two mixing:
/// a gate-replay row (Create) stays invisible to a proposal-hint peek and vice versa, a proposal hint
/// replaces its predecessor, DiscardProposalHints removes only hints, and a hint older than the peek's
/// maxAge is gone. The age test back-dates a row through the repository on purpose — CreateProposalHint
/// always stamps a full ConfirmationTtlMinutes expiry so a hint cannot be aged through the store API,
/// and PeekLatestForUser reconstructs the creation time from exactly that TTL.
/// The third purpose, the correction undo, is here for one reason only: it must stay redeemable. Consume
/// keys on the token ALONE and never reads the purpose column, which is what lets the undo token travel
/// the ordinary confirm_pending_action replay path without a single change to that skill.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class PendingConfirmationStoreProposalHintTests
{
    private const string ApplySkillName = "apply_customer_grouping";
    private const string OtherApplySkillName = "apply_employee_grouping";
    private const string GatedSkillName = "delete_group";
    private const string UndoParameterName = "groupId";
    private const string UndoParameterValue = "g-1";

    private static readonly IReadOnlyDictionary<string, object> UndoParameters =
        new Dictionary<string, object> { [UndoParameterName] = UndoParameterValue };

    private static readonly TimeSpan ForceWindow =
        TimeSpan.FromSeconds(AutonomyDefaults.ConfirmationForceWindowSeconds);

    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
    }

    [Test]
    public void CreateProposalHint_IsVisibleToProposalHintPeek()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        store.CreateProposalHint(_userId, ApplySkillName);

        var hint = store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint);

        hint.ShouldNotBeNull();
        hint!.SkillName.ShouldBe(ApplySkillName);
    }

    [Test]
    public void CreateProposalHint_IsInvisibleToTheGateReplayPeek()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        store.CreateProposalHint(_userId, ApplySkillName);

        store.PeekLatestForUser(_userId, ForceWindow).ShouldBeNull();
    }

    [Test]
    public void GateReplayRow_IsInvisibleToTheProposalHintPeek()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        store.Create(_userId, GatedSkillName, new Dictionary<string, object> { ["groupId"] = "1" });

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
        store.PeekLatestForUser(_userId, ForceWindow).ShouldNotBeNull();
    }

    [Test]
    public void CreateProposalHint_ReplacesTheEarlierHintOfTheSameUser()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        store.CreateProposalHint(_userId, ApplySkillName);
        store.CreateProposalHint(_userId, OtherApplySkillName);

        var hint = store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint);

        hint.ShouldNotBeNull();
        hint!.SkillName.ShouldBe(OtherApplySkillName);
    }

    [Test]
    public void DiscardProposalHints_WithSkillName_RemovesOnlyThatHint()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        store.CreateProposalHint(_userId, ApplySkillName);
        store.DiscardProposalHints(_userId, OtherApplySkillName);

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldNotBeNull();

        store.DiscardProposalHints(_userId, ApplySkillName);

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public void DiscardProposalHints_LeavesGateReplayRowsUntouched()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        var token = store.Create(_userId, GatedSkillName, new Dictionary<string, object>());
        store.CreateProposalHint(_userId, ApplySkillName);

        store.DiscardProposalHints(_userId);

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
        store.Consume(token, _userId, GatedSkillName).ShouldNotBeNull();
    }

    [Test]
    public void ProposalHintPeek_ReturnsNull_WhenTheHintIsOlderThanTheForceWindow()
    {
        var (store, repository) = CreateStoreWithRepository();
        var agedBy = ForceWindow + TimeSpan.FromMinutes(1);

        repository.AddAsync(new PendingConfirmationRow
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = _userId,
            SkillName = ApplySkillName,
            ParametersJson = "{}",
            Purpose = PendingConfirmationPurposes.ProposalHint,
            ExpiresAtUtc = DateTime.UtcNow
                .AddMinutes(AutonomyDefaults.ConfirmationTtlMinutes)
                .Subtract(agedBy)
        }).GetAwaiter().GetResult();

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    [Test]
    public void ProposalHintPeek_ReturnsTheHint_WhenItIsInsideTheForceWindow()
    {
        var (store, repository) = CreateStoreWithRepository();

        repository.AddAsync(new PendingConfirmationRow
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = _userId,
            SkillName = ApplySkillName,
            ParametersJson = "{}",
            Purpose = PendingConfirmationPurposes.ProposalHint,
            ExpiresAtUtc = DateTime.UtcNow
                .AddMinutes(AutonomyDefaults.ConfirmationTtlMinutes)
                .Subtract(TimeSpan.FromSeconds(10))
        }).GetAwaiter().GetResult();

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldNotBeNull();
    }

    [Test]
    public void CorrectionUndoRow_IsVisibleOnlyToACorrectionUndoPeek_AndIsStillConsumable()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        var token = store.Create(
            _userId, GatedSkillName, UndoParameters, PendingConfirmationPurposes.CorrectionUndo);

        store.PeekLatestForUser(_userId, ForceWindow).ShouldBeNull();
        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();

        var undo = store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.CorrectionUndo);
        undo.ShouldNotBeNull();
        undo!.SkillName.ShouldBe(GatedSkillName);

        var consumed = store.Consume(token, _userId);
        consumed.ShouldNotBeNull();
        consumed!.SkillName.ShouldBe(GatedSkillName);
        consumed.Parameters[UndoParameterName].ToString().ShouldBe(UndoParameterValue);
    }

    [Test]
    public void DiscardCorrectionUndo_RemovesOnlyTheUndoRows()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        store.Create(_userId, GatedSkillName, UndoParameters, PendingConfirmationPurposes.CorrectionUndo);
        store.Create(_userId, OtherApplySkillName, UndoParameters);
        store.CreateProposalHint(_userId, ApplySkillName);

        store.DiscardCorrectionUndo(_userId);

        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.CorrectionUndo).ShouldBeNull();
        store.PeekLatestForUser(_userId, ForceWindow).ShouldNotBeNull();
        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldNotBeNull();
    }

    // The offering turn cannot drop its predecessor itself - it runs after the write - so a second
    // correction inside the force window would otherwise leave two redeemable offers for one "ja".
    [Test]
    public void ASecondCorrectionUndo_ReplacesTheFirst()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();

        var first = store.Create(
            _userId, GatedSkillName, UndoParameters, PendingConfirmationPurposes.CorrectionUndo);
        var second = store.Create(
            _userId, OtherApplySkillName, UndoParameters, PendingConfirmationPurposes.CorrectionUndo);

        store.Consume(first, _userId).ShouldBeNull();
        store.Consume(second, _userId).ShouldNotBeNull();
    }

    [Test]
    public void DiscardCorrectionUndo_LeavesAnotherUsersUndoAlone()
    {
        var store = PendingStoreTestFactory.CreateConfirmationStore();
        var otherUserId = Guid.NewGuid();

        store.Create(otherUserId, GatedSkillName, UndoParameters, PendingConfirmationPurposes.CorrectionUndo);

        store.DiscardCorrectionUndo(_userId);

        store.PeekLatestForUser(otherUserId, ForceWindow, PendingConfirmationPurposes.CorrectionUndo)
            .ShouldNotBeNull();
    }

    [Test]
    public void RowWithoutPurpose_IsReadAsGateReplay()
    {
        var (store, repository) = CreateStoreWithRepository();

        repository.AddAsync(new PendingConfirmationRow
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = _userId,
            SkillName = GatedSkillName,
            ParametersJson = "{}",
            Purpose = string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(AutonomyDefaults.ConfirmationTtlMinutes)
        }).GetAwaiter().GetResult();

        store.PeekLatestForUser(_userId, ForceWindow).ShouldNotBeNull();
        store.PeekLatestForUser(_userId, ForceWindow, PendingConfirmationPurposes.ProposalHint).ShouldBeNull();
    }

    private static (IPendingConfirmationStore Store, IPendingConfirmationRepository Repository) CreateStoreWithRepository()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPendingConfirmationRepository))
            .Returns(_ => new PendingConfirmationRepository(new DataBaseContext(options, httpAccessor)));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return (
            new PersistentPendingConfirmationStore(scopeFactory),
            new PendingConfirmationRepository(new DataBaseContext(options, httpAccessor)));
    }
}
