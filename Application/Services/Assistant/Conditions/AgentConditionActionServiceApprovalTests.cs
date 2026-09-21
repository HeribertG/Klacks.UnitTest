// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The approval path through the action dispatcher (design 2026-09-20, Owner decisions final): Execute
/// means execute AFTER approval. A Reported row without approval gets a chain asked - behind every
/// existing guard - and is never claimed; a stamped row is executed under the APPROVER's identity within
/// the approval execution window (two scan intervals, deliberately wider than the stale-claim window),
/// with the approver's rights re-checked at that moment; a stamp that is older than that window or whose
/// approver no longer qualifies is withdrawn and reported, never executed; and every report reaches the
/// approver plus the finding's audience. Same real ledger over the fake repository as
/// AgentConditionActionServiceTests, for the same reason: the claim IS the safety.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceApprovalTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string SkillName = "test_remediation_skill";
    private const string RequiredArgument = "containerId";

    private static readonly DateTime NowUtc = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ApproverUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid GroupId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private FakeAgentConditionRepository _repository = null!;
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
        _timeProvider = new SettableTimeProvider(NowUtc);
        _companyClock = new FixedCompanyClock(NowUtc);
        _ledger = new AgentConditionLedgerService(
            _repository, _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

        _governance = Substitute.For<IProactiveGovernanceResolver>();
        GivenGovernance(ProactiveMaxAction.Execute);

        _quietWindow = Substitute.For<IQuietWindowService>();
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        GivenIdentityResolvesFor(ApproverUserId);

        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Template created."));

        _reporter = Substitute.For<IProactiveActionReporter>();
        _reporter.ReportToApprovalAudienceAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);

        _approvalStarter = Substitute.For<IConditionApprovalChainStarter>();
        GivenStarterAnswers(ConditionApprovalStartOutcome.Started);

        _registry = new SingleEntryRegistry();
    }

    [Test]
    public async Task AnUnapprovedReportedRow_GetsAChainAsked_AndIsNeverClaimed()
    {
        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsRequested, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(_repository.Stored(condition.Id).AttemptCount, Is.Zero);
            Assert.That(_repository.EventsFor(condition.Id), Is.Empty);
        });

        await _approvalStarter.Received(1).TryStartAsync(
            Arg.Is<AgentCondition>(c => c.Id == condition.Id),
            Arg.Is<ConditionRemediationEntry>(e => e.RemediationSkillName == SkillName),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _identityProvider.DidNotReceiveWithAnyArgs().ResolveForSkillAsync(default, default, default!, default);
    }

    [Test]
    public async Task AChainAlreadyRunningOrEndedToday_IsCountedAsAwaiting_NotAskedAgain()
    {
        GivenStarterAnswers(ConditionApprovalStartOutcome.ChainAlreadyRunning);
        GivenCondition(AgentConditionStatus.Reported);
        GivenStarterAnswers(ConditionApprovalStartOutcome.WaitingForNextCompanyDay);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AwaitingApproval, Is.EqualTo(1));
            Assert.That(result.ApprovalsRequested, Is.Zero);
            Assert.That(result.Executed, Is.Zero);
        });
    }

    [Test]
    public async Task NoEligibleApproverOrDeclinedStart_FailsClosed_AndIsCounted()
    {
        GivenStarterAnswers(ConditionApprovalStartOutcome.NoEligibleApprover);
        var condition = GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsUnavailable, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });
    }

    [Test]
    public async Task TheExistingGuards_RunBeforeAnyChainIsAsked()
    {
        var cascade = GivenCondition(AgentConditionStatus.Reported);
        _repository.Stored(cascade.Id).CausedByConditionId = Guid.NewGuid();

        var quiet = GivenCondition(AgentConditionStatus.Reported);
        _quietWindow.IsQuietForAsync(Arg.Is<AgentCondition>(c => c.Id == quiet.Id), Arg.Any<CancellationToken>()).Returns(true);

        GivenGovernance(ProactiveMaxAction.Execute, killSwitchActive: true);
        GivenCondition(AgentConditionStatus.Reported);

        var result = await RunAsync();

        Assert.That(result.ApprovalsRequested, Is.Zero);
        await _approvalStarter.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!, default, default);
    }

    [Test]
    public async Task AnExhaustedBudget_BlocksTheChainStartToo()
    {
        GivenGovernance(ProactiveMaxAction.Execute, dailyActionBudget: 1);
        var condition = GivenCondition(AgentConditionStatus.Reported);
        await _repository.InsertEventAsync(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = AgentConditionStatus.Prepared.ToString(),
            AtUtc = NowUtc.AddHours(-2),
            Detail = AgentConditionActionDefaults.ActionClaimDetailPrefix + "earlier tick"
        });

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.LeftForBudget, Is.EqualTo(1));
            Assert.That(result.ApprovalsRequested, Is.Zero);
        });
        await _approvalStarter.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!, default, default);
    }

    [Test]
    public async Task AnApprovedRow_IsExecutedUnderTheApproversIdentity_AndStampedAsApproved()
    {
        var condition = GivenApprovedCondition(approvedAtUtc: NowUtc.AddMinutes(-5));

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(stored.ApprovedByUserId, Is.EqualTo(ApproverUserId));
            Assert.That(stored.HandlingKind, Is.EqualTo(AgentConditionHandlingKind.Executed));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
        });

        await _identityProvider.Received(1).ResolveForSkillAsync(
            ApproverUserId, condition.Id, SkillName, Arg.Any<CancellationToken>());
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(context => context.UserId == ApproverUserId),
            Arg.Any<CancellationToken>());

        var events = _repository.EventsFor(condition.Id);
        Assert.That(events.Select(e => e.EventType), Is.EquivalentTo(new[]
        {
            AgentConditionStatus.Prepared.ToString(), AgentConditionStatus.Executed.ToString()
        }));
        Assert.That(events.All(e => e.UserId == ApproverUserId), Is.True, "Claim and outcome events name the approver, never null.");

        await _reporter.Received(1).ReportToApprovalAudienceAsync(
            ApproverUserId, GroupId, Arg.Is<string>(m => m.Contains("carried out")), Arg.Any<CancellationToken>());
        await _approvalStarter.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!, default, default);
    }

    [Test]
    public async Task AnApproverWhoLostThePermission_IsNotExecuted_ApprovalWithdrawn_AndReported()
    {
        _identityProvider
            .ResolveForSkillAsync(ApproverUserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Refused(
                ProactiveActionIdentityRefusal.PermissionsMissing, "The acting user no longer holds CanEditClients."));
        var condition = GivenApprovedCondition(approvedAtUtc: NowUtc.AddMinutes(-5));

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsWithdrawn, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.Failed, Is.Zero);
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Reported));
            Assert.That(stored.AttemptCount, Is.Zero, "Refused before the claim: no attempt is spent.");
            Assert.That(stored.ApprovedByUserId, Is.Null);
            Assert.That(stored.ApprovedAtUtc, Is.Null);
        });

        var withdrawal = _repository.EventsFor(condition.Id).Single();
        Assert.That(withdrawal.EventType, Is.EqualTo(AgentConditionEventTypes.ApprovalWithdrawn));
        Assert.That(withdrawal.UserId, Is.EqualTo(ApproverUserId));

        await _skillExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _reporter.Received(1).ReportToApprovalAudienceAsync(
            ApproverUserId, GroupId, Arg.Is<string>(m => m.Contains("NOT carried out")), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnApprovalOlderThanTheExecutionWindow_IsWithdrawnInsteadOfExecuted()
    {
        var condition = GivenApprovedCondition(
            approvedAtUtc: NowUtc.AddMinutes(-AgentConditionActionDefaults.ApprovalExecutionWindowMinutes - 1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsWithdrawn, Is.EqualTo(1));
            Assert.That(result.Executed, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.Null);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });

        await _identityProvider.DidNotReceiveWithAnyArgs().ResolveForSkillAsync(default, default, default!, default);
        await _approvalStarter.DidNotReceiveWithAnyArgs().TryStartAsync(
            default!, default!, default, default);
    }

    /// <summary>
    /// The tick runs once per scan interval and an approval is only ever answered by a LATER tick, so an
    /// execution window as short as the stale-claim window would have withdrawn roughly every other
    /// approval before any tick could act on it. Pins that the two windows are not the same number.
    /// </summary>
    [Test]
    public async Task AnApprovalOlderThanTheStaleClaimWindow_ButInsideTheExecutionWindow_IsStillExecuted()
    {
        var condition = GivenApprovedCondition(
            approvedAtUtc: NowUtc.AddMinutes(-AgentConditionActionDefaults.StaleClaimMinutes - 1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(result.ApprovalsWithdrawn, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.EqualTo(ApproverUserId));
        });

        await _identityProvider.Received(1).ResolveForSkillAsync(
            ApproverUserId, condition.Id, SkillName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFailedRemediation_IsReportedToApproverAndAudience_AndStaysApprovedForTheRetry()
    {
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error("the container vanished"));
        var condition = GivenApprovedCondition(approvedAtUtc: NowUtc.AddMinutes(-1));

        var result = await RunAsync();

        var stored = _repository.Stored(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(AgentConditionStatus.Prepared));
            Assert.That(stored.ApprovedByUserId, Is.EqualTo(ApproverUserId));
        });

        await _reporter.Received(1).ReportToApprovalAudienceAsync(
            ApproverUserId, GroupId, Arg.Is<string>(m => m.Contains("did not work")), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEscalatedApprovedRow_IsReportedToApproverAndAudience()
    {
        var condition = GivenApprovedCondition(approvedAtUtc: NowUtc.AddMinutes(-1));
        _repository.Stored(condition.Id).AttemptCount = AgentConditionActionDefaults.MaxAttemptsBeforeEscalation;

        var result = await RunAsync();

        Assert.That(result.Escalated, Is.EqualTo(1));
        await _reporter.Received(1).ReportToApprovalAudienceAsync(
            ApproverUserId, GroupId, Arg.Is<string>(m => m.Contains("giving up")), Arg.Any<CancellationToken>());
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
            Substitute.For<IStandingApprovalRepository>(),
            _timeProvider,
            _companyClock,
            NullLogger<AgentConditionActionService>.Instance)
            .RunAsync(CancellationToken.None);

    private void GivenStarterAnswers(ConditionApprovalStartOutcome outcome) =>
        _approvalStarter
            .TryStartAsync(Arg.Any<AgentCondition>(), Arg.Any<ConditionRemediationEntry>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(outcome);

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

    private AgentCondition GivenCondition(AgentConditionStatus status, string severity = AgentTriggerSeverity.Medium)
    {
        var condition = _repository.Seed(Kind, Guid.NewGuid().ToString(), status, NowUtc.AddHours(-1));
        condition.Severity = severity;
        condition.EntityId = Guid.NewGuid();
        condition.GroupId = GroupId;
        condition.PayloadJson = "{}";

        return condition;
    }

    private AgentCondition GivenApprovedCondition(DateTime approvedAtUtc, string severity = AgentTriggerSeverity.Medium)
    {
        var condition = GivenCondition(AgentConditionStatus.Reported, severity);
        condition.ApprovedByUserId = ApproverUserId;
        condition.ApprovedAtUtc = approvedAtUtc;

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
