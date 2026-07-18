// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the RestrictedTimeWindowRule GetQueryHandler: returns the mapped resource on success,
/// throws KeyNotFoundException for an unknown Id.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.RestrictedTimeWindowRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.RestrictedTimeWindowRules;

[TestFixture]
public class GetQueryHandlerTests
{
    private IRestrictedTimeWindowRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetQueryHandler>>());
    }

    [Test]
    public async Task Handle_ExistingRule_ReturnsMappedResource()
    {
        var id = Guid.NewGuid();
        var rule = new RestrictedTimeWindowRule
        {
            Id = id,
            SeasonFromMonth = 6,
            SeasonFromDay = 15,
            SeasonToMonth = 9,
            SeasonToDay = 15,
            DailyStart = new TimeOnly(12, 30),
            DailyEnd = new TimeOnly(15, 0),
            AppliesToGroupTag = "outdoor",
        };
        _repository.GetAsync(id).Returns(rule);

        var result = await _handler.Handle(new GetQuery<RestrictedTimeWindowRuleResource>(id), CancellationToken.None);

        result.Id.ShouldBe(id);
        result.SeasonFromMonth.ShouldBe(6);
        result.SeasonFromDay.ShouldBe(15);
        result.SeasonToMonth.ShouldBe(9);
        result.SeasonToDay.ShouldBe(15);
        result.DailyStart.ShouldBe(new TimeOnly(12, 30));
        result.DailyEnd.ShouldBe(new TimeOnly(15, 0));
        result.AppliesToGroupTag.ShouldBe("outdoor");
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((RestrictedTimeWindowRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetQuery<RestrictedTimeWindowRuleResource>(id), CancellationToken.None));
    }
}
