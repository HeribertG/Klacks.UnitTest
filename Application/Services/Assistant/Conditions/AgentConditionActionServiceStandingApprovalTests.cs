// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The standing-approval path through the action dispatcher (Owner decisions 2026-09-21): an
/// administrator's advance consent for one kind in one scope replaces the per-finding approval chain
/// while it lasts, and nothing else is relaxed. The properties pinned here are the ones the feature would
/// be dangerous without:
///  - a covered row executes in the same tick, under the GRANTER's identity, and NO chain is started;
///  - the grant's id lands on the claim event (behind the budget prefix, which must stay first) and the
///    execution appends an ExecutedUnderStandingApproval event naming grant and granter;
///  - expired, revoked, foreign-group and foreign-kind grants do not apply, and the row falls back to the
///    chain - these four run against the real StandingApprovalPolicy through
///    FakeStandingApprovalRepository, not against a stubbed answer;
///  - a spent grant budget falls back to the chain rather than executing;
///  - a granter who can no longer act leaves NO approval stamp on the row: the identity is resolved before
///    the stamp, so there is nothing to withdraw and no stamp/withdraw loop;
///  - the kill switch still wins, and a grant is never even looked for when the gates before it stop the
///    row.
/// Same real ledger over the fake repository as the sibling fixtures, for the same reason: the claim IS
/// the safety.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceStandingApprovalTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string OtherKind = AgentTriggerKinds.OpenOrder;
    private const string SkillName = "test_remediation_skill";
    private const string RequiredArgument = "containerId";

    private static readonly DateTime NowUtc = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GranterUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid GroupId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherGroupId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private FakeAgentConditionRepository _repository = null!;
    private FakeStandingApprovalRepository _standingApprovals = null!;
    private SettableTimeProvider _timeProvider = null!;
    private FixedCompanyClock _companyClock = null!;
    private IAgentConditionLedgerService _ledger = null!;
    private IProactiveGovernanceResolver _governance = null!;
    private IQuietWindowService _quietWindow = null!;
    private IProactiveActionIdentityProvider _identityProvider = null!;
    private ISkillExecutor _skillExecutor = null!;
    private IProactiveActionReporter _reporter = null!;
    private IConditionApprovalChainStarter _approvalStarter = null!;
    private IConditionRemediationRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new FakeAgentConditionRepository();
        _standingApprovals = new FakeStandingApprovalRepository();
        _timeProvider = new SettableTimeProvider(NowUtc);
        _companyClock = new FixedCompanyClock(NowUtc);
        _ledger = new AgentConditionLedgerService(
            _repository, _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

        _governance = Substitute.For<IProactiveGovernanceResolver>();
        GivenGovernance(ProactiveMaxAction.Execute);

        _quietWindow = Substitute.For<IQuietWindowService>();
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        GivenIdentityResolvesFor(GranterUserId);

        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Template created."));

        _reporter = Substitute.For<IProactiveActionReporter>();
        _reporter
            .ReportToApprovalAudienceAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);

        _approvalStarter = Substitute.For<IConditionApprovalChainStarter>();
        _approvalStarter
            .TryStartAsync(Arg.Any<AgentCondition>(), Arg.Any<ConditionRemediationEntry>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ConditionApprovalStartOutcome.Started);

        _registry = new SingleEntryRegistry();
    }

    [Test]
    public async Task AnActiveGrant_ExecutesInTheSameTick_UnderTheGranter_AndAsksNoChain()
    {
        var grant = GivenGrant();
        var condition = GivenCondition();

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(result.ApprovalsRequested, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(stored.ApprovedByUserId, Is.EqualTo(GranterUserId));
            Assert.That(stored.HandlingKind, Is.EqualTo(AgentConditionHandlingKind.Executed));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
        });

        await _approvalStarter.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!, default, default);
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(context => context.UserId == GranterUserId),
            Arg.Any<CancellationToken>());

        var events = _repository.EventsFor(condition.Id);
        Assert.That(events.Select(e => e.EventType), Is.EquivalentTo(new[]
        {
            AgentConditionEventTypes.Approved,
            AgentConditionStatus.Prepared.ToString(),
            AgentConditionStatus.Executed.ToString(),
            AgentConditionEventTypes.ExecutedUnderStandingApproval
        }));

        var audit = events.Single(e => e.EventType == AgentConditionEventTypes.ExecutedUnderStandingApproval);
        Assert.Multiple(() =>
        {
            Assert.That(audit.Detail, Does.Contain(grant.Id.ToString()));
            Assert.That(audit.Detail, Does.Contain(GranterUserId.ToString()));
        });
    }

    /// <summary>
    /// The claim event is what the daily budget and the circuit breaker count, recognised by the
    /// ActionClaimDetailPrefix at the START of Detail. The grant id is appended for the audit trail, so
    /// this pins both: the id is there AND the prefix is still first.
    /// </summary>
    [Test]
    public async Task TheClaimEvent_NamesTheGrant_WithoutLosingTheBudgetPrefix()
    {
        var grant = GivenGrant();
        var condition = GivenCondition();

        await RunAsync();

        var claim = _repository.EventsFor(condition.Id)
            .Single(e => e.EventType == AgentConditionStatus.Prepared.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(claim.Detail, Does.StartWith(AgentConditionActionDefaults.ActionClaimDetailPrefix));
            Assert.That(claim.Detail, Does.Contain(grant.Id.ToString()));
        });
    }

    [Test]
    public async Task TheReport_SaysItRanUnderAStandingApproval_AndNamesTheGranter()
    {
        var grant = GivenGrant();
        GivenCondition();

        await RunAsync();

        await _reporter.Received(1).ReportToApprovalAudienceAsync(
            GranterUserId,
            GroupId,
            Arg.Is<string>(message =>
                message.Contains("standing approval")
                && message.Contains(grant.Id.ToString())
                && message.Contains(GranterUserId.ToString())),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnExpiredGrant_DoesNotApply_AndTheChainIsAskedInstead()
    {
        _standingApprovals.Seed(
            Kind, GroupId, GranterUserId,
            grantedAtUtc: NowUtc.AddDays(-40),
            expiresAtUtc: NowUtc.AddMinutes(-1));
        var condition = GivenCondition();

        var result = await RunAsync();

        await AssertFellBackToTheChainAsync(result, condition);
    }

    [Test]
    public async Task ARevokedGrant_DoesNotApply_AndTheChainIsAskedInstead()
    {
        _standingApprovals.Seed(
            Kind, GroupId, GranterUserId,
            grantedAtUtc: NowUtc.AddDays(-2),
            expiresAtUtc: NowUtc.AddDays(28),
            revokedAtUtc: NowUtc.AddHours(-1));
        var condition = GivenCondition();

        var result = await RunAsync();

        await AssertFellBackToTheChainAsync(result, condition);
    }

    /// <summary>
    /// A null GroupId on a grant is the ungrouped bucket, never a wildcard, and a grant for one group
    /// never reaches another - the production lookup has no fallback from a group to the
    /// installation-wide row, unlike the governance lookup it would otherwise resemble.
    /// </summary>
    [Test]
    public async Task AGrantForAnotherGroupOrForNoGroup_DoesNotApplyToAGroupedFinding()
    {
        _standingApprovals.Seed(Kind, OtherGroupId, GranterUserId, NowUtc.AddDays(-1), NowUtc.AddDays(29));
        _standingApprovals.Seed(Kind, null, GranterUserId, NowUtc.AddDays(-1), NowUtc.AddDays(29));
        var condition = GivenCondition();

        var result = await RunAsync();

        await AssertFellBackToTheChainAsync(result, condition);
    }

    [Test]
    public async Task AGrantForAnotherKind_DoesNotApply()
    {
        _standingApprovals.Seed(OtherKind, GroupId, GranterUserId, NowUtc.AddDays(-1), NowUtc.AddDays(29));
        var condition = GivenCondition();

        var result = await RunAsync();

        await AssertFellBackToTheChainAsync(result, condition);
    }

    [Test]
    public async Task ASpentGrantBudget_FallsBackToTheChain_InsteadOfExecuting()
    {
        _standingApprovals.Seed(
            Kind, GroupId, GranterUserId, NowUtc.AddDays(-1), NowUtc.AddDays(29), dailyBudget: 1);
        var condition = GivenCondition();
        await GivenEarlierClaimTodayAsync(condition);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.ApprovalsRequested, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.Null);
        });
    }

    /// <summary>
    /// The one property that keeps a dead grant from becoming an endless writer: the granter's identity is
    /// resolved BEFORE the approval stamp, so a granter who lost the role leaves no stamp, no withdrawal
    /// event and no attempt behind - the finding simply goes to a human.
    /// </summary>
    [Test]
    public async Task AGranterWhoLostTheRights_LeavesNoStamp_NoWithdrawal_AndNoAttempt()
    {
        GivenGrant();
        _identityProvider
            .ResolveForSkillAsync(GranterUserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Refused(
                ProactiveActionIdentityRefusal.PermissionsMissing, "The acting user no longer holds CanEditClients."));
        var condition = GivenCondition();

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.ApprovalsWithdrawn, Is.Zero);
            Assert.That(result.ApprovalsRequested, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(stored.ApprovedByUserId, Is.Null);
            Assert.That(stored.ApprovedAtUtc, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(_repository.EventsFor(condition.Id), Is.Empty);
        });

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheKillSwitch_StillStopsAGrantedKind_AndNoGrantIsEvenConsulted()
    {
        GivenGrant();
        GivenGovernance(ProactiveMaxAction.Execute, killSwitchActive: true);
        var condition = GivenCondition();

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.ApprovalsRequested, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.Null);
            Assert.That(_standingApprovals.Lookups, Is.Zero);
        });
    }

    /// <summary>
    /// A grant covers a finding nobody has approved, never a claim somebody else's regime left behind: a
    /// Prepared row without a stamp is left to age into escalation exactly as before.
    /// </summary>
    [Test]
    public async Task APreparedRowWithoutAStamp_IsNotResumedUnderAGrant()
    {
        GivenGrant();
        var condition = GivenCondition(AgentConditionStatus.Prepared);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.SkippedNoApprover, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.Null);
        });
    }

    private async Task AssertFellBackToTheChainAsync(
        AgentConditionActionTickResult result, AgentCondition condition)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.ApprovalsRequested, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.Null);
            Assert.That(_repository.EventsFor(condition.Id), Is.Empty);
        });

        await _approvalStarter.Received(1).TryStartAsync(
            Arg.Is<AgentCondition>(c => c.Id == condition.Id),
            Arg.Any<ConditionRemediationEntry>(),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    private Task<AgentConditionActionTickResult> RunAsync() =>
        new AgentConditionActionService(
            _repository,
            _ledger,
            _governance,
            _registry,
            _quietWindow,
            _identityProvider,
            _skillExecutor,
            _reporter,
            _approvalStarter,
            _standingApprovals,
            _timeProvider,
            _companyClock,
            NullLogger<AgentConditionActionService>.Instance)
            .RunAsync(CancellationToken.None);

    private StandingApproval GivenGrant(int dailyBudget = StandingApprovalDefaults.DefaultDailyBudget) =>
        _standingApprovals.Seed(
            Kind, GroupId, GranterUserId,
            grantedAtUtc: NowUtc.AddDays(-1),
            expiresAtUtc: NowUtc.AddDays(StandingApprovalDefaults.DefaultDurationDays - 1),
            dailyBudget: dailyBudget);

    private Task GivenEarlierClaimTodayAsync(AgentCondition condition) =>
        _repository.InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = AgentConditionStatus.Prepared.ToString(),
            AtUtc = NowUtc.AddHours(-2),
            Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
        });

    private void GivenGovernance(
        ProactiveMaxAction maxAction,
        bool killSwitchActive = false,
        int dailyActionBudget = 50)
    {
        var decision = new ProactiveGovernanceDecision(
            TriggerKind: Kind,
            GroupId: GroupId,
            EffectiveMaxAction: killSwitchActive ? ProactiveMaxAction.Hint : maxAction,
            ConfiguredMaxAction: maxAction,
            Enabled: true,
            KillSwitchActive: killSwitchActive,
            DailyActionBudget: dailyActionBudget,
            WindowActionLimit: 50,
            WindowMinutes: 60,
            IsStored: true);

        _governance
            .ResolveAsync(Kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(decision);
    }

    private void GivenIdentityResolvesFor(Guid actingUserId)
    {
        _identityProvider
            .ResolveForSkillAsync(actingUserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = actingUserId,
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = ["some.permission"],
                    BypassAutonomyGate = true
                },
                ["some.permission"]));
    }

    private AgentCondition GivenCondition(AgentConditionStatus status = AgentConditionStatus.Reported)
    {
        var condition = _repository.Seed(Kind, Guid.NewGuid().ToString(), status, NowUtc.AddHours(-1));
        condition.Severity = AgentTriggerSeverity.Medium;
        condition.EntityId = Guid.NewGuid();
        condition.GroupId = GroupId;
        condition.PayloadJson = "{}";

        if (status == AgentConditionStatus.Prepared)
        {
            condition.LastAttemptAtUtc = NowUtc.AddMinutes(-AgentConditionActionDefaults.StaleClaimMinutes - 1);
        }

        return condition;
    }

    private sealed class SingleEntryRegistry : IConditionRemediationRegistry
    {
        public IReadOnlyCollection<string> RegisteredKinds => [Kind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == Kind
                ? new ConditionRemediationEntry(SkillName, new AlwaysBinds(), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == Kind ? configuredMaxAction : ProactiveMaxAction.Hint;

        private sealed class AlwaysBinds : IConditionRemediationParameterBinder
        {
            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                new Dictionary<string, object?>(StringComparer.Ordinal) { [RequiredArgument] = Guid.NewGuid().ToString() };
        }
    }
}
