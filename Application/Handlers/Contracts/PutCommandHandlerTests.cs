// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the Contract PutCommandHandler: a committed update of calculation-relevant fields
/// dispatches a ContractChangedEvent whose window starts at the earlier of the old and new ValidFrom;
/// a name-only update never dispatches.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Handlers.Contracts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Events;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Contracts;

[TestFixture]
public class PutCommandHandlerTests
{
    private IContractRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IContractRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();

        _handler = new PutCommandHandler(
            _repository,
            _mapper,
            _unitOfWork,
            _eventDispatcher,
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    [Test]
    public async Task Handle_NightRateChanged_DispatchesContractChangedEventFromEarlierValidFrom()
    {
        var contractId = Guid.NewGuid();
        var existing = BuildContract(contractId, validFrom: new DateTime(2026, 4, 1), nightRate: 0.10m);
        _repository.Get(contractId).Returns(existing);

        var resource = BuildResource(contractId, validFrom: new DateTime(2026, 2, 1), nightRate: 0.25m);

        await _handler.Handle(new PutCommand<ContractResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is ContractChangedEvent &&
                ((ContractChangedEvent)e).ContractId == contractId &&
                ((ContractChangedEvent)e).ClientId == null &&
                ((ContractChangedEvent)e).RecalculationFrom == new DateOnly(2026, 2, 1) &&
                ((ContractChangedEvent)e).RecalculationUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NameOnlyChanged_DoesNotDispatch()
    {
        var contractId = Guid.NewGuid();
        var existing = BuildContract(contractId, validFrom: new DateTime(2026, 4, 1), nightRate: 0.10m);
        existing.Name = "Old name";
        _repository.Get(contractId).Returns(existing);

        var resource = BuildResource(contractId, validFrom: new DateTime(2026, 4, 1), nightRate: 0.10m);
        resource.Name = "New name";

        await _handler.Handle(new PutCommand<ContractResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ContractNotFound_ThrowsAndDoesNotDispatch()
    {
        var contractId = Guid.NewGuid();
        _repository.Get(contractId).Returns((Contract?)null);

        var resource = BuildResource(contractId, validFrom: new DateTime(2026, 4, 1), nightRate: 0.10m);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new PutCommand<ContractResource>(resource), CancellationToken.None));

        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    private static Contract BuildContract(Guid id, DateTime validFrom, decimal nightRate)
    {
        return new Contract
        {
            Id = id,
            Name = "Contract",
            GuaranteedHours = 100m,
            MinimumHours = 80m,
            MaximumHours = 120m,
            FullTime = 0m,
            NightRate = nightRate,
            HolidayRate = 0m,
            WE1Rate = 0m,
            WE2Rate = 0m,
            WE3Rate = 0m,
            PaymentInterval = Klacks.Api.Domain.Enums.PaymentInterval.Monthly,
            ValidFrom = validFrom,
        };
    }

    private static ContractResource BuildResource(Guid id, DateTime validFrom, decimal nightRate)
    {
        return new ContractResource
        {
            Id = id,
            Name = "Contract",
            GuaranteedHours = 100m,
            MinimumHours = 80m,
            MaximumHours = 120m,
            NightRate = nightRate,
            PaymentInterval = Klacks.Api.Domain.Enums.PaymentInterval.Monthly,
            ValidFrom = validFrom,
        };
    }
}
