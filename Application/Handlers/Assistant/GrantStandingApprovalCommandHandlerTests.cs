// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The gates of granting a standing approval (Owner decisions 2026-09-21). Every property here is a way
/// a stored grant could otherwise look like autonomy while being unable to act, or could reach further
/// than the Owner's floor: a kind without a remediation, an unregistered or irreversible remediation
/// skill, a granting administrator who does not hold the skill's permissions or whose own autonomy level
/// is below the threshold the unattended policy will apply, a duration or budget outside the hard limits,
/// and a grant that collides with one that is still running - which is reported as a collision and NEVER
/// silently replaced, so the previous window stays in the audit trail with the person who ended it.
///
/// What this fixture does NOT cover: whether the caller is an administrator at all. That is the
/// controller's [Authorize(Roles = Roles.Admin)] and is pinned by
/// ProactiveStandingApprovalsControllerAuthorizationTests, because the handler never sees a principal.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class GrantStandingApprovalCommandHandlerTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string UnremediatedKind = AgentTriggerKinds.OpenOrder;
    private const string SkillName = "test_remediation_skill";
    private const string RequiredPermission = "some.permission";

    private static readonly DateTime NowUtc = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AdminUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid GroupId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private FakeStandingApprovalRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IConditionRemediationRegistry _registry = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private ISkillPermissionGate _permissionGate = null!;
    private IAgentAutonomyPreferenceRepository _autonomyRepository = null!;
    private SettableTimeProvider _timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeStandingApprovalRepository();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _registry = new SingleEntryRegistry();
        _timeProvider = new SettableTimeProvider(NowUtc);

        _skillRegistry = Substitute.For<ISkillRegistry>();
        _skillRegistry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName, "remediation", SkillCategory.Action, [], [RequiredPermission], [], null));

        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.Reversible);

        _permissionGate = Substitute.For<ISkillPermissionGate>();
        _permissionGate
            .HoldsAsync(AdminUserId.ToString(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(true);

        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        GivenAutonomyLevel(AutonomyLevel.Autonomous);
    }

    [Test]
    public async Task AValidRequest_StoresTheGrant_WithTheDefaultDurationAndBudget_AndCommits()
    {
        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GrantStandingApprovalOutcome.Granted));
            Assert.That(result.Approval, Is.Not.Null);
            Assert.That(result.Approval!.IsActive, Is.True);
            Assert.That(result.Approval.DailyBudget, Is.EqualTo(StandingApprovalDefaults.DefaultDailyBudget));
            Assert.That(
                result.Approval.ExpiresAtUtc,
                Is.EqualTo(NowUtc.AddDays(StandingApprovalDefaults.DefaultDurationDays)));
            Assert.That(result.Approval.GrantedByUserId, Is.EqualTo(AdminUserId));
        });

        var stored = _repository.Rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(stored.TriggerKind, Is.EqualTo(Kind));
            Assert.That(stored.GroupId, Is.EqualTo(GroupId));
            Assert.That(stored.RevokedAtUtc, Is.Null);
        });

        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ARunningGrantForTheSameScope_IsACollision_AndIsNeverReplaced()
    {
        var running = _repository.Seed(Kind, GroupId, AdminUserId, NowUtc.AddDays(-1), NowUtc.AddDays(29));

        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GrantStandingApprovalOutcome.AlreadyActive));
            Assert.That(result.Reason, Does.Contain("Revoke it"));
            Assert.That(_repository.Rows.Single().Id, Is.EqualTo(running.Id));
        });

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    /// <summary>
    /// An expired grant is history, not a lock: the same scope has to be grantable again without anybody
    /// having to revoke a row that stopped applying by itself. This is why "one grant per scope" is a
    /// handler check and not a partial unique index.
    /// </summary>
    [Test]
    public async Task AnExpiredGrantForTheSameScope_DoesNotBlockAFreshOne()
    {
        _repository.Seed(Kind, GroupId, AdminUserId, NowUtc.AddDays(-40), NowUtc.AddMinutes(-1));

        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GrantStandingApprovalOutcome.Granted));
            Assert.That(_repository.Rows.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AKindWithoutARemediation_IsRefused()
    {
        var result = await HandleAsync(
            new GrantStandingApprovalCommand(UnremediatedKind, GroupId, null, null, AdminUserId));

        AssertRefused(result, "no remediation");
    }

    [Test]
    public async Task AnUnregisteredRemediationSkill_IsRefused()
    {
        _skillRegistry.GetSkillByName(SkillName).Returns((SkillDescriptor?)null);

        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        AssertRefused(result, "not registered");
    }

    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task AnIrreversibleOrSensitiveRemediation_IsRefused(SkillRiskClass riskClass)
    {
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(riskClass);

        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        AssertRefused(result, "approved per finding");
    }

    [Test]
    public async Task AGranterWithoutTheSkillsPermissions_IsRefused()
    {
        _permissionGate
            .HoldsAsync(AdminUserId.ToString(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(false);

        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        AssertRefused(result, "do not hold the permissions");
    }

    /// <summary>
    /// The one refusal that exists purely to avoid a silent no-op: the identity provider asks the
    /// unattended policy with the GRANTER's own autonomy level, so a grant from an administrator below
    /// the threshold would be stored, look active, and refuse every single execution in a warning log.
    /// The refusal names the same threshold the later gate applies.
    /// </summary>
    [Test]
    public async Task AGranterBelowTheUnattendedThreshold_IsRefusedWithThatThreshold()
    {
        GivenAutonomyLevel(AutonomyLevel.Assisted);

        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, GroupId, null, null, AdminUserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GrantStandingApprovalOutcome.Refused));
            Assert.That(
                result.Reason,
                Does.Contain(UnattendedSkillPolicyDefaults.MinimumLevelForReversible.ToString()));
            Assert.That(_repository.Rows, Is.Empty);
        });
    }

    [TestCase(0)]
    [TestCase(StandingApprovalDefaults.MaximumDurationDays + 1)]
    public async Task ADurationOutsideTheLimits_IsRefused(int durationDays)
    {
        var result = await HandleAsync(
            new GrantStandingApprovalCommand(Kind, GroupId, durationDays, null, AdminUserId));

        AssertRefused(result, "duration");
    }

    [TestCase(0)]
    [TestCase(StandingApprovalDefaults.MaximumDailyBudget + 1)]
    public async Task ABudgetOutsideTheLimits_IsRefused(int dailyBudget)
    {
        var result = await HandleAsync(
            new GrantStandingApprovalCommand(Kind, GroupId, null, dailyBudget, AdminUserId));

        AssertRefused(result, "daily budget");
    }

    [Test]
    public async Task AGrantForTheUngroupedBucket_IsStoredWithANullGroup()
    {
        var result = await HandleAsync(new GrantStandingApprovalCommand(Kind, null, null, null, AdminUserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GrantStandingApprovalOutcome.Granted));
            Assert.That(_repository.Rows.Single().GroupId, Is.Null);
        });
    }

    private void AssertRefused(GrantStandingApprovalResult result, string reasonFragment)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GrantStandingApprovalOutcome.Refused));
            Assert.That(result.Reason, Does.Contain(reasonFragment));
            Assert.That(result.Approval, Is.Null);
            Assert.That(_repository.Rows, Is.Empty);
        });
    }

    private void GivenAutonomyLevel(AutonomyLevel level) =>
        _autonomyRepository
            .GetAsync(AdminUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminUserId.ToString(), Level = level });

    private Task<GrantStandingApprovalResult> HandleAsync(GrantStandingApprovalCommand command) =>
        new GrantStandingApprovalCommandHandler(
            _repository,
            _unitOfWork,
            _registry,
            _skillRegistry,
            _riskClassifier,
            _permissionGate,
            _autonomyRepository,
            _timeProvider)
            .Handle(command, CancellationToken.None);

    private sealed class SingleEntryRegistry : IConditionRemediationRegistry
    {
        public IReadOnlyCollection<string> RegisteredKinds => [Kind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == Kind
                ? new ConditionRemediationEntry(SkillName, new NeverAsked(), [])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == Kind ? configuredMaxAction : ProactiveMaxAction.Hint;

        /// <summary>The grant path never binds arguments; a binder that throws proves it never does.</summary>
        private sealed class NeverAsked : IConditionRemediationParameterBinder
        {
            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                throw new InvalidOperationException("Granting a standing approval must not bind remediation arguments.");
        }
    }
}
