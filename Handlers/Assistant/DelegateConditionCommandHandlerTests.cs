// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DelegateConditionCommandHandler (Etappe 4e "mach du", since 2026-09-20 also the
/// approval). Covers: a message id that does not resolve to the delegating user's own row, or to no
/// condition at all, is reported NotFound without ever consulting scope or the ledger; a condition
/// outside the delegating user's own group-visibility scope is reported NotFound, never Forbidden, so
/// its existence is never confirmed to somebody who may not see it; the rights gate - the remediation
/// skill's RequiredPermissions through ISkillPermissionGate, Admin bypass, no remediation or unknown
/// skill refused as well - answers Forbidden with a reason only once the condition is already known to
/// be visible; a successful grant reaches the ledger with exactly the resolved condition id, requested
/// MaxAction and delegating user id; a Reported row is additionally stamped with the delegating user as
/// approver and its Running approval chain superseded, while a Prepared row keeps the plain cap raise.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class DelegateConditionCommandHandlerTests
{
    private const string Kind = "empty_container";
    private const string SkillName = "create_container_template";
    private const string RequiredPermission = "CanEditShifts";

    private static readonly Guid DelegatingUserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentConditionScopeResolver _scopeResolver = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IConditionRemediationRegistry _remediationRegistry = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillPermissionGate _permissionGate = null!;
    private IEscalationChainService _chainService = null!;
    private DelegateConditionCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _scopeResolver = Substitute.For<IAgentConditionScopeResolver>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _remediationRegistry = Substitute.For<IConditionRemediationRegistry>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _permissionGate = Substitute.For<ISkillPermissionGate>();
        _chainService = Substitute.For<IEscalationChainService>();

        GivenRemediationRegistered();
        GivenSkillKnown();
        GivenDelegatingUserHoldsPermissions(true);
        _ledgerService.TryApproveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _sut = new DelegateConditionCommandHandler(
            _dispatchRepository,
            _scopeResolver,
            _conditionRepository,
            _ledgerService,
            _remediationRegistry,
            _skillRegistry,
            _permissionGate,
            _chainService,
            NullLogger<DelegateConditionCommandHandler>.Instance);
    }

    private void GivenRemediationRegistered()
    {
        _remediationRegistry
            .TryGetEntry(Kind, out Arg.Any<ConditionRemediationEntry?>())
            .Returns(call =>
            {
                call[1] = new ConditionRemediationEntry(SkillName, Substitute.For<IConditionRemediationParameterBinder>(), []);
                return true;
            });
    }

    private void GivenNoRemediation()
    {
        _remediationRegistry
            .TryGetEntry(Arg.Any<string>(), out Arg.Any<ConditionRemediationEntry?>())
            .Returns(call =>
            {
                call[1] = null;
                return false;
            });
    }

    private void GivenSkillKnown() =>
        _skillRegistry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName, string.Empty, SkillCategory.Crud, [], [RequiredPermission], [], null));

    private void GivenDelegatingUserHoldsPermissions(bool holds) =>
        _permissionGate
            .HoldsAsync(DelegatingUserId.ToString(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(holds);

    private static ProactiveTriggerDispatchRow MakeRow(Guid id, string userId, Guid? conditionId) =>
        new()
        {
            Id = id,
            UserId = userId,
            TriggerKind = Kind,
            DedupKey = "dedup-key",
            ConditionId = conditionId
        };

    private DelegateConditionCommand MakeCommand(Guid messageId, ProactiveMaxAction maxAction) =>
        new()
        {
            MessageId = messageId,
            DelegatingUserId = DelegatingUserId,
            MaxAction = maxAction
        };

    private (Guid MessageId, Guid ConditionId) GivenVisibleCondition(
        AgentConditionVisibilityScope scope, AgentConditionStatus status = AgentConditionStatus.Reported)
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(MakeRow(messageId, DelegatingUserId.ToString(), conditionId));
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId, TriggerKind = Kind, Status = status });
        _ledgerService.TryDelegateAsync(conditionId, Arg.Any<ProactiveMaxAction>(), DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        return (messageId, conditionId);
    }

    private static AgentConditionVisibilityScope RestrictedScope() =>
        AgentConditionVisibilityScope.Restricted(new HashSet<Guid> { Guid.NewGuid() });

    [Test]
    public async Task Handle_UnknownMessageId_ReturnsNotFoundWithoutConsultingScope()
    {
        _dispatchRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ProactiveTriggerDispatchRow?)null);

        var result = await _sut.Handle(MakeCommand(Guid.NewGuid(), ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _scopeResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_RowOfOtherUser_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(MakeRow(messageId, OtherUserId.ToString(), Guid.NewGuid()));

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_RowWithoutAConditionId_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(MakeRow(messageId, DelegatingUserId.ToString(), conditionId: null));

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _scopeResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Test]
    public async Task Handle_DelegatingUserIsNotAPlanner_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(MakeRow(messageId, DelegatingUserId.ToString(), Guid.NewGuid()));
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _conditionRepository.DidNotReceiveWithAnyArgs()
            .GetOpenForScopeByIdAsync(default, default, default!, default);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_ConditionOutsideDelegatingUsersScope_ReturnsNotFoundNeverForbidden()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(MakeRow(messageId, DelegatingUserId.ToString(), conditionId));
        var scope = RestrictedScope();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns((AgentCondition?)null);
        GivenDelegatingUserHoldsPermissions(false);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _permissionGate.DidNotReceiveWithAnyArgs().HoldsAsync(default(string)!, default!);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_PlannerWithoutTheRemediationSkillsPermissions_ReturnsForbiddenWithReason_ForAnyMaxAction()
    {
        var (messageId, _) = GivenVisibleCondition(RestrictedScope());
        GivenDelegatingUserHoldsPermissions(false);

        var prepare = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);
        var execute = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(prepare.Outcome, Is.EqualTo(DelegateConditionOutcome.Forbidden));
            Assert.That(execute.Outcome, Is.EqualTo(DelegateConditionOutcome.Forbidden));
            Assert.That(execute.Reason, Does.Contain(SkillName));
            Assert.That(execute.Reason, Does.Contain("permissions you do not hold"));
        });
        await _permissionGate.Received().HoldsAsync(
            DelegatingUserId.ToString(),
            Arg.Is<IReadOnlyCollection<string>>(permissions => permissions.Contains(RequiredPermission)));
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
    }

    [Test]
    public async Task Handle_PlannerHoldingThePermissions_MayDelegateExecute_TheOldLevelCeilingIsGone()
    {
        var (messageId, conditionId) = GivenVisibleCondition(RestrictedScope());

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated));
        await _ledgerService.Received(1).TryDelegateAsync(
            conditionId, ProactiveMaxAction.Execute, DelegatingUserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AdminWithoutTheSkillPermission_PassesThroughTheGatesAdminBypass()
    {
        var (messageId, _) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        GivenDelegatingUserHoldsPermissions(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated));
        await _permissionGate.Received(1).HoldsAsync(DelegatingUserId.ToString(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Test]
    public async Task Handle_KindWithoutARegisteredRemediation_ReturnsForbiddenWithReason()
    {
        var (messageId, _) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        GivenNoRemediation();

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Forbidden));
            Assert.That(result.Reason, Does.Contain("no remediation"));
        });
        await _permissionGate.DidNotReceiveWithAnyArgs().HoldsAsync(default(string)!, default!);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_RemediationSkillUnknownToTheRegistry_ReturnsForbiddenWithReason()
    {
        var (messageId, _) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        _skillRegistry.GetSkillByName(SkillName).Returns((SkillDescriptor?)null);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Forbidden));
            Assert.That(result.Reason, Does.Contain(SkillName));
        });
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_ReportedRow_StampsTheDelegatingUserAsApprover_AndSupersedesTheRunningChain()
    {
        var (messageId, conditionId) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        _chainService.SupersedeConditionApprovalChainAsync(conditionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated));
        Received.InOrder(() =>
        {
            _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Execute, DelegatingUserId, Arg.Any<CancellationToken>());
            _chainService.SupersedeConditionApprovalChainAsync(conditionId, Arg.Is<string>(r => r.Length > 0), Arg.Any<CancellationToken>());
            _ledgerService.TryApproveAsync(conditionId, DelegatingUserId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Handle_ReportedRow_WhenSomebodyApprovedFirst_StillReportsDelegated()
    {
        var (messageId, conditionId) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        _ledgerService.TryApproveAsync(conditionId, DelegatingUserId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated), "The grant is stored; the stamp belongs to whoever approved first.");
    }

    [TestCase(AgentConditionStatus.Prepared)]
    [TestCase(AgentConditionStatus.Escalated)]
    public async Task Handle_RowThatIsNoLongerReported_KeepsThePlainCapRaise_NoStampNoSupersede(AgentConditionStatus status)
    {
        var (messageId, conditionId) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted(), status);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated));
        await _ledgerService.Received(1).TryDelegateAsync(
            conditionId, ProactiveMaxAction.Execute, DelegatingUserId, Arg.Any<CancellationToken>());
        await _ledgerService.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
        await _chainService.DidNotReceiveWithAnyArgs().SupersedeConditionApprovalChainAsync(default, default!, default);
    }

    [Test]
    public async Task Handle_LedgerLosesTheRace_ReturnsNotFound_NeverStampsNorAcknowledges()
    {
        var (messageId, conditionId) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        _ledgerService.TryDelegateAsync(conditionId, Arg.Any<ProactiveMaxAction>(), DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _ledgerService.DidNotReceiveWithAnyArgs().TryApproveAsync(default, default, default);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!, default);
    }

    [Test]
    public async Task Handle_OwnRowWithDifferentUserIdCasing_IsStillRecognisedAsOwn()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(MakeRow(messageId, DelegatingUserId.ToString().ToUpperInvariant(), conditionId));
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId, TriggerKind = Kind });
        _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Prepare, DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated));
    }

    [Test]
    public async Task Handle_Delegated_AcknowledgesTheDispatchRowBestEffort()
    {
        var (messageId, _) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(DelegateConditionOutcome.Delegated));
        await _dispatchRepository.Received(1).AcknowledgeAsync(
            messageId, DelegatingUserId.ToString(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AcknowledgementThrows_DelegationIsStillReported()
    {
        var (messageId, _) = GivenVisibleCondition(AgentConditionVisibilityScope.Unrestricted());
        _dispatchRepository
            .AcknowledgeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("store is down"));

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(
            result.Outcome,
            Is.EqualTo(DelegateConditionOutcome.Delegated),
            "The grant is already written when the acknowledgement runs; its failure must not fail the delegation.");
    }
}
