// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the RestrictedTimeWindowRule GetListQueryHandler: maps all active rules and wraps
/// repository failures in an InvalidRequestException.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.RestrictedTimeWindowRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.RestrictedTimeWindowRules;

[TestFixture]
public class GetListQueryHandlerTests
{
    private IRestrictedTimeWindowRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetListQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetListQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetListQueryHandler>>());
    }

    [Test]
    public async Task Handle_ReturnsMappedResources()
    {
        _repository.GetAllActiveAsync().Returns(
        [
            new RestrictedTimeWindowRule { Id = Guid.NewGuid(), SeasonFromMonth = 6, SeasonFromDay = 15, SeasonToMonth = 9, SeasonToDay = 15, DailyStart = new TimeOnly(12, 30), DailyEnd = new TimeOnly(15, 0), AppliesToGroupTag = "outdoor" },
            new RestrictedTimeWindowRule { Id = Guid.NewGuid(), SeasonFromMonth = 11, SeasonFromDay = 1, SeasonToMonth = 2, SeasonToDay = 15, DailyStart = new TimeOnly(22, 0), DailyEnd = new TimeOnly(6, 0), AppliesToGroupTag = "night" },
        ]);

        var result = await _handler.Handle(new ListQuery<RestrictedTimeWindowRuleResource>(), CancellationToken.None);

        result.Count().ShouldBe(2);
    }

    [Test]
    public async Task Handle_RepositoryThrows_WrapsInInvalidRequestException()
    {
        _repository.GetAllActiveAsync().Returns<Task<List<RestrictedTimeWindowRule>>>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new ListQuery<RestrictedTimeWindowRuleResource>(), CancellationToken.None));
    }
}
