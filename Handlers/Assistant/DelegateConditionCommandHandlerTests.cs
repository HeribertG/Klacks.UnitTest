// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DelegateConditionCommandHandler (Etappe 4e, "mach du"). Covers: a message id that
/// does not resolve to the delegating user's own row, or to no condition at all, is reported NotFound
/// without ever consulting scope or the ledger; a condition outside the delegating user's own
/// group-visibility scope is reported NotFound, never Forbidden, so its existence is never confirmed to
/// somebody who may not see it; a role tier that does not cover the requested MaxAction is reported
/// Forbidden only once the condition is already known to be visible; and a successful grant reaches the
/// ledger service with exactly the resolved condition id, requested MaxAction and delegating user id.
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
    private static readonly Guid DelegatingUserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentConditionScopeResolver _scopeResolver = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private DelegateConditionCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _scopeResolver = Substitute.For<IAgentConditionScopeResolver>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();

        _sut = new DelegateConditionCommandHandler(
            _dispatchRepository,
            _scopeResolver,
            _conditionRepository,
            _ledgerService,
            NullLogger<DelegateConditionCommandHandler>.Instance);
    }

    private static ProactiveTriggerDispatchRow MakeRow(Guid id, string userId, Guid? conditionId) =>
        new()
        {
            Id = id,
            UserId = userId,
            TriggerKind = "open_order",
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

    [Test]
    public async Task Handle_UnknownMessageId_ReturnsNotFoundWithoutConsultingScope()
    {
        _dispatchRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ProactiveTriggerDispatchRow?)null);

        var result = await _sut.Handle(MakeCommand(Guid.NewGuid(), ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _scopeResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_RowOfOtherUser_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        var row = MakeRow(messageId, OtherUserId.ToString(), Guid.NewGuid());
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_RowWithoutAConditionId_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId: null);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _scopeResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Test]
    public async Task Handle_DelegatingUserIsNotAPlanner_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _conditionRepository.DidNotReceiveWithAnyArgs()
            .GetOpenForScopeByIdAsync(default, default, default!, default);
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_ConditionOutsideDelegatingUsersScope_ReturnsNotFoundNeverForbidden()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Restricted(new HashSet<Guid> { Guid.NewGuid() });
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns((AgentCondition?)null);

        // Even an Admin-only action must not leak that the row exists.
        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_AuthorisedPlannerRequestingExecute_ReturnsForbidden()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Restricted(new HashSet<Guid> { Guid.NewGuid() });
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.Forbidden));
        await _ledgerService.DidNotReceiveWithAnyArgs().TryDelegateAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_AuthorisedPlannerRequestingPrepare_DelegatesAndReturnsDelegated()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Restricted(new HashSet<Guid> { Guid.NewGuid() });
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Prepare, DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.Delegated));
    }

    [Test]
    public async Task Handle_AdminRequestingExecute_DelegatesAndReturnsDelegated()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Execute, DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Execute), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.Delegated));
        await _ledgerService.Received(1).TryDelegateAsync(
            conditionId, ProactiveMaxAction.Execute, DelegatingUserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_LedgerLosesTheRace_ReturnsNotFound()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, Arg.Any<ProactiveMaxAction>(), DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
    }

    [Test]
    public async Task Handle_OwnRowWithDifferentUserIdCasing_IsStillRecognisedAsOwn()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString().ToUpperInvariant(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Prepare, DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.Delegated));
    }

    [Test]
    public async Task Handle_Delegated_AcknowledgesTheDispatchRowBestEffort()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Prepare, DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.Delegated));
        await _dispatchRepository.Received(1).AcknowledgeAsync(
            messageId, DelegatingUserId.ToString(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AcknowledgementThrows_DelegationIsStillReported()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, ProactiveMaxAction.Prepare, DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(true);
        _dispatchRepository
            .AcknowledgeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("store is down"));

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(
            result,
            Is.EqualTo(DelegateConditionOutcome.Delegated),
            "The grant is already written when the acknowledgement runs; its failure must not fail the delegation.");
    }

    [Test]
    public async Task Handle_LedgerLosesTheRace_NeverAcknowledgesTheDispatchRow()
    {
        var messageId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var row = MakeRow(messageId, DelegatingUserId.ToString(), conditionId);
        _dispatchRepository.GetByIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(row);
        var scope = AgentConditionVisibilityScope.Unrestricted();
        _scopeResolver.ResolveAsync(DelegatingUserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);
        _conditionRepository.GetOpenForScopeByIdAsync(conditionId, scope.IsUnrestricted, scope.VisibleRootIds, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = conditionId });
        _ledgerService.TryDelegateAsync(conditionId, Arg.Any<ProactiveMaxAction>(), DelegatingUserId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.Handle(MakeCommand(messageId, ProactiveMaxAction.Prepare), CancellationToken.None);

        Assert.That(result, Is.EqualTo(DelegateConditionOutcome.NotFound));
        await _dispatchRepository.DidNotReceiveWithAnyArgs().AcknowledgeAsync(default, default!, default);
    }
}
