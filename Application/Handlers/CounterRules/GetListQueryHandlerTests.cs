// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the CounterRule GetListQueryHandler: maps all active rules and wraps repository
/// failures in an InvalidRequestException.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.CounterRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.CounterRules;

[TestFixture]
public class GetListQueryHandlerTests
{
    private ICounterRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetListQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ICounterRuleRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetListQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetListQueryHandler>>());
    }

    [Test]
    public async Task Handle_ReturnsMappedResources()
    {
        _repository.GetAllActiveAsync().Returns(
        [
            new CounterRule { Id = Guid.NewGuid(), EventType = CounterEventType.NightShift, Period = CounterPeriod.Year, Threshold = 25 },
            new CounterRule { Id = Guid.NewGuid(), EventType = CounterEventType.WorkedDayInWeek, Period = CounterPeriod.Week, Threshold = 6 },
        ]);

        var result = await _handler.Handle(new ListQuery<CounterRuleResource>(), CancellationToken.None);

        result.Count().ShouldBe(2);
    }

    [Test]
    public async Task Handle_RepositoryThrows_WrapsInInvalidRequestException()
    {
        _repository.GetAllActiveAsync().Returns<Task<List<CounterRule>>>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new ListQuery<CounterRuleResource>(), CancellationToken.None));
    }
}
