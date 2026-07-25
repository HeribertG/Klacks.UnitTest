// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the IndividualPeriod GetQueryHandler: returns the mapped resource when found and
/// propagates KeyNotFoundException when it does not exist.
/// </summary>

using Klacks.Api.Application.Handlers.IndividualPeriods;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.IndividualPeriods;

[TestFixture]
public class GetQueryHandlerTests
{
    private IIndividualPeriodRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IIndividualPeriodRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetQueryHandler>>());
    }

    [Test]
    public async Task Handle_Existing_ReturnsMappedResource()
    {
        var id = Guid.NewGuid();
        var entity = new IndividualPeriod
        {
            Id = id,
            Name = "Cycle",
            Periods = [new Period { Id = Guid.NewGuid(), IndividualPeriodId = id, FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        };
        _repository.Get(id).Returns(entity);

        var result = await _handler.Handle(new GetQuery<IndividualPeriodResource>(id), CancellationToken.None);

        result.Name.ShouldBe("Cycle");
        result.Periods.Count.ShouldBe(1);
    }

    [Test]
    public async Task Handle_NotFound_ThrowsKeyNotFoundException()
    {
        var id = Guid.NewGuid();
        _repository.Get(id).Returns((IndividualPeriod?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetQuery<IndividualPeriodResource>(id), CancellationToken.None));
    }
}
