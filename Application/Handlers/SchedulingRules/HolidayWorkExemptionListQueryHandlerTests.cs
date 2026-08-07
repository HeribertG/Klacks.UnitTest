// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the HolidayWorkExemptionListQueryHandler: returns active exemptions mapped to
/// resources and ordered by description, and wraps repository failures in an InvalidRequestException.
/// </summary>

using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Application.Queries.SchedulingRules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class HolidayWorkExemptionListQueryHandlerTests
{
    private IHolidayWorkExemptionRuleRepository _repository = null!;
    private HolidayWorkExemptionListQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IHolidayWorkExemptionRuleRepository>();
        _handler = new HolidayWorkExemptionListQueryHandler(
            _repository, Substitute.For<ILogger<HolidayWorkExemptionListQueryHandler>>());
    }

    [Test]
    public async Task Handle_ReturnsResourcesOrderedByDescription()
    {
        _repository.GetAllActiveAsync().Returns(
        [
            new HolidayWorkExemptionRule { Id = Guid.NewGuid(), Description = "Zoo emergency care" },
            new HolidayWorkExemptionRule { Id = Guid.NewGuid(), Description = "Ambulance service" },
        ]);

        var result = await _handler.Handle(new HolidayWorkExemptionListQuery(), CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].Description.ShouldBe("Ambulance service");
        result[1].Description.ShouldBe("Zoo emergency care");
    }

    [Test]
    public async Task Handle_MapsSchedulingRuleIdAndImportSourceKey()
    {
        var ruleId = Guid.NewGuid();
        _repository.GetAllActiveAsync().Returns(
        [
            new HolidayWorkExemptionRule
            {
                Id = Guid.NewGuid(),
                Description = "Security detail",
                SchedulingRuleId = ruleId,
                ImportSourceKey = "region-setup:industryProfiles:security:exemption",
            },
        ]);

        var result = await _handler.Handle(new HolidayWorkExemptionListQuery(), CancellationToken.None);

        result.Single().SchedulingRuleId.ShouldBe(ruleId);
        result.Single().ImportSourceKey.ShouldBe("region-setup:industryProfiles:security:exemption");
    }

    [Test]
    public async Task Handle_RepositoryThrows_WrapsInInvalidRequestException()
    {
        _repository.GetAllActiveAsync().Returns<Task<List<HolidayWorkExemptionRule>>>(_ => throw new InvalidOperationException("db down"));

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new HolidayWorkExemptionListQuery(), CancellationToken.None));
    }
}
