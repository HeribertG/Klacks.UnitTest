// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the read-only SearchEmployeesSkill — happy path with matches, empty result,
/// entity-type mapping and the truncation hint when more rows exist than the limit.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SearchEmployeesSkillTests
{
    private IClientSearchRepository _searchRepository = null!;
    private SearchEmployeesSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _skill = new SearchEmployeesSkill(_searchRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private void WireSearch(int totalCount, params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = totalCount });
    }

    private static ClientSearchItem Item(string firstName, string lastName) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = firstName,
        LastName = lastName,
        IdNumber = 1
    };

    [Test]
    public async Task ReturnsMatches_WithCountInMessage()
    {
        WireSearch(2, Item("Anna", "Müller"), Item("Max", "Meier"));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "M" });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Found 2"));
        Assert.That(result.Message, Does.Contain("'M'"));
    }

    [Test]
    public async Task ReturnsEmptyResultMessage_WhenNothingMatches()
    {
        WireSearch(0);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Nobody" });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No employees found"));
    }

    [Test]
    public async Task PassesCantonAndEntityTypeToRepository()
    {
        WireSearch(1, Item("Anna", "Müller"));
        var parameters = new Dictionary<string, object>
        {
            ["searchTerm"] = "Anna",
            ["canton"] = "BE",
            ["entityType"] = "Customer"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _searchRepository.Received(1).SearchAsync(
            "Anna", "BE", EntityTypeEnum.Customer, null, 10, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IgnoresUnknownEntityType()
    {
        WireSearch(1, Item("Anna", "Müller"));

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["entityType"] = "Alien" });

        Assert.That(result.Success, Is.True);
        await _searchRepository.Received(1).SearchAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), null, null, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportsTruncation_WhenMoreResultsThanLimit()
    {
        WireSearch(12, Item("Anna", "Müller"), Item("Max", "Meier"));

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["searchTerm"] = "M", ["limit"] = 2 });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Found 12"));
        Assert.That(result.Message, Does.Contain("Showing first 2"));
    }
}
