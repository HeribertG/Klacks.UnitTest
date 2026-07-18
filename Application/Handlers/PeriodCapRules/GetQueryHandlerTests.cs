// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the PeriodCapRule GetQueryHandler: returns the mapped resource on success, throws
/// KeyNotFoundException for an unknown Id.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.PeriodCapRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.PeriodCapRules;

[TestFixture]
public class GetQueryHandlerTests
{
    private IPeriodCapRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private GetQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IPeriodCapRuleRepository>();
        _mapper = new ScheduleMapper();

        _handler = new GetQueryHandler(_repository, _mapper, Substitute.For<ILogger<GetQueryHandler>>());
    }

    [Test]
    public async Task Handle_ExistingRule_ReturnsMappedResource()
    {
        var id = Guid.NewGuid();
        var rule = new PeriodCapRule
        {
            Id = id,
            Period = PeriodCapPeriod.CustomWeeks,
            Scope = PeriodCapScope.OvertimeHours,
            CapHours = 0m,
            RollingWindowWeeks = 17,
            MaxAverageWeeklyHours = 48m,
            CustomPeriodWeeks = 17,
            WarnAtPercent = 80,
        };
        _repository.GetAsync(id).Returns(rule);

        var result = await _handler.Handle(new GetQuery<PeriodCapRuleResource>(id), CancellationToken.None);

        result.Id.ShouldBe(id);
        result.Period.ShouldBe(PeriodCapPeriod.CustomWeeks);
        result.Scope.ShouldBe(PeriodCapScope.OvertimeHours);
        result.RollingWindowWeeks.ShouldBe(17);
        result.MaxAverageWeeklyHours.ShouldBe(48m);
        result.CustomPeriodWeeks.ShouldBe(17);
        result.WarnAtPercent.ShouldBe(80);
    }

    [Test]
    public async Task Handle_UnknownId_ThrowsKeyNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((PeriodCapRule?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetQuery<PeriodCapRuleResource>(id), CancellationToken.None));
    }
}
