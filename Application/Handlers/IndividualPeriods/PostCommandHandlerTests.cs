// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the IndividualPeriod PostCommandHandler: validates periods before persisting and
/// blocks the repository call when validation fails.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.IndividualPeriods;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.IndividualPeriods;

[TestFixture]
public class PostCommandHandlerTests
{
    private IIndividualPeriodRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IIndividualPeriodRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PostCommandHandler(
            _repository,
            _mapper,
            _unitOfWork,
            Substitute.For<ILogger<PostCommandHandler>>());
    }

    [Test]
    public async Task Handle_ValidResource_AddsAndReturnsResource()
    {
        var resource = new IndividualPeriodResource
        {
            Name = "Custom cycle",
            Periods = [new PeriodResource { FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        };

        var result = await _handler.Handle(new PostCommand<IndividualPeriodResource>(resource), CancellationToken.None);

        await _repository.Received(1).Add(Arg.Is<IndividualPeriod>(ip => ip.Name == "Custom cycle" && ip.Periods.Count == 1));
        await _unitOfWork.Received(1).CompleteAsync();
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Custom cycle");
    }

    [Test]
    public async Task Handle_NegativeFullHours_ThrowsAndDoesNotCallRepository()
    {
        var resource = new IndividualPeriodResource
        {
            Name = "Invalid cycle",
            Periods = [new PeriodResource { FromDate = new DateOnly(2026, 1, 1), FullHours = -1m }],
        };

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<IndividualPeriodResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().Add(Arg.Any<IndividualPeriod>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_UntilDateBeforeFromDate_ThrowsAndDoesNotCallRepository()
    {
        var resource = new IndividualPeriodResource
        {
            Name = "Invalid cycle",
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
            () => _handler.Handle(new PostCommand<IndividualPeriodResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().Add(Arg.Any<IndividualPeriod>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
