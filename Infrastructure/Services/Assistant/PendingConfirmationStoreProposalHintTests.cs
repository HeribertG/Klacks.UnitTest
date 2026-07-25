// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the SINGLE pending-confirmation store carries both purposes without the two mixing:
/// a gate-replay row (Create) stays invisible to a proposal-hint peek and vice versa, a proposal hint
/// replaces its predecessor, DiscardProposalHints removes only hints, and a hint older than the peek's
/// maxAge is gone. The age test back-dates a row through the repository on purpose — CreateProposalHint
/// always stamps a full ConfirmationTtlMinutes expiry so a hint cannot be aged through the store API,
/// and PeekLatestForUser reconstructs the creation time from exactly that TTL.
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
