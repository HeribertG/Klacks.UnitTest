// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the identity a proactive action runs under: the happy path builds a context whose acting name
/// is Klacksy's while its rights are the owner's current ones, and each of the three refusal categories
/// comes back as a result rather than an exception. The deleted-owner case matters most - AgentTriggerGovernance
/// carries no foreign key to the user, so a governance row can outlive the account it names. The policy is
/// always asked as the heartbeat kind with the irreversible opt-in hard-wired to false: a governance rule
/// carries no per-action consent, so nothing irreversible may ever run on this path.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class ProactiveActionIdentityProviderTests
{
    private const string SkillName = "cover_absence";
    private const string DeletedOwnerReason = "This could not run because the owner account no longer exists.";
    private const string SensitiveSkillReason = "The skill is classified as sensitive.";

    private IInternalTokenIssuer _tokenIssuer = null!;
    private IUnattendedSkillPolicy _unattendedPolicy = null!;
    private IAgentAutonomyPreferenceRepository _autonomyRepository = null!;
    private ProactiveActionIdentityProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _tokenIssuer = Substitute.For<IInternalTokenIssuer>();
        _unattendedPolicy = Substitute.For<IUnattendedSkillPolicy>();
        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _provider = new ProactiveActionIdentityProvider(
            _tokenIssuer,
            _unattendedPolicy,
            _autonomyRepository,
            NullLogger<ProactiveActionIdentityProvider>.Instance);
    }

    [Test]
    public async Task ResolveForSkill_WithAnIssuableOwner_ActsAsKlacksyUnderTheOwnersCurrentRights()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        GivenTokenFor(ownerUserId, Roles.Authorised);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        var identity = await _provider.ResolveForSkillAsync(ownerUserId, conditionId, SkillName);

        // Assert
        identity.Success.ShouldBeTrue();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.None);

        var context = identity.Context.ShouldNotBeNull();
        context.UserId.ShouldBe(ownerUserId);
        context.UserName.ShouldBe(KlacksyIdentity.SystemUserName);
        context.SessionId.ShouldBe(KlacksyIdentity.ProactiveActionSessionId(conditionId));
        context.BypassAutonomyGate.ShouldBeTrue();
        context.TokenRenewalOwnerId.ShouldBe(ownerUserId);
        context.AccessToken.ShouldNotBeNull();

        // The rights are the EXPANSION of the freshly read roles, not the role names alone - the Admin
        // bypass in the skill executor matches the role string, so both halves have to be present.
        context.UserPermissions.ShouldContain(Roles.Authorised);
        context.UserPermissions.ShouldBe(Permissions.ExpandRoles([Roles.Authorised]));
        identity.UserPermissions.ShouldBe(context.UserPermissions);
    }

    [Test]
    public async Task ResolveForSkill_WithoutAResponsibleOwner_RefusesWithoutMintingAToken()
    {
        // Act
        var identity = await _provider.ResolveForSkillAsync(null, Guid.NewGuid(), SkillName);

        // Assert
        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.NoResponsibleOwner);
        identity.Context.ShouldBeNull();
        identity.UserPermissions.ShouldBeEmpty();
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueForOwnerAsync(default, default, default);
    }

    [Test]
    public async Task ResolveForSkill_WithAnEmptyGuidOwner_IsTreatedAsNoOwnerRatherThanAnAccount()
    {
        // Act
        var identity = await _provider.ResolveForSkillAsync(Guid.Empty, Guid.NewGuid(), SkillName);

        // Assert
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.NoResponsibleOwner);
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueForOwnerAsync(default, default, default);
    }

    [Test]
    public async Task ResolveForSkill_WhenTheOwnerAccountIsGone_ReportsTheIssuersReasonAndNeverThrows()
    {
        // Arrange - ResponsibleOwnerUserId has no foreign key, so it can point at a deleted account.
        var ownerUserId = Guid.NewGuid();
        _tokenIssuer.IssueForOwnerAsync(ownerUserId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Refused(DeletedOwnerReason));

        // Act
        var identity = await _provider.ResolveForSkillAsync(ownerUserId, Guid.NewGuid(), SkillName);

        // Assert
        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.TokenRefused);
        identity.Reason.ShouldBe(DeletedOwnerReason);
        identity.Context.ShouldBeNull();
        _unattendedPolicy.DidNotReceiveWithAnyArgs().Decide(default!);
    }

    [Test]
    public async Task ResolveForSkill_WhenTheUnattendedPolicyRefuses_ReportsItSeparatelyFromATokenRefusal()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        GivenTokenFor(ownerUserId, Roles.Admin);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>())
            .Returns(UnattendedSkillDecision.Deny(SensitiveSkillReason, UnattendedDenyReason.SensitiveSkill));

        // Act
        var identity = await _provider.ResolveForSkillAsync(ownerUserId, Guid.NewGuid(), SkillName);

        // Assert
        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.PolicyRefused);
        identity.Reason.ShouldBe(SensitiveSkillReason);
        identity.Context.ShouldBeNull();
    }

    [Test]
    public async Task ResolveForSkill_ConsultsThePolicyWithTheExpandedRights_NotTheBareRoleNames()
    {
        // Arrange - an empty or role-only permission list is exactly what UnattendedSkillPolicy denies on.
        var ownerUserId = Guid.NewGuid();
        var expandedRights = Permissions.ExpandRoles([Roles.Authorised]);
        GivenTokenFor(ownerUserId, Roles.Authorised);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        await _provider.ResolveForSkillAsync(ownerUserId, Guid.NewGuid(), SkillName);

        // Assert
        _unattendedPolicy.Received(1).Decide(Arg.Is<UnattendedSkillRequest>(request =>
            request.SkillName == SkillName &&
            request.OwnerPermissions.SequenceEqual(expandedRights)));
    }

    [Test]
    public async Task ResolveForSkill_AsksThePolicyAsTheHeartbeat_WithTheIrreversibleOptInHardWiredOff()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        GivenTokenFor(ownerUserId, Roles.Authorised);
        _autonomyRepository.GetAsync(ownerUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow
            {
                UserId = ownerUserId.ToString(),
                Level = AutonomyLevel.FullyAutonomous
            });
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        await _provider.ResolveForSkillAsync(ownerUserId, Guid.NewGuid(), SkillName);

        // Assert
        _unattendedPolicy.Received(1).Decide(Arg.Is<UnattendedSkillRequest>(request =>
            request.ExecutionKind == UnattendedExecutionKind.ProactiveHeartbeat &&
            !request.AllowIrreversibleUnattended &&
            request.AutonomyLevel == AutonomyLevel.FullyAutonomous));
    }

    [Test]
    public async Task ResolveForSkill_WithoutAnAutonomyRow_FallsBackToTheSystemDefaultLevel()
    {
        // Arrange
        var ownerUserId = Guid.NewGuid();
        GivenTokenFor(ownerUserId, Roles.Authorised);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        await _provider.ResolveForSkillAsync(ownerUserId, Guid.NewGuid(), SkillName);

        // Assert
        _unattendedPolicy.Received(1).Decide(
            Arg.Is<UnattendedSkillRequest>(request => request.AutonomyLevel == AutonomyDefaults.DefaultLevel));
    }

    private void GivenTokenFor(Guid ownerUserId, params string[] roles)
    {
        _tokenIssuer.IssueForOwnerAsync(ownerUserId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken(Guid.NewGuid().ToString()), roles));
    }
}
