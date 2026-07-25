// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the IndividualPeriod DeleteCommandHandler: blocks deletion while an active contract
/// still references the individual period, and returns null when it does not exist.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.IndividualPeriods;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.IndividualPeriods;

[TestFixture]
public class DeleteCommandHandlerTests
{
    private IIndividualPeriodRepository _repository = null!;
    private IContractRepository _contractRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IIndividualPeriodRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new DeleteCommandHandler(
            _repository,
            _contractRepository,
            _mapper,
            _unitOfWork,
            Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    [Test]
    public async Task Handle_ExistingWithoutActiveContracts_DeletesAndReturnsResource()
    {
        var id = Guid.NewGuid();
        var entity = new IndividualPeriod { Id = id, Name = "Cycle", Periods = [] };
        _repository.Get(id).Returns(entity);
        _contractRepository.CountActiveContractsByIndividualPeriodAsync(id).Returns(0);

        var result = await _handler.Handle(new DeleteCommand<IndividualPeriodResource>(id), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Cycle");
        await _repository.Received(1).Delete(id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NotFound_ReturnsNullWithoutCheckingContracts()
    {
        var id = Guid.NewGuid();
        _repository.Get(id).Returns((IndividualPeriod?)null);

        var result = await _handler.Handle(new DeleteCommand<IndividualPeriodResource>(id), CancellationToken.None);

        result.ShouldBeNull();
        await _contractRepository.DidNotReceive().CountActiveContractsByIndividualPeriodAsync(Arg.Any<Guid>());
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task Handle_ActiveContractReferencesIt_ThrowsAndDoesNotDelete()
    {
        var id = Guid.NewGuid();
        var entity = new IndividualPeriod { Id = id, Name = "Cycle", Periods = [] };
        _repository.Get(id).Returns(entity);
        _contractRepository.CountActiveContractsByIndividualPeriodAsync(id).Returns(2);

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new DeleteCommand<IndividualPeriodResource>(id), CancellationToken.None));

        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
