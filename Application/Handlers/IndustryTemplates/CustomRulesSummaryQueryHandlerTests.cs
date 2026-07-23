// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CustomRulesSummaryQueryHandler: the returned count is exactly whatever the repository
/// reports for custom (empty-Industry) rules, whether zero or several - industry-bound rules are
/// the repository's responsibility to exclude and are verified separately on
/// SchedulingRuleRepositoryTests.
/// </summary>

using Klacks.Api.Application.Handlers.IndustryTemplates;
using Klacks.Api.Application.Queries.IndustryTemplates;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.IndustryTemplates;

[TestFixture]
public class CustomRulesSummaryQueryHandlerTests
{
    private ISchedulingRuleRepository _schedulingRuleRepository = null!;
    private CustomRulesSummaryQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _schedulingRuleRepository = Substitute.For<ISchedulingRuleRepository>();
        _handler = new CustomRulesSummaryQueryHandler(
            _schedulingRuleRepository,
            Substitute.For<ILogger<CustomRulesSummaryQueryHandler>>());
    }

    [Test]
    public async Task Handle_NoCustomRules_ReturnsZero()
    {
        _schedulingRuleRepository.GetCustomRuleCountAsync().Returns(0);

        var result = await _handler.Handle(new CustomRulesSummaryQuery(), CancellationToken.None);

        result.CustomSchedulingRuleCount.ShouldBe(0);
    }

    [Test]
    public async Task Handle_SeveralCustomRules_ReturnsRepositoryCount()
    {
        _schedulingRuleRepository.GetCustomRuleCountAsync().Returns(7);

        var result = await _handler.Handle(new CustomRulesSummaryQuery(), CancellationToken.None);

        result.CustomSchedulingRuleCount.ShouldBe(7);
    }
}
