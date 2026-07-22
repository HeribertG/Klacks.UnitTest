// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreatePlanSkill: the proposal path returns a Confirmation and does NOT auto-start
/// execution; the confirmed replay (override flag) starts the fire-and-forget execution exactly once
/// and does NOT loop back into a second Confirmation; ownership, missing-model and idempotency guards.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreatePlanSkillTests
{
    private const string TwoStepJson =
        "[{\"Order\":1,\"Skill\":\"create_employee\",\"Params\":{},\"VerifySkill\":null,\"Reversible\":true}," +
        "{\"Order\":2,\"Skill\":\"create_shift\",\"Params\":{},\"VerifySkill\":\"list_shifts\",\"Reversible\":true}]";

    private IPlanChatService _planChatService = null!;
    private IAgentPlanRepository _planRepository = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private ITurnConfirmationScope _turnScope = null!;
    private CreatePlanSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _planChatService = Substitute.For<IPlanChatService>();
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _confirmationStore = Substitute.For<IPendingConfirmationStore>();
        _turnScope = Substitute.For<ITurnConfirmationScope>();
        _confirmationStore
            .Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns("plan-token");
        _skill = new CreatePlanSkill(_planChatService, _planRepository, _confirmationStore, _turnScope);
    }

    private static SkillExecutionContext Ctx(Guid userId) => new()
    {
        UserId = userId,
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static AgentPlan DraftPlan(Guid id, Guid userId, string stepsJson = TwoStepJson) => new()
    {
        Id = id,
        UserId = userId.ToString(),
        StepsJson = stepsJson,
        Status = PlanStatus.Drafting
    };

    // ── Proposal path ──────────────────────────────────────────────────────────

    [Test]
    public async Task Proposal_ReturnsConfirmation_MarksTurnScope_AndDoesNotStartExecution()
    {
        var userId = Guid.NewGuid();
        var plan = DraftPlan(Guid.NewGuid(), userId);
        _planChatService.CreatePlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await _skill.ExecuteAsync(
            Ctx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "create a customer and an order" });

        result.Type.ShouldBe(SkillResultType.Confirmation);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("1.");
        result.Message.ShouldContain("plan-token");
        _confirmationStore.Received(1).Create(userId, PlanSkillDefaults.CreatePlanSkillName,
            Arg.Any<IReadOnlyDictionary<string, object>>());
        _turnScope.Received(1).MarkIssuedForSensitiveSkill("plan-token");
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Proposal_StoresExecuteOverrideFlagAndPlanId()
    {
        var userId = Guid.NewGuid();
        var plan = DraftPlan(Guid.NewGuid(), userId);
        _planChatService.CreatePlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(plan);
        IReadOnlyDictionary<string, object>? stored = null;
        _confirmationStore
            .Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Do<IReadOnlyDictionary<string, object>>(p => stored = p))
            .Returns("plan-token");

        await _skill.ExecuteAsync(
            Ctx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "do X and Y" });

        stored.ShouldNotBeNull();
        stored![PlanSkillDefaults.ExecuteConfirmedParameter].ShouldBe(PlanSkillDefaults.ExecuteConfirmedValue);
        stored[PlanSkillDefaults.PlanIdParameter].ShouldBe(plan.Id.ToString());
    }

    [Test]
    public async Task Proposal_WithoutGoal_ReturnsError_AndDoesNotCreatePlan()
    {
        var result = await _skill.ExecuteAsync(Ctx(Guid.NewGuid()), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        await _planChatService.DidNotReceive().CreatePlanAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Proposal_WhenPlannerReturnsNoSteps_ReturnsError_AndStoresNoConfirmation()
    {
        var userId = Guid.NewGuid();
        var plan = DraftPlan(Guid.NewGuid(), userId, stepsJson: "[]");
        _planChatService.CreatePlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await _skill.ExecuteAsync(
            Ctx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "impossible goal" });

        result.Success.ShouldBeFalse();
        _confirmationStore.DidNotReceive().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // ── Confirmed execution replay ──────────────────────────────────────────────

    [Test]
    public async Task ConfirmedReplay_StartsExecutionOnce_AndReturnsNoSecondConfirmation()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns(DraftPlan(planId, userId));
        _planChatService.ResolveExecutionProviderAsync(Arg.Any<CancellationToken>())
            .Returns(new PlanProviderResolution(true, LLMProviderType.OpenAI));

        var result = await _skill.ExecuteAsync(Ctx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeTrue();
        result.Type.ShouldBe(SkillResultType.Data);
        _planChatService.Received(1).StartBackgroundExecution(planId, Arg.Any<SkillExecutionContext>(), false);
        _confirmationStore.DidNotReceive().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Test]
    public async Task ConfirmedReplay_WithoutDefaultModel_ReturnsError_AndDoesNotStart()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns(DraftPlan(planId, userId));
        _planChatService.ResolveExecutionProviderAsync(Arg.Any<CancellationToken>())
            .Returns(new PlanProviderResolution(false, null));

        var result = await _skill.ExecuteAsync(Ctx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeFalse();
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ConfirmedReplay_ForeignPlan_ReturnsError_AndDoesNotStart()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>())
            .Returns(DraftPlan(planId, Guid.NewGuid()));

        var result = await _skill.ExecuteAsync(Ctx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeFalse();
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ConfirmedReplay_AlreadyRunningPlan_DoesNotStartAgain()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var plan = DraftPlan(planId, userId);
        plan.Status = PlanStatus.Executing;
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _skill.ExecuteAsync(Ctx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeTrue();
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    private static Dictionary<string, object> ExecuteParameters(Guid planId) => new()
    {
        [PlanSkillDefaults.ExecuteConfirmedParameter] = PlanSkillDefaults.ExecuteConfirmedValue,
        [PlanSkillDefaults.PlanIdParameter] = planId.ToString(),
        [PlanSkillDefaults.GoalParameter] = "do X and Y"
    };
}
