// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for ListGroupsSkill.ExecuteAsync, written before its long body was split into filter and
/// message steps: the validity windows (default active, former, future, combinations, none), the root-only and canton
/// filters (trimmed upper-case state code, no filtering when no calendar selection matches), the exact composed
/// message including the validity note and the OtherMatches sentence, the multi-suggestion ask-back and the property
/// order of the serialized result.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListGroupsSkillCharacterizationTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid CalendarId = Guid.NewGuid();

    private IGroupRepository _groupRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ICountryResolver _countryResolver = null!;
    private ListGroupsSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries { Abbreviation = "CH" });
        _skill = new ListGroupsSkill(_groupRepository, _calendarSelectionRepository, _countryResolver, new FixedCompanyClock(Today));

        var activeId = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = activeId, Name = "Active", ValidFrom = Utc(2025, 1, 1) },
            new() { Id = Guid.NewGuid(), Name = "Former", ValidFrom = Utc(2020, 1, 1), ValidUntil = Utc(2024, 1, 1) },
            new() { Id = Guid.NewGuid(), Name = "Future", ValidFrom = Utc(2027, 1, 1) },
            new() { Id = Guid.NewGuid(), Name = "Deleted", ValidFrom = Utc(2025, 1, 1), IsDeleted = true },
            new() { Id = Guid.NewGuid(), Name = "Child", ValidFrom = Utc(2025, 1, 1), Parent = activeId, CalendarSelectionId = CalendarId }
        });
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private Task<SkillResult> RunAsync(params (string Key, object Value)[] parameters) =>
        _skill.ExecuteAsync(Ctx(), parameters.ToDictionary(p => p.Key, p => p.Value));

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    private static List<string> GroupNames(SkillResult result) =>
        Data(result).GetProperty("Groups").EnumerateArray()
            .Select(group => group.GetProperty("Name").GetString()!)
            .ToList();

    [Test]
    public async Task Default_ListsActiveGroups_WithExactMessageAndDataShape()
    {
        var result = await RunAsync();

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Found 2 group(s).");
        GroupNames(result).ShouldBe(new[] { "Active", "Child" });
        var data = Data(result);
        data.EnumerateObject().Select(p => p.Name).ShouldBe(new[]
        {
            "Groups", "TotalCount", "SearchTerm", "OtherMatches", "RootOnly", "Canton",
            "ActiveDateRange", "FormerDateRange", "FutureDateRange"
        });
        data.GetProperty("Groups")[0].EnumerateObject().Select(p => p.Name).ShouldBe(new[]
        {
            "Id", "Name", "Parent", "ValidFrom", "ValidUntil", "IsRoot"
        });
        data.GetProperty("ActiveDateRange").GetBoolean().ShouldBeTrue();
        data.GetProperty("FormerDateRange").GetBoolean().ShouldBeFalse();
        data.GetProperty("FutureDateRange").GetBoolean().ShouldBeFalse();
        data.GetProperty("Groups")[1].GetProperty("IsRoot").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task FormerOnly_ListsEndedGroups()
    {
        var result = await RunAsync(("formerDateRange", true));

        GroupNames(result).ShouldBe(new[] { "Former" });
        result.Message.ShouldBe("Found 1 group(s) (validity: former).");
        Data(result).GetProperty("ActiveDateRange").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task FutureOnly_ListsUpcomingGroups()
    {
        var result = await RunAsync(("futureDateRange", true));

        GroupNames(result).ShouldBe(new[] { "Future" });
        result.Message.ShouldBe("Found 1 group(s) (validity: future).");
    }

    [Test]
    public async Task ActiveAndFormer_CombineTheNote()
    {
        var result = await RunAsync(("activeDateRange", true), ("formerDateRange", true));

        GroupNames(result).ShouldBe(new[] { "Active", "Child", "Former" });
        result.Message.ShouldBe("Found 3 group(s) (validity: active+former).");
    }

    [Test]
    public async Task AllWindows_ListEveryUndeletedGroup()
    {
        var result = await RunAsync(("activeDateRange", true), ("formerDateRange", true), ("futureDateRange", true));

        GroupNames(result).ShouldBe(new[] { "Active", "Child", "Former", "Future" });
        result.Message.ShouldBe("Found 4 group(s) (validity: active+former+future).");
    }

    [Test]
    public async Task NoWindowSelected_MatchesNothing_AndSaysWhy()
    {
        var result = await RunAsync(("activeDateRange", false));

        GroupNames(result).ShouldBeEmpty();
        result.Message.ShouldBe("Found 0 group(s) (no validity window selected, so no group can match).");
    }

    [Test]
    public async Task RootOnly_ListsGroupsWithoutParent()
    {
        var result = await RunAsync(("rootOnly", true));

        GroupNames(result).ShouldBe(new[] { "Active" });
        result.Message.ShouldBe("Found 1 group(s) (root level only).");
    }

    [Test]
    public async Task Canton_FiltersByCalendarSelection_UsingTrimmedUpperCaseState()
    {
        _calendarSelectionRepository.GetIdsByStateAsync("CH", "BE", Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { CalendarId });

        var result = await RunAsync(("canton", " be"));

        GroupNames(result).ShouldBe(new[] { "Child" });
        result.Message.ShouldBe("Found 1 group(s) for canton  BE.");
        Data(result).GetProperty("Canton").GetString().ShouldBe(" be");
        await _calendarSelectionRepository.Received(1).GetIdsByStateAsync("CH", "BE", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Canton_WithoutMatchingCalendarSelection_DoesNotFilter()
    {
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Countries?)null);
        _calendarSelectionRepository.GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var result = await RunAsync(("canton", "ZH"));

        GroupNames(result).ShouldBe(new[] { "Active", "Child" });
        result.Message.ShouldBe("Found 2 group(s) for canton ZH.");
        await _calendarSelectionRepository.Received(1).GetIdsByStateAsync(string.Empty, "ZH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchTerm_WithOtherMatches_ComposesTheFullMessage()
    {
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Nord", ValidFrom = Utc(2025, 1, 1) },
            new() { Id = Guid.NewGuid(), Name = "Region Nord", ValidFrom = Utc(2025, 1, 1) }
        });

        var result = await RunAsync(("searchTerm", "Nord"), ("rootOnly", true));

        GroupNames(result).ShouldBe(new[] { "Nord" });
        result.Message.ShouldBe(
            "Found 1 group(s) matching 'Nord' (root level only). " +
            "Groups that match the term less exactly (OtherMatches, names only): Region Nord.");
    }

    [Test]
    public async Task SearchTerm_WithSeveralSuggestionsOnly_AsksBackWithAllNames()
    {
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Nordwest", ValidFrom = Utc(2025, 1, 1) },
            new() { Id = Guid.NewGuid(), Name = "Nordost", ValidFrom = Utc(2025, 1, 1) }
        });

        var result = await RunAsync(("searchTerm", "Nord"));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(NameResolutionMessages.SuggestedNames("Nord", "Nordost, Nordwest"));
        var data = Data(result);
        data.EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "Groups", "TotalCount", "SearchTerm", "OtherMatches" });
        data.GetProperty("TotalCount").GetInt32().ShouldBe(0);
    }
}
