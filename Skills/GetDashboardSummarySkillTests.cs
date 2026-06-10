// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_dashboard_summary: the skill sends GetShiftCoverageStatisticsQuery and
/// aggregates per-group coverage and sealing figures into overall totals; empty or null
/// statistics yield a zero-totals success.
/// </summary>

using Klacks.Api.Application.DTOs.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetDashboardSummarySkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task Summary_AggregatesGroupStatistics()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetShiftCoverageStatisticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftCoverageStatisticsResource>
            {
                new()
                {
                    GroupId = Guid.NewGuid(),
                    GroupName = "Alpha",
                    TotalSlots = 10,
                    CoveredSlots = 8,
                    TotalWorkEntries = 8,
                    SealedWorkEntries = 4
                },
                new()
                {
                    GroupId = Guid.NewGuid(),
                    GroupName = "Beta",
                    TotalSlots = 20,
                    CoveredSlots = 10,
                    TotalWorkEntries = 10,
                    SealedWorkEntries = 10
                }
            });
        var skill = new GetDashboardSummarySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("18/30 slots covered");
        result.Message.ShouldContain("2 group(s)");
        result.Message.ShouldContain("12 open");
        result.Message.ShouldContain("14/18 work entries sealed");
        await mediator.Received(1).Send(
            Arg.Any<GetShiftCoverageStatisticsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Summary_Empty_ReturnsZeroTotals()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetShiftCoverageStatisticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftCoverageStatisticsResource>());
        var skill = new GetDashboardSummarySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("0/0 slots covered");
        result.Message.ShouldContain("0 group(s)");
    }

    [Test]
    public async Task Summary_NullStatistics_ReturnsZeroTotals()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetShiftCoverageStatisticsQuery>(), Arg.Any<CancellationToken>())
            .Returns((IEnumerable<ShiftCoverageStatisticsResource>?)null);
        var skill = new GetDashboardSummarySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("0/0 slots covered");
    }
}
