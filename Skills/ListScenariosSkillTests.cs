// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the list_scenarios [K8] status filter: by default only Active (open) scenarios
/// are returned; onlyOpen=false includes accepted/rejected; the status is projected. Payload is
/// asserted via serialized JSON because the projection uses an internal anonymous type.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListScenariosSkillTests
{
    private IAnalyseScenarioRepository _repo = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IAnalyseScenarioRepository>();
        _repo.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>
            {
                new() { Id = Guid.NewGuid(), Name = "Open", Token = Guid.NewGuid(), Status = AnalyseScenarioStatus.Active },
                new() { Id = Guid.NewGuid(), Name = "Done", Token = Guid.NewGuid(), Status = AnalyseScenarioStatus.Accepted },
                new() { Id = Guid.NewGuid(), Name = "Gone", Token = Guid.NewGuid(), Status = AnalyseScenarioStatus.Rejected }
            });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private ListScenariosSkill Skill() => new(_repo);

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task Default_OnlyOpen_FiltersToActive()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = DataAsJson(result);
        data.GetProperty("Count").GetInt32().ShouldBe(1);
        data.GetProperty("OnlyOpen").GetBoolean().ShouldBeTrue();
        var first = data.GetProperty("Scenarios")[0];
        first.GetProperty("Name").GetString().ShouldBe("Open");
        first.GetProperty("Status").GetString().ShouldBe("Active");
    }

    [Test]
    public async Task OnlyOpenFalse_IncludesAllStatuses()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["onlyOpen"] = "false"
        });

        var data = DataAsJson(result);
        data.GetProperty("Count").GetInt32().ShouldBe(3);
        data.GetProperty("OnlyOpen").GetBoolean().ShouldBeFalse();
    }
}
