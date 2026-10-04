// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for CreatePlanSkill: the proposal path returns a Confirmation and does NOT auto-start
/// execution; the confirmed replay (override flag) starts the fire-and-forget execution exactly once
/// and does NOT loop back into a second Confirmation; ownership, missing-model and idempotency guards.
/// Over MCP (ExternalAgentAccessMode set) every step and verify skill must be available to the caller over MCP
/// (real McpDelegatedSkillPolicy) at proposal and again at the confirmed start.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Mcp;
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
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
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
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.Reversible);
        _skill = new CreatePlanSkill(
            _planChatService, _planRepository, _confirmationStore, _turnScope, _skillRegistry,
            new McpDelegatedSkillPolicy(
                new McpSkillExposurePolicy(_riskClassifier), new McpReadModeToolPolicy(_riskClassifier)));
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
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
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
        _turnScope.Received(1).MarkIssued("plan-token");
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Proposal_StoresExecuteOverrideFlagAndPlanId()
    {
        var userId = Guid.NewGuid();
        var plan = DraftPlan(Guid.NewGuid(), userId);
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
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
        await _planChatService.DidNotReceive().DraftPlanAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
        await _planRepository.DidNotReceive().AddAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Proposal_WhenPlannerReturnsNoSteps_ReturnsError_AndStoresNoConfirmation()
    {
        var userId = Guid.NewGuid();
        var plan = DraftPlan(Guid.NewGuid(), userId, stepsJson: "[]");
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(plan);

        var result = await _skill.ExecuteAsync(
            Ctx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "impossible goal" });

        result.Success.ShouldBeFalse();
        await _planRepository.DidNotReceive().AddAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
        _confirmationStore.DidNotReceive().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
        _turnScope.DidNotReceive().MarkIssued(Arg.Any<string>());
    }

    [Test]
    public async Task Proposal_WithSteps_PersistsPlanOnce_BeforeTokenIsMinted_AndTokenReferencesPlanId()
    {
        var userId = Guid.NewGuid();
        var plan = DraftPlan(Guid.NewGuid(), userId);
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(plan);
        IReadOnlyDictionary<string, object>? stored = null;
        _confirmationStore
            .Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Do<IReadOnlyDictionary<string, object>>(p => stored = p))
            .Returns("plan-token");

        await _skill.ExecuteAsync(
            Ctx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "do X and Y" });

        await _planRepository.Received(1).AddAsync(plan, Arg.Any<CancellationToken>());
        stored.ShouldNotBeNull();
        stored![PlanSkillDefaults.PlanIdParameter].ShouldBe(plan.Id.ToString());
        Received.InOrder(() =>
        {
            _planRepository.AddAsync(plan, Arg.Any<CancellationToken>());
            _confirmationStore.Create(userId, PlanSkillDefaults.CreatePlanSkillName,
                Arg.Any<IReadOnlyDictionary<string, object>>());
        });
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

    // -- External agent (MCP) ------------------------------------------------------

    private const string HiddenVerifyJson =
        "[{\"Order\":1,\"Skill\":\"create_employee\",\"Params\":{},\"VerifySkill\":\"list_personal_access_tokens\",\"Reversible\":true}]";

    private void RegisterSkills(params string[] names)
    {
        foreach (var name in names)
        {
            _skillRegistry.GetSkillByName(name).Returns(new SkillDescriptor(
                name, "test skill", SkillCategory.Crud,
                Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null));
        }
    }

    private static SkillExecutionContext McpCtx(Guid userId) =>
        Ctx(userId) with { ExternalAgentAccessMode = PersonalAccessTokenAccessMode.Write };

    private void ExecutionProviderAvailable()
    {
        _planChatService.ResolveExecutionProviderAsync(Arg.Any<CancellationToken>())
            .Returns(new PlanProviderResolution(true, LLMProviderType.OpenAI));
    }

    [Test]
    public async Task OverMcp_Proposal_WithAHiddenVerifySkill_IsRefused_AndNothingIsStored()
    {
        RegisterSkills("create_employee", "list_personal_access_tokens");
        var userId = Guid.NewGuid();
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(DraftPlan(Guid.NewGuid(), userId, HiddenVerifyJson));

        var result = await _skill.ExecuteAsync(
            McpCtx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "hire and check" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("list_personal_access_tokens");
        await _planRepository.DidNotReceive().AddAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
        _confirmationStore.DidNotReceive().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Test]
    public async Task OverMcp_Proposal_WithAnUnregisteredStepSkill_IsRefused()
    {
        RegisterSkills("create_employee");
        var userId = Guid.NewGuid();
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(DraftPlan(Guid.NewGuid(), userId));

        var result = await _skill.ExecuteAsync(
            McpCtx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "hire and plan" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("create_shift");
        await _planRepository.DidNotReceive().AddAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OverMcp_Proposal_WithExposedSkills_ReturnsConfirmation()
    {
        RegisterSkills("create_employee", "create_shift", "list_shifts");
        var userId = Guid.NewGuid();
        _planChatService.DraftPlanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(DraftPlan(Guid.NewGuid(), userId));

        var result = await _skill.ExecuteAsync(
            McpCtx(userId),
            new Dictionary<string, object> { [PlanSkillDefaults.GoalParameter] = "hire and plan" });

        result.Type.ShouldBe(SkillResultType.Confirmation);
        await _planRepository.Received(1).AddAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OverMcp_ConfirmedReplay_OfAPlanWithAHiddenSkill_DoesNotStart()
    {
        RegisterSkills("create_employee", "list_personal_access_tokens");
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>())
            .Returns(DraftPlan(planId, userId, HiddenVerifyJson));
        ExecutionProviderAvailable();

        var result = await _skill.ExecuteAsync(McpCtx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("list_personal_access_tokens");
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task OverMcp_ConfirmedReplay_WithExposedSkills_StartsUnderTheMcpContext()
    {
        RegisterSkills("create_employee", "create_shift", "list_shifts");
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns(DraftPlan(planId, userId));
        ExecutionProviderAvailable();

        var result = await _skill.ExecuteAsync(McpCtx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeTrue();
        _planChatService.Received(1).StartBackgroundExecution(
            planId,
            Arg.Is<SkillExecutionContext>(c => c.ExternalAgentAccessMode == PersonalAccessTokenAccessMode.Write
                && c.TokenRenewalOwnerId == null),
            false);
    }

    [Test]
    public async Task InChat_ConfirmedReplay_OfAPlanWithASkillHiddenFromMcp_StillStarts()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>())
            .Returns(DraftPlan(planId, userId, HiddenVerifyJson));
        ExecutionProviderAvailable();

        var result = await _skill.ExecuteAsync(Ctx(userId), ExecuteParameters(planId));

        result.Success.ShouldBeTrue();
        _planChatService.Received(1).StartBackgroundExecution(planId, Arg.Any<SkillExecutionContext>(), false);
    }
}
