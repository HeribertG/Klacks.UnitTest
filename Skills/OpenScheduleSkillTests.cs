// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for open_schedule: the route carries the groupId, clientId and date query parameters the
/// schedule page reads. The date is a calendar day written as yyyy-MM-dd without any time zone shift, a
/// relative day word is anchored on the company day, a client is resolved by id or by name through the
/// visibility-aware resolvers, a hidden client reads like an unknown one, and a person scope replaces the
/// group filter exactly like the page does.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class OpenScheduleSkillTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();

    private IGroupSearchRepository _groupSearch = null!;
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _clientSearch = null!;
    private ICompanyClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _groupSearch = Substitute.For<IGroupSearchRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _clientSearch = Substitute.For<IClientSearchRepository>();
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        _clientRepository.Get(ClientId).Returns(
            new Client { Id = ClientId, FirstName = "Anna", Name = "Müller", IdNumber = 7 });
        _groupSearch.SearchAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult
            {
                Items = [new GroupSearchItem { Id = GroupId, Name = "Bern" }],
                TotalCount = 1
            });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string> { "CanViewSchedule" }
    };

    private OpenScheduleSkill Skill(IClientVisibilityGuard? visibility = null) => new(
        _groupSearch, _clientRepository, _clientSearch, visibility ?? SkillClientVisibility.AllVisible(), _clock);

    private static string RouteOf(SkillResult result) =>
        JsonSerializer.SerializeToElement(result.Data).GetProperty("Route").GetString()!;

    [Test]
    public async Task WithoutParameters_OpensThePlainSchedule()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe("/workplace/schedule");
    }

    [Test]
    public async Task GroupName_StillAddsTheGroupId()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern" });

        RouteOf(result).ShouldBe($"/workplace/schedule?groupId={GroupId}");
    }

    [Test]
    public async Task GroupName_ContainedInSeveralGroups_OpensTheGroupWithExactlyThatName()
    {
        var exactId = Guid.NewGuid();
        _groupSearch.SearchAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult
            {
                Items =
                [
                    new GroupSearchItem { Id = Guid.NewGuid(), Name = "Winterthur Nord" },
                    new GroupSearchItem { Id = exactId, Name = "Winterthur" },
                    new GroupSearchItem { Id = Guid.NewGuid(), Name = "Kundendienst Winterthur" },
                ],
                TotalCount = 3
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "winterthur",
            ["date"] = "2026-11-02"
        });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe($"/workplace/schedule?groupId={exactId}&date=2026-11-02");
    }

    [Test]
    public async Task GroupName_ContainedInSeveralGroupsWithoutAnExactMatch_ListsTheCandidates()
    {
        _groupSearch.SearchAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult
            {
                Items =
                [
                    new GroupSearchItem { Id = Guid.NewGuid(), Name = "Winterthur Nord" },
                    new GroupSearchItem { Id = Guid.NewGuid(), Name = "Kundendienst Winterthur" },
                ],
                TotalCount = 2
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = "Winterthur" });

        result.Success.ShouldBeTrue(result.Message);
        JsonSerializer.SerializeToElement(result.Data).TryGetProperty("Route", out _).ShouldBeFalse();
        result.Message.ShouldContain("Multiple groups match");
    }

    [Test]
    public async Task Date_IsWrittenAsTheSameCalendarDay()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["date"] = "2026-03-31" });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe("/workplace/schedule?date=2026-03-31");
    }

    [TestCase("2026-03-31T23:30:00Z", "2026-03-31")]
    [TestCase("2026-03-31T00:30:00+02:00", "2026-03-31")]
    public async Task Date_WithATimeComponent_KeepsTheCalendarDayOfTheValue(string raw, string expectedDay)
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["date"] = raw });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe($"/workplace/schedule?date={expectedDay}");
    }

    [Test]
    public async Task RelativeDay_IsAnchoredOnTheCompanyDay()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["date"] = "tomorrow" });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe("/workplace/schedule?date=2026-10-14");
    }

    [Test]
    public async Task UnreadableDate_IsRejectedInsteadOfDropped()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["date"] = "sometime soon" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("date");
    }

    [Test]
    public async Task GroupAndDate_AreCombinedInOneRoute()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["date"] = "2026-11-02"
        });

        RouteOf(result).ShouldBe($"/workplace/schedule?groupId={GroupId}&date=2026-11-02");
    }

    [Test]
    public async Task ClientId_ScopesTheScheduleToThatPerson()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["date"] = "2026-11-02"
        });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe($"/workplace/schedule?clientId={ClientId}&date=2026-11-02");
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("ClientName").GetString().ShouldBe("Anna Müller");
    }

    [Test]
    public async Task ClientWithoutAnIdNumber_OpensUnfilteredAndSaysSo_BecauseThePageFiltersByThatNumber()
    {
        var withoutNumber = Guid.NewGuid();
        _clientRepository.Get(withoutNumber).Returns(
            new Client { Id = withoutNumber, FirstName = "Berta", Name = "Keller", IdNumber = 0 });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = withoutNumber.ToString(),
            ["date"] = "2026-11-02"
        });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe("/workplace/schedule?date=2026-11-02");
        result.Message.ShouldContain("no ID number");
        result.Message.ShouldContain("without a person filter");
        JsonSerializer.SerializeToElement(result.Data).GetProperty("PersonFilterApplied").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task WithoutADate_TheCompanyClockIsNotAsked()
    {
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern" });

        await _clock.DidNotReceive().GetTodayAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClientId_ReplacesTheGroupFilter_LikeThePageDoes()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["groupName"] = "Bern"
        });

        RouteOf(result).ShouldBe($"/workplace/schedule?clientId={ClientId}");
        await _groupSearch.DidNotReceive().SearchAsync(
            Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClientId_OfAHiddenClient_IsAnsweredLikeAnUnknownId_AndNeverLoaded()
    {
        var hidden = await Skill(SkillClientVisibility.Hiding(ClientId)).ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() });
        var unknownId = Guid.NewGuid();
        var unknown = await Skill().ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["clientId"] = unknownId.ToString() });

        hidden.Success.ShouldBeFalse();
        hidden.Data.ShouldBeNull();
        hidden.Message.ShouldBe(unknown.Message.Replace(unknownId.ToString(), ClientId.ToString()));
        await _clientRepository.DidNotReceive().Get(ClientId);
    }

    [Test]
    public async Task InvalidClientId_IsRejected()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = "not-a-guid" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid client ID format");
    }

    [Test]
    public async Task FirstAndLastName_ResolveTheClientThroughTheScopedSearch()
    {
        _clientSearch.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = ClientId, FirstName = "Anna", LastName = "Müller", IdNumber = 7 }],
                TotalCount = 1
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Anna",
            ["lastName"] = "Müller"
        });

        result.Success.ShouldBeTrue(result.Message);
        RouteOf(result).ShouldBe($"/workplace/schedule?clientId={ClientId}");
    }

    [Test]
    public async Task AmbiguousName_AsksForTheClientNumber_InsteadOfPickingOne()
    {
        _clientSearch.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items =
                [
                    new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Anna", LastName = "Müller", IdNumber = 7 },
                    new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Anna", LastName = "Müller", IdNumber = 9 }
                ],
                TotalCount = 2
            });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Anna",
            ["lastName"] = "Müller"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("#7");
        result.Message.ShouldContain("#9");
        result.Message.ShouldContain("idNumber");
    }

    [Test]
    public async Task UnknownName_IsAnsweredWithTheNotFoundMessage()
    {
        _clientSearch.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = [], TotalCount = 0 });
        _clientSearch.SearchByFuzzyNameAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = [], TotalCount = 0 });

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Nobody",
            ["lastName"] = "Here"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientResolver.NotFoundMessage("Nobody", "Here"));
    }

    [Test]
    public void Seed_DeclaresTheNewParameters_AllOptional()
    {
        var seed = SkillSeedCatalogue.Get("open_schedule");

        var parameters = seed.Parameters!.ToDictionary(p => p.Name);
        foreach (var name in new[] { "groupName", "date", "clientId", "firstName", "lastName", "idNumber" })
        {
            parameters.ShouldContainKey(name);
            parameters[name].Required.ShouldBeFalse();
        }

        parameters["date"].Type.ShouldBe("Date");
    }
}
