// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the identity a proactive action runs under: the happy path builds a context whose acting name
/// is Klacksy's while its rights are the approver's current ones, and each of the three refusal categories
/// comes back as a result rather than an exception. The deleted-approver case matters most - the approval
/// stamp on the condition carries no foreign key to the user, so a stamped row can outlive the account it
/// names. The policy is always asked as the heartbeat kind with the irreversible opt-in hard-wired to
/// false: an approval releases one remediation, not an irreversible one, so nothing irreversible may ever
/// run on this path.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class ProactiveActionIdentityProviderTests
{
    private const string SkillName = "cover_absence";
    private const string DeletedApproverReason = "This could not run because the approver account no longer exists.";
    private const string SensitiveSkillReason = "The skill is classified as sensitive.";

    private IInternalTokenIssuer _tokenIssuer = null!;
    private IUnattendedSkillPolicy _unattendedPolicy = null!;
    private IAgentAutonomyPreferenceRepository _autonomyRepository = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ProactiveActionIdentityProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _tokenIssuer = Substitute.For<IInternalTokenIssuer>();
        _unattendedPolicy = Substitute.For<IUnattendedSkillPolicy>();
        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        GivenSkillRequires();
        _provider = new ProactiveActionIdentityProvider(
            _tokenIssuer,
            _unattendedPolicy,
            _autonomyRepository,
            _skillRegistry,
            NullLogger<ProactiveActionIdentityProvider>.Instance);
    }

    [Test]
    public async Task ResolveForSkill_WhenTheActingUserLostTheSkillsPermission_RefusesBeforeAskingThePolicy()
    {
        // Arrange - the approver still has a role (the token is issued) but no longer the one the
        // remediation needs. This is the "approved yesterday, demoted today" case: refused here, before
        // any claim, so it costs no attempt and the approval is asked again from somebody who qualifies.
        var approverUserId = Guid.NewGuid();
        GivenTokenFor(approverUserId, Roles.User);
        GivenSkillRequires(Permissions.CanDeleteClients);

        // Act
        var identity = await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

        // Assert
        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.PermissionsMissing);
        identity.Reason.ShouldNotBeNull().ShouldContain(Permissions.CanDeleteClients);
        _unattendedPolicy.DidNotReceive().Decide(Arg.Any<UnattendedSkillRequest>());
    }

    [Test]
    public async Task ResolveForSkill_AnAdminPassesTheSkillsPermissionCheckRegardless()
    {
        var adminUserId = Guid.NewGuid();
        GivenTokenFor(adminUserId, Roles.Admin);
        GivenSkillRequires(Permissions.CanDeleteClients);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        var identity = await _provider.ResolveForSkillAsync(adminUserId, Guid.NewGuid(), SkillName);

        identity.Success.ShouldBeTrue();
    }

    [Test]
    public async Task ResolveForSkill_WhenTheSkillIsUnknownToTheRegistry_RefusesRatherThanSkippingTheCheck()
    {
        var approverUserId = Guid.NewGuid();
        GivenTokenFor(approverUserId, Roles.Authorised);
        _skillRegistry.GetSkillByName(SkillName).Returns((SkillDescriptor?)null);

        var identity = await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.PermissionsMissing);
    }

    private void GivenSkillRequires(params string[] requiredPermissions)
    {
        _skillRegistry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName,
            "test",
            SkillCategory.Query,
            Array.Empty<SkillParameter>(),
            requiredPermissions,
            Array.Empty<LLMCapability>(),
            null));
    }

    [Test]
    public async Task ResolveForSkill_WithAnIssuableApprover_ActsAsKlacksyUnderTheApproversCurrentRights()
    {
        // Arrange
        var approverUserId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        GivenTokenFor(approverUserId, Roles.Authorised);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        var identity = await _provider.ResolveForSkillAsync(approverUserId, conditionId, SkillName);

        // Assert
        identity.Success.ShouldBeTrue();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.None);

        var context = identity.Context.ShouldNotBeNull();
        context.UserId.ShouldBe(approverUserId);
        context.UserName.ShouldBe(KlacksyIdentity.SystemUserName);
        context.SessionId.ShouldBe(KlacksyIdentity.ProactiveActionSessionId(conditionId));
        context.BypassAutonomyGate.ShouldBeTrue();
        context.TokenRenewalOwnerId.ShouldBe(approverUserId);
        context.AccessToken.ShouldNotBeNull();

        // The rights are the EXPANSION of the freshly read roles, not the role names alone - the Admin
        // bypass in the skill executor matches the role string, so both halves have to be present.
        context.UserPermissions.ShouldContain(Roles.Authorised);
        context.UserPermissions.ShouldBe(Permissions.ExpandRoles([Roles.Authorised]));
        identity.UserPermissions.ShouldBe(context.UserPermissions);
    }

    [Test]
    public async Task ResolveForSkill_WithAnEmptyGuidApprover_IsTreatedAsNoApproverRatherThanAnAccount()
    {
        // Act
        var identity = await _provider.ResolveForSkillAsync(Guid.Empty, Guid.NewGuid(), SkillName);

        // Assert
        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.NoApprover);
        identity.Context.ShouldBeNull();
        identity.UserPermissions.ShouldBeEmpty();
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueForOwnerAsync(default, default, default);
    }

    [Test]
    public async Task ResolveForSkill_WhenTheApproverAccountIsGone_ReportsTheIssuersReasonAndNeverThrows()
    {
        // Arrange - the approval stamp has no foreign key to the user, so it can point at a deleted account.
        var approverUserId = Guid.NewGuid();
        _tokenIssuer.IssueForOwnerAsync(approverUserId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Refused(DeletedApproverReason));

        // Act
        var identity = await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

        // Assert
        identity.Success.ShouldBeFalse();
        identity.Refusal.ShouldBe(ProactiveActionIdentityRefusal.TokenRefused);
        identity.Reason.ShouldBe(DeletedApproverReason);
        identity.Context.ShouldBeNull();
        _unattendedPolicy.DidNotReceiveWithAnyArgs().Decide(default!);
    }

    [Test]
    public async Task ResolveForSkill_WhenTheUnattendedPolicyRefuses_ReportsItSeparatelyFromATokenRefusal()
    {
        // Arrange
        var approverUserId = Guid.NewGuid();
        GivenTokenFor(approverUserId, Roles.Admin);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>())
            .Returns(UnattendedSkillDecision.Deny(SensitiveSkillReason, UnattendedDenyReason.SensitiveSkill));

        // Act
        var identity = await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

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
        var approverUserId = Guid.NewGuid();
        var expandedRights = Permissions.ExpandRoles([Roles.Authorised]);
        GivenTokenFor(approverUserId, Roles.Authorised);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

        // Assert
        _unattendedPolicy.Received(1).Decide(Arg.Is<UnattendedSkillRequest>(request =>
            request.SkillName == SkillName &&
            request.OwnerPermissions.SequenceEqual(expandedRights)));
    }

    [Test]
    public async Task ResolveForSkill_AsksThePolicyAsTheHeartbeat_WithTheIrreversibleOptInHardWiredOff()
    {
        // Arrange
        var approverUserId = Guid.NewGuid();
        GivenTokenFor(approverUserId, Roles.Authorised);
        _autonomyRepository.GetAsync(approverUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow
            {
                UserId = approverUserId.ToString(),
                Level = AutonomyLevel.FullyAutonomous
            });
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

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
        var approverUserId = Guid.NewGuid();
        GivenTokenFor(approverUserId, Roles.Authorised);
        _unattendedPolicy.Decide(Arg.Any<UnattendedSkillRequest>()).Returns(UnattendedSkillDecision.Allow());

        // Act
        await _provider.ResolveForSkillAsync(approverUserId, Guid.NewGuid(), SkillName);

        // Assert
        _unattendedPolicy.Received(1).Decide(
            Arg.Is<UnattendedSkillRequest>(request => request.AutonomyLevel == AutonomyDefaults.DefaultLevel));
    }

    private void GivenTokenFor(Guid approverUserId, params string[] roles)
    {
        _tokenIssuer.IssueForOwnerAsync(approverUserId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken(Guid.NewGuid().ToString()), roles));
    }
}
