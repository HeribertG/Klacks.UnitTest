// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the IndividualPeriod ListQueryHandler: maps every repository row to a resource.
/// </summary>

using Klacks.Api.Application.Handlers.IndividualPeriods;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.DTOs.Schedules;

namespace Klacks.UnitTest.Application.Handlers.IndividualPeriods;

[TestFixture]
public class ListQueryHandlerTests
{
    private IIndividualPeriodRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private ListQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IIndividualPeriodRepository>();
        _mapper = new ScheduleMapper();

        _handler = new ListQueryHandler(_repository, _mapper);
    }

    [Test]
    public async Task Handle_ReturnsAllMappedResources()
    {
        var list = new List<IndividualPeriod>
        {
            new() { Id = Guid.NewGuid(), Name = "A", Periods = [] },
            new() { Id = Guid.NewGuid(), Name = "B", Periods = [] },
        };
        _repository.List().Returns(list);

        var result = await _handler.Handle(new ListQuery<IndividualPeriodResource>(), CancellationToken.None);

        result.Select(r => r.Name).ShouldBe(["A", "B"], ignoreOrder: true);
    }
}
