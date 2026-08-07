// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for MigrateContractSchedulingRulesCommandHandler: only the contracts the admin explicitly
/// listed are moved, a stale row (contract no longer exists) is skipped instead of failing the whole
/// batch, a target equal to the current one is a no-op, a null target clears the assignment, and every
/// contract that actually moved raises exactly one ContractChangedEvent from its ValidFrom so the
/// surcharge recalculation reacts as it does for a normal contract edit.
/// </summary>

using Klacks.Api.Application.Commands.SchedulingRules;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class MigrateContractSchedulingRulesCommandHandlerTests
{
    private static readonly DateTime ValidFrom = new(2026, 3, 1);

    private IContractRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private MigrateContractSchedulingRulesCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IContractRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();

        _handler = new MigrateContractSchedulingRulesCommandHandler(
            _repository,
            _unitOfWork,
            _eventDispatcher,
            Substitute.For<ILogger<MigrateContractSchedulingRulesCommandHandler>>());
    }

    [Test]
    public async Task Handle_ContractMovedToAnotherRule_PersistsAndDispatchesFromValidFrom()
    {
        var contract = BuildContract(Guid.NewGuid());
        var targetRuleId = Guid.NewGuid();
        _repository.Get(contract.Id).Returns(contract);

        var result = await _handler.Handle(Command((contract.Id, targetRuleId)), CancellationToken.None);

        result.ShouldBe(1);
        contract.SchedulingRuleId.ShouldBe(targetRuleId);
        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is ContractChangedEvent &&
                ((ContractChangedEvent)e).ContractId == contract.Id &&
                ((ContractChangedEvent)e).ClientId == null &&
                ((ContractChangedEvent)e).RecalculationFrom == DateOnly.FromDateTime(ValidFrom) &&
                ((ContractChangedEvent)e).RecalculationUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnknownContractId_IsSkippedWithoutPersistingOrDispatching()
    {
        var unknownId = Guid.NewGuid();
        _repository.Get(unknownId).Returns((Contract?)null);

        var result = await _handler.Handle(Command((unknownId, Guid.NewGuid())), CancellationToken.None);

        result.ShouldBe(0);
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnknownContractInBatch_DoesNotBlockTheRemainingMigrations()
    {
        var contract = BuildContract(Guid.NewGuid());
        var unknownId = Guid.NewGuid();
        var targetRuleId = Guid.NewGuid();
        _repository.Get(contract.Id).Returns(contract);
        _repository.Get(unknownId).Returns((Contract?)null);

        var result = await _handler.Handle(
            Command((unknownId, targetRuleId), (contract.Id, targetRuleId)),
            CancellationToken.None);

        result.ShouldBe(1);
        contract.SchedulingRuleId.ShouldBe(targetRuleId);
        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_TargetEqualsCurrentRule_ChangesNothing()
    {
        var currentRuleId = Guid.NewGuid();
        var contract = BuildContract(Guid.NewGuid(), currentRuleId);
        _repository.Get(contract.Id).Returns(contract);

        var result = await _handler.Handle(Command((contract.Id, currentRuleId)), CancellationToken.None);

        result.ShouldBe(0);
        contract.SchedulingRuleId.ShouldBe(currentRuleId);
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NullTarget_ClearsTheAssignmentAndDispatches()
    {
        var contract = BuildContract(Guid.NewGuid(), Guid.NewGuid());
        _repository.Get(contract.Id).Returns(contract);

        var result = await _handler.Handle(Command((contract.Id, (Guid?)null)), CancellationToken.None);

        result.ShouldBe(1);
        contract.SchedulingRuleId.ShouldBeNull();
        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NullTargetOnAlreadyUnassignedContract_ChangesNothing()
    {
        var contract = BuildContract(Guid.NewGuid());
        _repository.Get(contract.Id).Returns(contract);

        var result = await _handler.Handle(Command((contract.Id, (Guid?)null)), CancellationToken.None);

        result.ShouldBe(0);
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_SeveralMoves_DispatchesOneEventPerMovedContract()
    {
        var first = BuildContract(Guid.NewGuid());
        var second = BuildContract(Guid.NewGuid());
        var targetRuleId = Guid.NewGuid();
        _repository.Get(first.Id).Returns(first);
        _repository.Get(second.Id).Returns(second);

        var result = await _handler.Handle(
            Command((first.Id, targetRuleId), (second.Id, targetRuleId)),
            CancellationToken.None);

        result.ShouldBe(2);
        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e => e is ContractChangedEvent && ((ContractChangedEvent)e).ContractId == first.Id),
            Arg.Any<CancellationToken>());
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e => e is ContractChangedEvent && ((ContractChangedEvent)e).ContractId == second.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_OnlyListedContractsAreTouched()
    {
        var listed = BuildContract(Guid.NewGuid());
        var unlisted = BuildContract(Guid.NewGuid());
        _repository.Get(listed.Id).Returns(listed);

        await _handler.Handle(Command((listed.Id, Guid.NewGuid())), CancellationToken.None);

        await _repository.Received(1).Get(listed.Id);
        await _repository.DidNotReceive().Get(unlisted.Id);
        unlisted.SchedulingRuleId.ShouldBeNull();
    }

    private static MigrateContractSchedulingRulesCommand Command(params (Guid ContractId, Guid? RuleId)[] assignments)
    {
        return new MigrateContractSchedulingRulesCommand(
            assignments.Select(a => new ContractSchedulingRuleAssignment(a.ContractId, a.RuleId)).ToList());
    }

    private static Contract BuildContract(Guid id, Guid? schedulingRuleId = null) => new()
    {
        Id = id,
        Name = "Contract",
        ValidFrom = ValidFrom,
        SchedulingRuleId = schedulingRuleId
    };
}
