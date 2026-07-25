// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the IndividualPeriod PutCommandHandler: validates periods before persisting, blocks
/// the repository call when validation fails, and returns null when the repository reports the
/// individual period as not found.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.IndividualPeriods;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.IndividualPeriods;

[TestFixture]
public class PutCommandHandlerTests
{
    private IIndividualPeriodRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IIndividualPeriodRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PutCommandHandler(
            _repository,
            _mapper,
            _unitOfWork,
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    [Test]
    public async Task Handle_ExistingIndividualPeriod_UpdatesAndReturnsResource()
    {
        var id = Guid.NewGuid();
        var resource = new IndividualPeriodResource { Id = id, Name = "Updated", Periods = [] };
        var updatedEntity = new IndividualPeriod { Id = id, Name = "Updated", Periods = [] };

        _repository.Put(Arg.Any<IndividualPeriod>()).Returns(updatedEntity);

        var result = await _handler.Handle(new PutCommand<IndividualPeriodResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Updated");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NotFound_ReturnsNullAndDoesNotComplete()
    {
        _repository.Put(Arg.Any<IndividualPeriod>()).Returns((IndividualPeriod?)null);

        var resource = new IndividualPeriodResource { Id = Guid.NewGuid(), Name = "Missing", Periods = [] };

        var result = await _handler.Handle(new PutCommand<IndividualPeriodResource>(resource), CancellationToken.None);

        result.ShouldBeNull();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_NegativeFullHours_ThrowsAndDoesNotCallRepositoryPut()
    {
        var resource = new IndividualPeriodResource
        {
            Id = Guid.NewGuid(),
            Name = "Invalid",
            Periods = [new PeriodResource { FromDate = new DateOnly(2026, 1, 1), FullHours = -5m }],
        };

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<IndividualPeriodResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().Put(Arg.Any<IndividualPeriod>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_UntilDateBeforeFromDate_ThrowsAndDoesNotCallRepositoryPut()
    {
        var resource = new IndividualPeriodResource
        {
            Id = Guid.NewGuid(),
            Name = "Invalid",
            Periods =
            [
                new PeriodResource
                {
                    FromDate = new DateOnly(2026, 2, 1),
                    UntilDate = new DateOnly(2026, 1, 1),
                    FullHours = 160m,
                },
            ],
        };

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<IndividualPeriodResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().Put(Arg.Any<IndividualPeriod>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
