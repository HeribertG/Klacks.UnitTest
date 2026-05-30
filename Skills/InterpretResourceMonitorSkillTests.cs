// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the interpret_resource_monitor skill: parameter validation, per-month
/// over-/under-staffing interpretation, critical-month detection and current-month coverage
/// filtering. The Data payload is asserted via its serialized JSON shape (internal anonymous types).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Dashboard;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class InterpretResourceMonitorSkillTests
{
    private const int Year = 2026;
    private static readonly Guid GroupId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static ResourceMonitorDayResource Day(
        int month, int day, double dienst, double max, double wunsch = 0, double total = 0, double absence = 0)
        => new()
        {
            Date = new DateOnly(Year, month, day),
            DienstCount = dienst,
            MaxCount = max,
            WunschCount = wunsch,
            TotalCount = total,
            AbsenzCount = absence
        };

    private static ShiftCoverageStatisticsResource Coverage(Guid groupId, int total, int covered)
        => new()
        {
            GroupId = groupId,
            GroupName = "Group",
            TotalSlots = total,
            CoveredSlots = covered,
            TotalWorkEntries = covered,
            SealedWorkEntries = 0
        };

    private static IMediator MediatorWith(
        IEnumerable<ResourceMonitorDayResource> days,
        IEnumerable<ShiftCoverageStatisticsResource>? coverage = null)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetResourceMonitorQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ResourceMonitorResource { DailyData = days.ToList() });
        mediator.Send(Arg.Any<GetShiftCoverageStatisticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(coverage ?? new List<ShiftCoverageStatisticsResource>());
        return mediator;
    }

    private static Dictionary<string, object> Params(object? groupId = null)
    {
        var p = new Dictionary<string, object> { ["year"] = Year };
        if (groupId != null)
        {
            p["groupId"] = groupId;
        }
        return p;
    }

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task Interprets_UnderstaffedAndTightDays_PerMonth()
    {
        var days = new List<ResourceMonitorDayResource>
        {
            Day(month: 1, day: 1, dienst: 5, max: 3, wunsch: 2),  // understaffed: demand > max
            Day(month: 1, day: 2, dienst: 2, max: 3, wunsch: 1),  // tight: above desired, within max
            Day(month: 2, day: 1, dienst: 1, max: 5, wunsch: 3)   // fine
        };
        var skill = new InterpretResourceMonitorSkill(MediatorWith(days));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("TotalDays").GetInt32().ShouldBe(3);
        data.GetProperty("UnderstaffedDays").GetInt32().ShouldBe(1);
        data.GetProperty("TightDays").GetInt32().ShouldBe(1);

        var critical = data.GetProperty("CriticalMonths").EnumerateArray().Select(e => e.GetInt32()).ToList();
        critical.ShouldBe(new[] { 1 });

        var months = data.GetProperty("Months").EnumerateArray().ToList();
        months.Count.ShouldBe(2);
        var january = months.First(m => m.GetProperty("Month").GetInt32() == 1);
        january.GetProperty("UnderstaffedDays").GetInt32().ShouldBe(1);
        january.GetProperty("TightDays").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task Coverage_FilteredToRequestedGroup()
    {
        var otherGroup = Guid.NewGuid();
        var coverage = new List<ShiftCoverageStatisticsResource>
        {
            Coverage(GroupId, total: 10, covered: 7),
            Coverage(otherGroup, total: 4, covered: 4)
        };
        var skill = new InterpretResourceMonitorSkill(
            MediatorWith(new[] { Day(1, 1, 1, 5) }, coverage));

        var result = await skill.ExecuteAsync(Ctx(), Params(GroupId.ToString()));

        var cov = DataAsJson(result).GetProperty("CurrentMonthCoverage").EnumerateArray().ToList();
        cov.Count.ShouldBe(1);
        cov[0].GetProperty("UncoveredSlots").GetInt32().ShouldBe(3);
        cov[0].GetProperty("CoverageRatio").GetDouble().ShouldBe(0.7);
    }

    [Test]
    public async Task NoGroupFilter_IncludesAllCoverage()
    {
        var coverage = new List<ShiftCoverageStatisticsResource>
        {
            Coverage(GroupId, 10, 7),
            Coverage(Guid.NewGuid(), 4, 4)
        };
        var skill = new InterpretResourceMonitorSkill(
            MediatorWith(new[] { Day(1, 1, 1, 5) }, coverage));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        DataAsJson(result).GetProperty("CurrentMonthCoverage").GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task NoData_ReturnsEmptySuccess()
    {
        var skill = new InterpretResourceMonitorSkill(MediatorWith(new List<ResourceMonitorDayResource>()));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("TotalDays").GetInt32().ShouldBe(0);
        result.Message.ShouldContain("No resource-monitor data");
    }

    [Test]
    public async Task InvalidYear_ReturnsError()
    {
        var skill = new InterpretResourceMonitorSkill(MediatorWith(new List<ResourceMonitorDayResource>()));
        var p = Params();
        p["year"] = 1500;

        var result = await skill.ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("year");
    }

    [Test]
    public async Task InvalidGroupId_ReturnsError()
    {
        var skill = new InterpretResourceMonitorSkill(MediatorWith(new List<ResourceMonitorDayResource>()));

        var result = await skill.ExecuteAsync(Ctx(), Params("not-a-guid"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("groupId");
    }
}
