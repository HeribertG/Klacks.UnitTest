// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for the action dispatcher, written against the code as it stood before it was
/// split into collaborators (2026-10-21). They pin what the other fixtures only test by substring or not at
/// all: the exact audit details and report texts of every outcome, the per-kind scope of the governance
/// cache and the tick cap, the per-kind exception boundary, the cascade guard's GroupId fallback and the
/// executor's edge cases (lost success transition, null arguments, blank result message). A split that
/// moves a constant, re-orders a gate or turns per-kind state into per-tick state turns one of these red.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionActionServiceCharacterizationTests
{
    private const string Kind = AgentTriggerKinds.EmptyContainer;
    private const string OtherKind = AgentTriggerKinds.OpenOrder;
    private const string SkillName = "test_remediation_skill";
    private const string RequiredArgument = "containerId";
    private const string OptionalArgument = "optionalNote";
    private const string SkillMessage = "Template created.";

    private static readonly DateTime NowUtc = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ApproverUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GranterUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid GroupId = Guid.Parse("66666666-6666-6666-6666-666666666666");

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
    private CharacterizationRegistry _registry = null!;
    private List<(Guid? Approver, Guid? Group, string Message)> _reports = null!;
    private List<SkillInvocation> _invocations = null!;

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
        GivenGovernance(Kind, ProactiveMaxAction.Execute);
        GivenGovernance(OtherKind, ProactiveMaxAction.Execute);

        _quietWindow = Substitute.For<IQuietWindowService>();
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ResolvedIdentity((Guid)call[0]));

        _invocations = [];
        _skillExecutor = Substitute.For<ISkillExecutor>();
        GivenSkillReturns(SkillResult.SuccessResult(null, SkillMessage));

        _reports = [];
        _reporter = Substitute.For<IProactiveActionReporter>();
        _reporter
            .ReportToApprovalAudienceAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _reports.Add(((Guid?)call[0], (Guid?)call[1], (string)call[2]));
                return 1;
            });

        _approvalStarter = Substitute.For<IConditionApprovalChainStarter>();
        _approvalStarter
            .TryStartAsync(Arg.Any<AgentCondition>(), Arg.Any<ConditionRemediationEntry>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ConditionApprovalStartOutcome.Started);

        _registry = new CharacterizationRegistry([Kind]);
    }

    [Test]
    public async Task AnExecutedRow_WritesTheExactClaimAndOutcomeDetails_AndTheExactReport()
    {
        var condition = GivenApprovedCondition();

        var result = await RunAsync();

        var events = _repository.EventsFor(condition.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(
                events.Single(e => e.EventType == nameof(AgentConditionStatus.Prepared)).Detail,
                Is.EqualTo($"klacksy-claim:skill={SkillName} attempt=1"));
            Assert.That(
                events.Single(e => e.EventType == nameof(AgentConditionStatus.Executed)).Detail,
                Is.EqualTo($"klacksy-action:executed {SkillName}"));
            Assert.That(events.Single(e => e.EventType == nameof(AgentConditionStatus.Executed)).UserId, Is.EqualTo(ApproverUserId));
            Assert.That(_repository.Stored(condition.Id).ApprovedByUserId, Is.EqualTo(ApproverUserId));
            Assert.That(_reports, Has.Count.EqualTo(1));
            Assert.That(_reports[0].Approver, Is.EqualTo(ApproverUserId));
            Assert.That(
                _reports[0].Message,
                Is.EqualTo(
                    "I have carried out a remediation on my own.\n\n"
                    + $"Finding: {Kind} (condition {condition.Id})\nAction: {SkillName}\nResult: {SkillMessage}"));
        });

        await _identityProvider.Received(1).ResolveForSkillAsync(
            ApproverUserId, condition.Id, SkillName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFailedResultWithABlankMessage_RecordsNoMessage_AndTheExactFailedReport()
    {
        GivenSkillReturns(SkillResult.Error("   "));
        var condition = GivenApprovedCondition();

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(
                _repository.EventsFor(condition.Id).Single(e => e.EventType == AgentConditionEventTypes.AttemptFailed).Detail,
                Is.EqualTo("klacksy-action:attempt failed: no message"));
            Assert.That(_reports.Single().Message, Is.EqualTo(
                "A remediation I attempted on my own did not work.\n\n"
                + $"Finding: {Kind} (condition {condition.Id})\nAction: {SkillName}\nProblem: no message\n"
                + "Attempt 1 of 3; after that I stop trying and leave it to you."));
        });
    }

    [Test]
    public async Task ARemediationThatThrows_RecordsTheExceptionMessageAsTheFailure()
    {
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the API was unreachable"));
        var condition = GivenApprovedCondition();

        await RunAsync();

        Assert.That(
            _repository.EventsFor(condition.Id).Single(e => e.EventType == AgentConditionEventTypes.AttemptFailed).Detail,
            Is.EqualTo("klacksy-action:attempt failed: the API was unreachable"));
    }

    [Test]
    public async Task AnExhaustedRow_IsEscalatedBeforeTheQuietWindowAndTheBinderAreConsulted_WithTheExactDetailAndReport()
    {
        _quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(true);
        _registry.BindsNothing = true;
        var condition = GivenApprovedCondition();
        _repository.Stored(condition.Id).AttemptCount = AgentConditionActionDefaults.MaxAttemptsBeforeEscalation;

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Escalated, Is.EqualTo(1));
            Assert.That(result.SkippedQuiet, Is.Zero);
            Assert.That(result.SkippedUnbindable, Is.Zero);
            Assert.That(
                _repository.EventsFor(condition.Id).Single(e => e.EventType == nameof(AgentConditionStatus.Escalated)).Detail,
                Is.EqualTo("klacksy-action:ineffective after 3 attempt(s)"));
            Assert.That(_reports.Single().Approver, Is.EqualTo(ApproverUserId));
            Assert.That(_reports.Single().Message, Is.EqualTo(
                "I am giving up on a finding and leaving it to you.\n\n"
                + $"Finding: {Kind} (condition {condition.Id})\nI attempted the remediation 3 time(s) without success."));
        });
    }

    [Test]
    public async Task AnExpiredApproval_IsWithdrawnWithTheExactUnprefixedReason_AndTheExactReport()
    {
        var condition = GivenApprovedCondition();
        _repository.Stored(condition.Id).ApprovedAtUtc =
            NowUtc.AddMinutes(-AgentConditionActionDefaults.ApprovalExecutionWindowMinutes - 1);
        var expectedReason = string.Format(
            CultureInfo.InvariantCulture,
            "approval older than {0} minute(s) when the tick reached the row",
            AgentConditionActionDefaults.ApprovalExecutionWindowMinutes);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsWithdrawn, Is.EqualTo(1));
            Assert.That(
                _repository.EventsFor(condition.Id).Single(e => e.EventType == AgentConditionEventTypes.ApprovalWithdrawn).Detail,
                Is.EqualTo(expectedReason));
            Assert.That(_reports.Single().Message, Is.EqualTo(WithdrawnReport(condition, expectedReason)));
        });
    }

    [Test]
    public async Task ARefusedApprover_IsWithdrawnWithTheExactReason_BeforeAnyClaim()
    {
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Refused(ProactiveActionIdentityRefusal.PermissionsMissing, "account disabled"));
        var condition = GivenApprovedCondition();
        const string expectedReason = "approver no longer qualifies: account disabled";

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsWithdrawn, Is.EqualTo(1));
            Assert.That(result.Failed, Is.Zero);
            Assert.That(_repository.Stored(condition.Id).AttemptCount, Is.Zero);
            Assert.That(
                _repository.EventsFor(condition.Id).Single(e => e.EventType == AgentConditionEventTypes.ApprovalWithdrawn).Detail,
                Is.EqualTo(expectedReason));
            Assert.That(_reports.Single().Message, Is.EqualTo(WithdrawnReport(condition, expectedReason)));
        });
    }

    [Test]
    public async Task AWithdrawalLostToAnotherApprover_SkipsTheRowWithoutCountingOrReporting()
    {
        var condition = GivenApprovedCondition();
        _repository.Stored(condition.Id).ApprovedAtUtc =
            NowUtc.AddMinutes(-AgentConditionActionDefaults.ApprovalExecutionWindowMinutes - 1);
        _quietWindow
            .IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _repository.Stored(condition.Id).ApprovedByUserId = GranterUserId;
                return false;
            });

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsWithdrawn, Is.Zero);
            Assert.That(result.Executed, Is.Zero);
            Assert.That(result.Failed, Is.Zero);
            Assert.That(_reports, Is.Empty);
            Assert.That(_repository.EventsFor(condition.Id), Is.Empty);
        });
    }

    [Test]
    public async Task APreparedRowWithAnOldApproval_IsResumedRatherThanWithdrawn()
    {
        var condition = GivenApprovedCondition(AgentConditionStatus.Prepared);
        var stored = _repository.Stored(condition.Id);
        stored.ApprovedAtUtc = NowUtc.AddDays(-1);
        stored.AttemptCount = 1;
        stored.LastAttemptAtUtc = NowUtc.AddMinutes(-AgentConditionActionDefaults.StaleClaimMinutes - 1);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsWithdrawn, Is.Zero);
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(
                _repository.EventsFor(condition.Id).Single(e => e.EventType == AgentConditionEventTypes.Reclaimed).Detail,
                Is.EqualTo($"klacksy-claim:skill={SkillName} attempt=2"));
        });
    }

    [Test]
    public async Task AStandingApproval_WritesTheExactClaimSuffix_ProvenanceEvent_AndReport()
    {
        var grant = _standingApprovals.Seed(
            Kind, GroupId, GranterUserId,
            grantedAtUtc: NowUtc.AddDays(-1),
            expiresAtUtc: NowUtc.AddDays(6));
        var condition = GivenCondition(AgentConditionStatus.Reported, groupId: GroupId);

        var result = await RunAsync();

        var events = _repository.EventsFor(condition.Id);
        var expires = grant.ExpiresAtUtc.ToString("u", CultureInfo.InvariantCulture);
        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(
                events.Single(e => e.EventType == nameof(AgentConditionStatus.Prepared)).Detail,
                Is.EqualTo($"klacksy-claim:skill={SkillName} attempt=1 standing-approval={grant.Id}"));
            Assert.That(
                events.Single(e => e.EventType == AgentConditionEventTypes.ExecutedUnderStandingApproval).Detail,
                Is.EqualTo($"klacksy-action:standingApprovalId={grant.Id} grantedByUserId={GranterUserId}"));
            Assert.That(_reports.Single().Approver, Is.EqualTo(GranterUserId));
            Assert.That(_reports.Single().Group, Is.EqualTo(GroupId));
            Assert.That(_reports.Single().Message, Is.EqualTo(
                "I have carried out a remediation under a standing approval, without asking anybody about this finding.\n\n"
                + $"Finding: {Kind} (condition {condition.Id})\nAction: {SkillName}\nResult: {SkillMessage}\n"
                + $"Standing approval {grant.Id} of user {GranterUserId}, valid until {expires}.\n"
                + "If this should not have happened: revoke the standing approval to stop further runs, and ask me "
                + "to roll this change back - there is no one-click undo on this report."));
        });

        await _identityProvider.Received(1).ResolveForSkillAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheTickCap_CountsApprovalStarts_StopsTheKind_AndReportsTheExactCapReason()
    {
        for (var offset = 0; offset < AgentConditionActionDefaults.MaxExecutionsPerKindPerTick + 2; offset++)
        {
            GivenCondition(AgentConditionStatus.Reported, detectedAtUtc: NowUtc.AddHours(-10 + offset));
        }

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsRequested, Is.EqualTo(AgentConditionActionDefaults.MaxExecutionsPerKindPerTick));
            Assert.That(result.Considered, Is.EqualTo(AgentConditionActionDefaults.MaxExecutionsPerKindPerTick + 1));
            Assert.That(result.LeftForBudget, Is.EqualTo(2));
            Assert.That(_reports.Single().Approver, Is.Null);
            Assert.That(_reports.Single().Message, Is.EqualTo(
                $"I stopped acting on '{Kind}' for now: the absolute cap of 5 action(s) per kind per scan is reached.\n"
                + "2 further finding(s) of this kind stay open and unhandled until the limit frees up again."));
        });

        await _approvalStarter.Received(AgentConditionActionDefaults.MaxExecutionsPerKindPerTick).TryStartAsync(
            Arg.Any<AgentCondition>(), Arg.Any<ConditionRemediationEntry>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADailyBudgetStop_AfterAnAction_ReportsTheExactBudgetText()
    {
        GivenGovernance(Kind, ProactiveMaxAction.Execute, dailyActionBudget: 1);
        GivenApprovedCondition(detectedAtUtc: NowUtc.AddHours(-3));
        GivenApprovedCondition(detectedAtUtc: NowUtc.AddHours(-2));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(result.LeftForBudget, Is.EqualTo(1));
            Assert.That(_reports.Last().Approver, Is.EqualTo(ApproverUserId));
            Assert.That(_reports.Last().Message, Is.EqualTo(
                $"I stopped acting on '{Kind}' for now: the daily action budget of 1 is used up.\n"
                + "1 further finding(s) of this kind stay open and unhandled until the limit frees up again."));
        });
    }

    [Test]
    public async Task NullValuedArguments_AreDroppedFromTheSkillParameters()
    {
        _registry.AddsNullOptionalArgument = true;
        GivenApprovedCondition();

        await RunAsync();

        Assert.That(_invocations.Single().Parameters.Keys, Is.EquivalentTo(new[] { RequiredArgument }));
        Assert.That(_invocations.Single().SkillName, Is.EqualTo(SkillName));
    }

    [Test]
    public async Task ASuccessfulRunWhoseOutcomeTransitionIsLost_IsStillCountedExecutedAndReported()
    {
        var condition = GivenApprovedCondition();
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _repository.LoseNextTransitionFor(condition.Id);
                return SkillResult.SuccessResult(null, SkillMessage);
            });

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).Status, Is.EqualTo(AgentConditionStatus.Prepared));
            Assert.That(_reports.Single().Message, Does.StartWith("I have carried out a remediation on my own."));
        });
    }

    [Test]
    public async Task Governance_IsResolvedOncePerGroupWithinAKind()
    {
        GivenGovernance(Kind, ProactiveMaxAction.Hint);
        GivenApprovedCondition();
        GivenApprovedCondition();
        GivenApprovedCondition(groupId: GroupId);
        GivenApprovedCondition(groupId: GroupId);

        await RunAsync();

        await _governance.Received(1).ResolveAsync(Kind, null, Arg.Any<CancellationToken>());
        await _governance.Received(1).ResolveAsync(Kind, GroupId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheGovernanceCache_IsScopedToOneKind_SoAHintKindNeverBorrowsAnExecuteDecision()
    {
        _registry = new CharacterizationRegistry([Kind, OtherKind]);
        GivenGovernance(OtherKind, ProactiveMaxAction.Hint);
        var executable = GivenApprovedCondition();
        var hinted = GivenApprovedCondition(kind: OtherKind);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Considered, Is.EqualTo(2));
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(executable.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
            Assert.That(_repository.Stored(hinted.Id).Status, Is.EqualTo(AgentConditionStatus.Reported));
        });

        await _governance.Received(1).ResolveAsync(OtherKind, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheTickCap_IsCountedPerKind()
    {
        _registry = new CharacterizationRegistry([Kind, OtherKind]);
        for (var index = 0; index < AgentConditionActionDefaults.MaxExecutionsPerKindPerTick; index++)
        {
            GivenCondition(AgentConditionStatus.Reported);
        }

        GivenCondition(AgentConditionStatus.Reported, kind: OtherKind);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.ApprovalsRequested, Is.EqualTo(AgentConditionActionDefaults.MaxExecutionsPerKindPerTick + 1));
            Assert.That(result.LeftForBudget, Is.Zero);
        });
    }

    [Test]
    public async Task AKindThatThrows_DoesNotStopTheNextKind()
    {
        _registry = new CharacterizationRegistry([Kind, OtherKind]);
        _governance
            .ResolveAsync(Kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("governance store down"));
        GivenApprovedCondition();
        var other = GivenApprovedCondition(kind: OtherKind);

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Considered, Is.EqualTo(2));
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(other.Id).Status, Is.EqualTo(AgentConditionStatus.Executed));
        });
    }

    [Test]
    public void ACancelledTick_PropagatesTheCancellation()
    {
        GivenApprovedCondition();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => CreateService().RunAsync(cancellation.Token));
    }

    [Test]
    public async Task ARowWithoutEntity_IsACascadeOfAnExecutionInTheSameGroup_AndTheLinkIsWritten()
    {
        var cause = GivenExecutedCondition(entityId: Guid.NewGuid(), groupId: GroupId);
        var condition = GivenApprovedCondition(groupId: GroupId, detectedAtUtc: NowUtc.AddMinutes(-1));
        _repository.Stored(condition.Id).EntityId = null;

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCascade, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).CausedByConditionId, Is.EqualTo(cause.Id));
        });
    }

    [Test]
    public async Task ARowWithItsOwnEntity_IsNotACascadeOfAnExecutionOnAnotherEntityInTheSameGroup()
    {
        GivenExecutedCondition(entityId: Guid.NewGuid(), groupId: GroupId);
        var condition = GivenApprovedCondition(groupId: GroupId, detectedAtUtc: NowUtc.AddMinutes(-1));

        var result = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCascade, Is.Zero);
            Assert.That(result.Executed, Is.EqualTo(1));
            Assert.That(_repository.Stored(condition.Id).CausedByConditionId, Is.Null);
        });
    }

    private Task<AgentConditionActionTickResult> RunAsync() => CreateService().RunAsync(CancellationToken.None);

    private AgentConditionActionService CreateService() =>
        new(
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
            NullLogger<AgentConditionActionService>.Instance);

    private static string WithdrawnReport(AgentCondition condition, string reason) =>
        "An approved remediation was NOT carried out.\n\n"
        + $"Finding: {Kind} (condition {condition.Id})\nAction: {SkillName}\nReason: {reason}\n"
        + "The finding stays open; I will ask for approval again on the next company day at the earliest.";

    private static ProactiveActionIdentity ResolvedIdentity(Guid actingUserId) =>
        ProactiveActionIdentity.Resolved(
            new SkillExecutionContext
            {
                UserId = actingUserId,
                TenantId = Guid.Empty,
                UserName = KlacksyIdentity.SystemUserName,
                UserPermissions = ["some.permission"],
                BypassAutonomyGate = true
            },
            ["some.permission"]);

    private void GivenSkillReturns(SkillResult skillResult) =>
        _skillExecutor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _invocations.Add((SkillInvocation)call[0]);
                return skillResult;
            });

    private void GivenGovernance(string kind, ProactiveMaxAction maxAction, int dailyActionBudget = 50)
    {
        var decision = new ProactiveGovernanceDecision(
            TriggerKind: kind,
            GroupId: null,
            EffectiveMaxAction: maxAction,
            ConfiguredMaxAction: maxAction,
            Enabled: true,
            KillSwitchActive: false,
            DailyActionBudget: dailyActionBudget,
            WindowActionLimit: 50,
            WindowMinutes: 60,
            IsStored: true,
            GlobalAutonomyCap: ProactiveMaxAction.Execute);

        _governance
            .ResolveAsync(kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(decision);
    }

    private AgentCondition GivenCondition(
        AgentConditionStatus status,
        string kind = Kind,
        Guid? groupId = null,
        DateTime? detectedAtUtc = null)
    {
        var groups = groupId is { } scopedGroupId ? new HashSet<Guid> { scopedGroupId } : null;
        var condition = _repository.Seed(
            kind, Guid.NewGuid().ToString(), status, detectedAtUtc ?? NowUtc.AddHours(-1), groups);
        condition.Severity = AgentTriggerSeverity.Medium;
        condition.EntityId = Guid.NewGuid();
        condition.PayloadJson = "{}";

        return condition;
    }

    private AgentCondition GivenApprovedCondition(
        AgentConditionStatus status = AgentConditionStatus.Reported,
        string kind = Kind,
        Guid? groupId = null,
        DateTime? detectedAtUtc = null)
    {
        var condition = GivenCondition(status, kind, groupId, detectedAtUtc);
        condition.ApprovedByUserId = ApproverUserId;
        condition.ApprovedAtUtc = NowUtc.AddMinutes(-1);

        return condition;
    }

    private AgentCondition GivenExecutedCondition(Guid entityId, Guid groupId)
    {
        var executed = GivenCondition(AgentConditionStatus.Executed, OtherKind, groupId, NowUtc.AddHours(-2));
        executed.EntityId = entityId;
        executed.HandledAtUtc = NowUtc.AddMinutes(-5);

        return executed;
    }

    private sealed class CharacterizationRegistry : IConditionRemediationRegistry
    {
        private readonly IReadOnlyCollection<string> _kinds;

        public CharacterizationRegistry(IReadOnlyCollection<string> kinds)
        {
            _kinds = kinds;
        }

        public bool BindsNothing { get; set; }

        public bool AddsNullOptionalArgument { get; set; }

        public IReadOnlyCollection<string> RegisteredKinds => _kinds;

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = _kinds.Contains(triggerKind)
                ? new ConditionRemediationEntry(
                    SkillName, new CharacterizationBinder(BindsNothing, AddsNullOptionalArgument), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            _kinds.Contains(triggerKind) ? configuredMaxAction : ProactiveMaxAction.Hint;
    }

    private sealed class CharacterizationBinder : IConditionRemediationParameterBinder
    {
        private readonly bool _bindsNothing;
        private readonly bool _addsNullOptionalArgument;

        public CharacterizationBinder(bool bindsNothing, bool addsNullOptionalArgument)
        {
            _bindsNothing = bindsNothing;
            _addsNullOptionalArgument = addsNullOptionalArgument;
        }

        public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload)
        {
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (_bindsNothing)
            {
                return arguments;
            }

            arguments[RequiredArgument] = Guid.NewGuid().ToString();
            if (_addsNullOptionalArgument)
            {
                arguments[OptionalArgument] = null;
            }

            return arguments;
        }
    }
}
