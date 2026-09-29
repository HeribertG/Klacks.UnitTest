// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for list_scenarios: by default only Active (open) scenarios are returned; onlyOpen=false
/// includes accepted/rejected; without a group the scenarios of EVERY group are listed (the old repository
/// call only returned group-less scenarios, so an AutoWizard result bound to a group was invisible); a
/// restricted group scope hides scenarios outside the scope and group-less ones; a groupId outside the scope
/// is answered with "not accessible" instead of an empty list. The filters run in the repository query, so the
/// skill is exercised over the real AnalyseScenarioRepository on the EF InMemory provider - that proves the
/// filter semantics, not the Npgsql SQL translation. Payload is asserted via serialized JSON because the
/// projection uses an internal anonymous type.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListScenariosSkillTests
{
    private Group _winterthur = null!;
    private Group _winterthurSub = null!;
    private Group _bern = null!;

    private DataBaseContext _context = null!;
    private IAnalyseScenarioRepository _repo = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupScopeGuard _scopeGuard = null!;

    [SetUp]
    public async Task Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repo = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());

        _winterthur = new Group { Id = Guid.NewGuid(), Name = "Winterthur" };
        _winterthurSub = new Group { Id = Guid.NewGuid(), Name = "Winterthur Nord", Parent = _winterthur.Id, Root = _winterthur.Id };
        _bern = new Group { Id = Guid.NewGuid(), Name = "Bern" };
        _context.Group.AddRange(_winterthur, _winterthurSub, _bern);
        _context.AnalyseScenarios.AddRange(
            Scenario("Open", AnalyseScenarioStatus.Active, null),
            Scenario("Done", AnalyseScenarioStatus.Accepted, null),
            Scenario("Gone", AnalyseScenarioStatus.Rejected, null),
            Scenario("Auto Winterthur", AnalyseScenarioStatus.Active, _winterthur),
            Scenario("Auto Winterthur Nord", AnalyseScenarioStatus.Active, _winterthurSub),
            Scenario("Auto Bern", AnalyseScenarioStatus.Active, _bern));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.Get(Arg.Any<Guid>()).Returns(call =>
            new[] { _winterthur, _winterthurSub, _bern }.FirstOrDefault(g => g.Id == call.Arg<Guid>()));

        _scopeGuard = Substitute.For<IGroupScopeGuard>();
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private static AnalyseScenario Scenario(string name, AnalyseScenarioStatus status, Group? group) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Token = Guid.NewGuid(),
        Status = status,
        GroupId = group?.Id
    };

    private void RestrictToWinterthur() =>
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted(new[] { _winterthur.Id }, new[] { _winterthur.Name }));

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private ListScenariosSkill Skill() => new(_repo, _groupRepository, _scopeGuard);

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    private static List<string> Names(JsonElement data)
        => data.GetProperty("Scenarios").EnumerateArray().Select(s => s.GetProperty("Name").GetString()!).ToList();

    [Test]
    public async Task Default_OnlyOpen_FiltersToActive()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = DataAsJson(result);
        data.GetProperty("OnlyOpen").GetBoolean().ShouldBeTrue();
        Names(data).ShouldBe(new[] { "Open", "Auto Winterthur", "Auto Winterthur Nord", "Auto Bern" }, ignoreOrder: true);
        data.GetProperty("Scenarios").EnumerateArray()
            .All(s => s.GetProperty("Status").GetString() == "Active").ShouldBeTrue();
    }

    [Test]
    public async Task OnlyOpenFalse_IncludesAllStatuses()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["onlyOpen"] = "false"
        });

        var data = DataAsJson(result);
        data.GetProperty("Count").GetInt32().ShouldBe(6);
        data.GetProperty("OnlyOpen").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task WithoutGroup_ListsScenariosOfEveryGroup_NotOnlyGroupLessOnes()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = DataAsJson(result);
        Names(data).ShouldContain("Auto Winterthur");
        var winterthur = data.GetProperty("Scenarios").EnumerateArray()
            .Single(s => s.GetProperty("Name").GetString() == "Auto Winterthur");
        winterthur.GetProperty("GroupName").GetString().ShouldBe("Winterthur");
    }

    [Test]
    public async Task WithGroup_ListsOnlyThatGroup()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _winterthur.Id.ToString()
        });

        Names(DataAsJson(result)).ShouldBe(new[] { "Auto Winterthur" });
    }

    [Test]
    public async Task RestrictedScope_HidesOtherGroupsAndGroupLessScenarios_KeepsSubGroupsOfTheVisibleRoot()
    {
        RestrictToWinterthur();

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Names(DataAsJson(result)).ShouldBe(new[] { "Auto Winterthur", "Auto Winterthur Nord" }, ignoreOrder: true);
    }

    [Test]
    public async Task RestrictedScope_GroupOutsideScope_IsReportedAsNotAccessible_NotAsAnEmptyList()
    {
        RestrictToWinterthur();

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _bern.Id.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not accessible");
        result.Message.ShouldContain("Bern");
        result.Message.ShouldNotContain("Found 0");
    }

    [Test]
    public async Task RestrictedScope_SubGroupInsideScope_IsListed()
    {
        RestrictToWinterthur();

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _winterthurSub.Id.ToString()
        });

        Names(DataAsJson(result)).ShouldBe(new[] { "Auto Winterthur Nord" });
    }

    [Test]
    public async Task UnknownGroupId_ReturnsNotFound()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task MalformedGroupId_ReturnsFormatError()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = "winterthur"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid groupId");
    }

    [Test]
    public async Task Description_CarriesTheRunOutcome()
    {
        var noted = Scenario("Auto Bern partial", AnalyseScenarioStatus.Active, _bern);
        noted.Description = "AutoWizard failed in the HolisticHarmonizer stage: engine crashed";
        _context.AnalyseScenarios.Add(noted);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = _bern.Id.ToString()
        });

        var descriptions = DataAsJson(result).GetProperty("Scenarios").EnumerateArray()
            .Select(d => d.GetProperty("Description").GetString()).ToList();
        descriptions.ShouldContain("AutoWizard failed in the HolisticHarmonizer stage: engine crashed");
    }
}
