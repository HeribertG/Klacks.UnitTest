// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetPlanStatusSkill: reports the caller's most recent plan when no id is given,
/// reports a specific plan by id, refuses another user's plan, and handles the no-plans case.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetPlanStatusSkillTests
{
    private const string TwoStepJson =
        "[{\"Order\":1,\"Skill\":\"create_employee\"},{\"Order\":2,\"Skill\":\"create_shift\"}]";

    private IAgentPlanRepository _planRepository = null!;
    private GetPlanStatusSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _skill = new GetPlanStatusSkill(_planRepository);
    }

    private static SkillExecutionContext Ctx(Guid userId) => new()
    {
        UserId = userId,
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task NoPlanId_ReportsMostRecentPlan()
    {
        var userId = Guid.NewGuid();
        var older = new AgentPlan
        {
            Id = Guid.NewGuid(), UserId = userId.ToString(), StepsJson = TwoStepJson,
            Status = PlanStatus.Completed, CurrentStepIndex = 2, CreateTime = new DateTime(2026, 1, 1)
        };
        var newer = new AgentPlan
        {
            Id = Guid.NewGuid(), UserId = userId.ToString(), StepsJson = TwoStepJson,
            Status = PlanStatus.Executing, CurrentStepIndex = 1, CreateTime = new DateTime(2026, 6, 1)
        };
        _planRepository.ListByUserAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentPlan> { older, newer });

        var result = await _skill.ExecuteAsync(Ctx(userId), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(PlanStatus.Executing);
        result.Message.ShouldContain("2");
    }

    [Test]
    public async Task NoPlans_ReturnsHasPlanFalse()
    {
        var userId = Guid.NewGuid();
        _planRepository.ListByUserAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentPlan>());

        var result = await _skill.ExecuteAsync(Ctx(userId), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("no plans");
    }

    [Test]
    public async Task PlanId_ReportsThatPlan()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns(new AgentPlan
        {
            Id = planId, UserId = userId.ToString(), StepsJson = TwoStepJson,
            Status = PlanStatus.Failed, CurrentStepIndex = 1, LastErrorMessage = "boom"
        });

        var result = await _skill.ExecuteAsync(
            Ctx(userId), new Dictionary<string, object> { [PlanSkillDefaults.PlanIdParameter] = planId.ToString() });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(PlanStatus.Failed);
        result.Message.ShouldContain("boom");
    }

    [Test]
    public async Task PlanId_ForeignPlan_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns(new AgentPlan
        {
            Id = planId, UserId = Guid.NewGuid().ToString(), StepsJson = TwoStepJson, Status = PlanStatus.Executing
        });

        var result = await _skill.ExecuteAsync(
            Ctx(userId), new Dictionary<string, object> { [PlanSkillDefaults.PlanIdParameter] = planId.ToString() });

        result.Success.ShouldBeFalse();
    }
}
