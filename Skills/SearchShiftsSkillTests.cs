// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for search_shifts: the skill builds a ShiftFilter (search string, page size,
/// all date ranges, client included) for GetTruncatedListQuery and projects the result to a
/// compact list with a derived client name; invalid maxResults aborts without dispatch.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SearchShiftsSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static TruncatedShiftResource Result(params ShiftResource[] shifts) => new()
    {
        Shifts = shifts.ToList(),
        MaxItems = shifts.Length
    };

    [Test]
    public async Task SearchShifts_DispatchesFilter_AndProjectsCompactList()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result(
                new ShiftResource
                {
                    Id = Guid.NewGuid(),
                    Name = "Early shift",
                    Abbreviation = "ES",
                    FromDate = new DateOnly(2026, 1, 1),
                    StartShift = new TimeOnly(6, 0),
                    EndShift = new TimeOnly(14, 0),
                    Client = new ClientResource { Company = "Spitex Bern" }
                },
                new ShiftResource
                {
                    Id = Guid.NewGuid(),
                    Name = "Early support",
                    Abbreviation = "ESU",
                    FromDate = new DateOnly(2026, 2, 1),
                    StartShift = new TimeOnly(7, 0),
                    EndShift = new TimeOnly(15, 0)
                }));
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "early",
            ["maxResults"] = 5
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q =>
                q.Filter.SearchString == "early" &&
                q.Filter.NumberOfItemsPerPage == 5 &&
                q.Filter.ActiveDateRange &&
                q.Filter.FormerDateRange &&
                q.Filter.FutureDateRange &&
                q.Filter.IncludeClientName),
            Arg.Any<CancellationToken>());

        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("TotalCount").GetInt32().ShouldBe(2);
        data.GetProperty("Shifts").GetArrayLength().ShouldBe(2);
        data.GetProperty("Shifts")[0].GetProperty("Name").GetString().ShouldBe("Early shift");
        data.GetProperty("Shifts")[0].GetProperty("ClientName").GetString().ShouldBe("Spitex Bern");
        data.GetProperty("Shifts")[1].GetProperty("ClientName").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task SearchShifts_WithoutParameters_UsesDefaultMaxResults()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result());
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q =>
                q.Filter.SearchString == string.Empty &&
                q.Filter.NumberOfItemsPerPage == 20),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchShifts_InvalidMaxResults_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["maxResults"] = 0
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>());
    }
}
