// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the CounterRule GetQueryHandler: returns the mapped resource on success, throws
/// KeyNotFoundException for an unknown Id.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.CounterRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.CounterRules;

[TestFixture]
public class GetQueryHandlerTests
{
    private ICounterRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ICounterRuleRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetQueryHandler>>());
    }

    [Test]
    public async Task Handle_ExistingRule_ReturnsMappedResource()
    {
        var id = Guid.NewGuid();
        var rule = new CounterRule
        {
            Id = id,
            EventType = CounterEventType.ShiftExceedingHours,
            Period = CounterPeriod.Month,
            Threshold = 3,
            HoursThreshold = 13m,
        };
        _repository.GetAsync(id).Returns(rule);

        var result = await _handler.Handle(new GetQuery<CounterRuleResource>(id), CancellationToken.None);

        result.Id.ShouldBe(id);
        result.EventType.ShouldBe(CounterEventType.ShiftExceedingHours);
        result.HoursThreshold.ShouldBe(13m);
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((CounterRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetQuery<CounterRuleResource>(id), CancellationToken.None));
    }
}
