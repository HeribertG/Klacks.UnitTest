// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the PeriodCapRule GetListQueryHandler: maps all active rules and wraps repository
/// failures in an InvalidRequestException.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.PeriodCapRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.PeriodCapRules;

[TestFixture]
public class GetListQueryHandlerTests
{
    private IPeriodCapRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetListQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IPeriodCapRuleRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetListQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetListQueryHandler>>());
    }

    [Test]
    public async Task Handle_ReturnsMappedResources()
    {
        _repository.GetAllActiveAsync().Returns(
        [
            new PeriodCapRule { Id = Guid.NewGuid(), Period = PeriodCapPeriod.Month, Scope = PeriodCapScope.TotalHours, CapHours = 45m },
            new PeriodCapRule { Id = Guid.NewGuid(), Period = PeriodCapPeriod.Year, Scope = PeriodCapScope.OvertimeHours, CapHours = 180m },
        ]);

        var result = await _handler.Handle(new ListQuery<PeriodCapRuleResource>(), CancellationToken.None);

        result.Count().ShouldBe(2);
    }

    [Test]
    public async Task Handle_RepositoryThrows_WrapsInInvalidRequestException()
    {
        _repository.GetAllActiveAsync().Returns<Task<List<PeriodCapRule>>>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new ListQuery<PeriodCapRuleResource>(), CancellationToken.None));
    }
}
