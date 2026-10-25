// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
using Klacks.UnitTest.TestHelpers;

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
    public async Task SearchShifts_WithoutStatus_ListsThePlannableShifts_NotTheUnsealedOrderInbox()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>()).Returns(Result());
        var skill = new SearchShiftsSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q =>
                q.Filter.FilterType == ShiftFilterType.Shift &&
                !q.Filter.IsSealedOrder &&
                q.Filter.IsTimeRange &&
                q.Filter.IsSporadic),
            Arg.Any<CancellationToken>());
    }

    [TestCase("shift", ShiftFilterType.Shift, false)]
    [TestCase("draft_order", ShiftFilterType.Original, false)]
    [TestCase("sealed_order", ShiftFilterType.Original, true)]
    [TestCase("container", ShiftFilterType.Container, false)]
    [TestCase("SEALED_ORDER", ShiftFilterType.Original, true)]
    public async Task SearchShifts_Status_SelectsTheMatchingListView(
        string status, ShiftFilterType expectedType, bool expectedSealed)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>()).Returns(Result());
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["status"] = status });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q =>
                q.Filter.FilterType == expectedType &&
                q.Filter.IsSealedOrder == expectedSealed &&
                q.Filter.IsTimeRange &&
                q.Filter.IsSporadic &&
                q.Filter.ActiveDateRange &&
                q.Filter.FormerDateRange &&
                q.Filter.FutureDateRange),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchShifts_UnknownStatus_ReturnsErrorListingTheValidOnes_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["status"] = "archived" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("shift");
        result.Message.ShouldContain("draft_order");
        result.Message.ShouldContain("sealed_order");
        result.Message.ShouldContain("container");
        await mediator.DidNotReceive().Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchShifts_FuzzyFallback_ReadsTheCatalogOfTheSameListView()
    {
        var mediator = MediatorWithCatalog(
            "Sonderdienst",
            new ShiftResource { Id = Guid.NewGuid(), Name = "AllShift" });
        var skill = new SearchShiftsSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "Sonderdienst",
            ["status"] = "sealed_order"
        });

        await mediator.Received(2).Send(
            Arg.Is<GetTruncatedListQuery>(q =>
                q.Filter.FilterType == ShiftFilterType.Original && q.Filter.IsSealedOrder),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchShifts_ReportsEachShiftsStatus_SoFollowUpSkillsKnowWhatTheyGot()
    {
        var mediator = Substitute.For<IMediator>();
        var sealedOrderId = Guid.NewGuid();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result(new ShiftResource
            {
                Id = Guid.NewGuid(),
                Name = "Frühdienst",
                Status = ShiftStatus.OriginalShift,
                OriginalId = sealedOrderId
            }));
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var shift = JsonSerializer.SerializeToElement(result.Data).GetProperty("Shifts")[0];
        shift.GetProperty("Status").GetString().ShouldBe("OriginalShift");
        shift.GetProperty("OriginalId").GetGuid().ShouldBe(sealedOrderId);
    }

    [Test]
    public void SearchShifts_SeedDeclaresTheStatusEnum_WithThePlannableShiftsAsDefault()
    {
        var seed = SkillSeedCatalogue.Get("search_shifts");

        var status = seed.Parameters!.Single(p => p.Name == "status");
        status.Required.ShouldBeFalse();
        status.DefaultValue?.ToString().ShouldBe("shift");
        status.EnumValues.ShouldBe(new List<string> { "shift", "draft_order", "sealed_order", "container" });
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

    private static IMediator MediatorWithCatalog(string filteredSearch, params ShiftResource[] catalog)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(
                Arg.Is<GetTruncatedListQuery>(q => q.Filter.SearchString == filteredSearch),
                Arg.Any<CancellationToken>())
            .Returns(Result());
        mediator.Send(
                Arg.Is<GetTruncatedListQuery>(q => q.Filter.SearchString == string.Empty),
                Arg.Any<CancellationToken>())
            .Returns(Result(catalog));
        return mediator;
    }

    [Test]
    public async Task SearchShifts_EmptyDbFilter_FuzzyFallback_ResolvesClosestShift()
    {
        // 2026-07-11 transcript regression: the spoken "Dienst All-Shift" never survives the
        // database substring filter against the camel-case shift "AllShift".
        var allShiftId = Guid.NewGuid();
        var mediator = MediatorWithCatalog(
            "Dienst All-Shift",
            new ShiftResource { Id = allShiftId, Name = "AllShift", Abbreviation = "AS" },
            new ShiftResource { Id = Guid.NewGuid(), Name = "Nachtdienst", Abbreviation = "ND" });
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "Dienst All-Shift"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("closest shift is 'AllShift'");
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("TotalCount").GetInt32().ShouldBe(1);
        data.GetProperty("Shifts")[0].GetProperty("Id").GetGuid().ShouldBe(allShiftId);
    }

    [Test]
    public async Task SearchShifts_EmptyDbFilter_TypoOnlyMatch_AsksBackWithoutReturningARow()
    {
        var mediator = MediatorWithCatalog(
            "Nachtdinst",
            new ShiftResource { Id = Guid.NewGuid(), Name = "Nachtdienst", Abbreviation = "ND" },
            new ShiftResource { Id = Guid.NewGuid(), Name = "Frühdienst", Abbreviation = "FD" });
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "Nachtdinst"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(NameResolutionMessages.SuggestedName("Nachtdienst"));
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("TotalCount").GetInt32().ShouldBe(0);
        data.GetProperty("Shifts").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task SearchShifts_EmptyDbFilter_SeveralPlausibleShifts_AsksForDisambiguation()
    {
        var mediator = MediatorWithCatalog(
            "All Schift",
            new ShiftResource { Id = Guid.NewGuid(), Name = "AllShift" },
            new ShiftResource { Id = Guid.NewGuid(), Name = "AllShift2" });
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "All Schift"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("ambiguous");
        result.Message.ShouldContain("AllShift");
        result.Message.ShouldContain("AllShift2");
        result.Message.ShouldContain("do not guess");
    }

    [Test]
    public async Task SearchShifts_EmptyDbFilter_NoPlausibleShift_ListsAvailableNames()
    {
        var mediator = MediatorWithCatalog(
            "Sonderdienst",
            new ShiftResource { Id = Guid.NewGuid(), Name = "AllShift" },
            new ShiftResource { Id = Guid.NewGuid(), Name = "Nachtdienst" });
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "Sonderdienst"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Found 0 shift(s)");
        result.Message.ShouldContain("Available shifts include");
        result.Message.ShouldContain("AllShift");
        result.Message.ShouldContain("do not invent");
    }

    [Test]
    public async Task SearchShifts_LocationParameters_ReachTheFilter_AndTheMessageNamesTheOrderLocation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result(new ShiftResource { Id = Guid.NewGuid(), Name = "24h Bern" }));
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["status"] = "draft_order",
            ["city"] = " Bern ",
            ["state"] = "Bern",
            ["country"] = "Schweiz",
            ["zipPrefix"] = "30"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q =>
                q.Filter.City == "Bern" &&
                q.Filter.State == "Bern" &&
                q.Filter.Country == "Schweiz" &&
                q.Filter.ZipPrefix == "30" &&
                q.Filter.FilterType == ShiftFilterType.Original &&
                !q.Filter.IsSealedOrder),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("whose customer location (workplace, else main address) is in city 'Bern'");
        result.Message.ShouldContain("state 'Bern'");
    }

    [Test]
    public async Task SearchShifts_LocationWithoutHits_SkipsTheFuzzyFallback_AndPointsToStateAndOrderViews()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>()).Returns(Result());
        var skill = new SearchShiftsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchString"] = "24h",
            ["city"] = "Bern"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>());
        result.Message.ShouldContain("Found 0 shift(s)");
        result.Message.ShouldContain("A city and a state can share a name");
        result.Message.ShouldContain("status=draft_order or sealed_order");
        result.Message.ShouldContain("Never say there are no orders in a place");
    }

    [Test]
    public async Task SearchShifts_WithoutLocation_LeavesTheLocationFilterEmpty()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>()).Returns(Result());
        var skill = new SearchShiftsSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["city"] = "  " });

        await mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q => !q.Filter.HasLocationFilter && q.Filter.City == null),
            Arg.Any<CancellationToken>());
    }
}
