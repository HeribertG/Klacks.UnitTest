// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the rule-read skills list_scheduling_rules and get_scheduling_defaults. The Data
/// payloads are asserted via their serialized JSON shape (the projections use internal anonymous
/// types); default values are checked against SchedulingPolicyDefaults so the tests track the source.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SchedulingRuleReadSkillsTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task ListSchedulingRules_ReturnsProjectedRules()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<SchedulingRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchedulingRuleResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Standard", MaxWorkDays = 5, MinRestDays = 2, MaxWeeklyHours = 50 },
                new() { Id = Guid.NewGuid(), Name = "Night", MaxConsecutiveDays = 4 }
            });
        var skill = new ListSchedulingRulesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
        var rules = data.GetProperty("Rules").EnumerateArray().ToList();
        rules[0].GetProperty("Name").GetString().ShouldBe("Standard");
        rules[0].GetProperty("MaxWeeklyHours").GetDecimal().ShouldBe(50);
        rules[0].GetProperty("MaxWorkDays").GetInt32().ShouldBe(5);
    }

    [Test]
    public async Task ListSchedulingRules_Empty_ReturnsZero()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<SchedulingRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchedulingRuleResource>());
        var skill = new ListSchedulingRulesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("Count").GetInt32().ShouldBe(0);
        result.Message.ShouldContain("No scheduling rules");
    }

    [Test]
    public async Task GetSchedulingDefaults_ReturnsBuiltInFallbacks()
    {
        var skill = new GetSchedulingDefaultsSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("MinRestHours").GetDouble().ShouldBe(SchedulingPolicyDefaults.MinRestHours);
        data.GetProperty("MaxDailyHours").GetDouble().ShouldBe(SchedulingPolicyDefaults.MaxDailyHours);
        data.GetProperty("MaxConsecutiveDays").GetInt32().ShouldBe(SchedulingPolicyDefaults.MaxConsecutiveDays);
        data.GetProperty("MaxWeeklyHours").GetDouble().ShouldBe(SchedulingPolicyDefaults.MaxWeeklyHours);
        data.GetProperty("MinRestDays").GetInt32().ShouldBe(SchedulingPolicyDefaults.MinRestDays);
        data.GetProperty("IsFallback").GetBoolean().ShouldBeTrue();
    }
}
