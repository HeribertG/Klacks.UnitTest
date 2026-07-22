// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PlanStepExecutor — covers happy path, HITL pause, verify-skill, failure handling,
/// $prev placeholder resolution, and ApproveAndContinueAsync resume semantics. ISkillExecutor +
/// IAgentPlanRepository are mocked.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Planning;

[TestFixture]
public class PlanStepExecutorTests
{
    private IAgentPlanRepository _planRepository = null!;
    private ISkillExecutor _skillExecutor = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private IAgentAutonomyPreferenceRepository _autonomyRepository = null!;
    private IAssistantNotificationService _notificationService = null!;
    private PlanStepExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _skillRegistry.GetSkillByName(Arg.Any<string>()).Returns((SkillDescriptor?)null);
        _autonomyRepository.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _sut = new PlanStepExecutor(
            _planRepository,
            _skillExecutor,
            _skillRegistry,
            _riskClassifier,
            _autonomyRepository,
            _notificationService,
            NullLogger<PlanStepExecutor>.Instance);
    }

    private static SkillExecutionContext CreateSkillContext() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "test-user",
        UserPermissions = new List<string> { "Admin" }
    };

    private static AgentPlan CreatePlan(IEnumerable<PlanStep> steps)
    {
        var stepsJson = JsonSerializer.Serialize(steps.ToList());
        return new AgentPlan
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = "user-1",
            Goal = "test goal",
            StepsJson = stepsJson,
            Status = PlanStatus.Drafting,
            CurrentStepIndex = 0
        };
    }

    [Test]
    public async Task ExecutePlanAsync_HappyPath_AllReversible_CompletesAllSteps()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_a", new(), null, true),
            new PlanStep(2, "skill_b", new(), null, true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        Assert.That(result.CurrentStepIndex, Is.EqualTo(2));
        await _skillExecutor.Received(2).ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_NonReversibleStep_PausesForApproval()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_safe", new(), null, true),
            new PlanStep(2, "skill_destructive", new(), null, false)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.PausedForApproval));
        Assert.That(result.CurrentStepIndex, Is.EqualTo(1));
        await _skillExecutor.Received(1).ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "skill_safe"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApproveAndContinueAsync_AfterPause_RunsRemainingSteps()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_destructive", new(), null, false),
            new PlanStep(2, "skill_followup", new(), null, true)
        });
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 0;

        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ApproveAndContinueAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        Assert.That(result.CurrentStepIndex, Is.EqualTo(2));
    }

    [Test]
    public async Task ApproveAndContinueAsync_PlanNotPaused_IsNoOp()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) });
        plan.Status = PlanStatus.Completed;
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ApproveAndContinueAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_SkillFails_StatusFailedWithErrorMessage()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_broken", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error("group not found"));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Failed));
        Assert.That(result.LastErrorMessage, Is.EqualTo("group not found"));
        Assert.That(result.CurrentStepIndex, Is.EqualTo(0));
    }

    [Test]
    public async Task ExecutePlanAsync_VerifySkill_RunsAfterMutatingStep()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "place_work", new(), "get_schedule_for_period", true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "place_work"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _skillExecutor.Received(1).ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "get_schedule_for_period"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_PrevPlaceholder_ResolvesFromEarlierStep()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "create_employee", new(), null, true),
            new PlanStep(2, "assign_contract_to_client", new() { ["clientId"] = "$prev.id" }, null, true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "create_employee"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new Dictionary<string, object?> { ["id"] = "client-42" }));
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "assign_contract_to_client"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i =>
                i.SkillName == "assign_contract_to_client" &&
                (string)i.Parameters["clientId"] == "client-42"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_FullyAutonomous_RunsNonReversibleStepsWithoutPause()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_safe", new(), null, true),
            new PlanStep(2, "skill_destructive", new(), null, false)
        });
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(2).ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_SensitiveStep_PausesEvenAtFullyAutonomous()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "delete_system_user", new(), null, true)
        });
        var context = CreateSkillContext();
        var descriptor = new SkillDescriptor(
            "delete_system_user", "desc", SkillCategory.Crud, [], [], [], null);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillRegistry.GetSkillByName("delete_system_user").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Sensitive);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.FullyAutonomous });

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.PausedForApproval));
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_StepInvocations_BypassChatAutonomyGate()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        await _skillExecutor.Received(1).ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c => c.BypassAutonomyGate), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_VerifySkill_ReceivesMutationResultNotStepParams()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "create_shift",
                new Dictionary<string, object?> { ["clientId"] = "client-1", ["startTime"] = "08:00" },
                "get_shift", true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "create_shift"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ShiftId = "shift-99" }));
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "get_shift"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i =>
                i.SkillName == "get_shift" &&
                i.Parameters.ContainsKey("ShiftId") &&
                (string)i.Parameters["ShiftId"] == "shift-99"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_TransientFailureThenSuccess_RetriesOnceAndCompletes()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "place_work", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(
                SkillResult.Error("Rate limit exceeded (429)"),
                SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(2).ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_TransientFailurePersists_RetriesOnceThenFails()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "place_work", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error("Service temporarily unavailable (503)"));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Failed));
        await _skillExecutor.Received(2).ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_PermanentFailure_DoesNotRetryAndFails()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "place_work", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error("client not found"));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Failed));
        Assert.That(result.LastErrorMessage, Is.EqualTo("client not found"));
        await _skillExecutor.Received(1).ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_CancelledBetweenSteps_AbortsAndSkipsRemaining()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_a", new(), null, true),
            new PlanStep(2, "skill_b", new(), null, true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        using var cts = new CancellationTokenSource();
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "skill_a"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return SkillResult.SuccessResult(new { id = "ok" });
            });

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext(), cts.Token);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Aborted));
        Assert.That(result.LastErrorMessage, Is.Null);
        await _skillExecutor.Received(1).ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "skill_a"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "skill_b"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_CancelledDuringStep_AbortsRatherThanFails()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        using var cts = new CancellationTokenSource();
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return SkillResult.Cancelled("Skill 'skill_a' execution was cancelled");
            });

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext(), cts.Token);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Aborted));
        Assert.That(result.LastErrorMessage, Is.Null);
    }

    [Test]
    public async Task AbortAsync_PausedPlan_SetsAbortedAndPublishes()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) });
        plan.Status = PlanStatus.PausedForApproval;
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.AbortAsync(plan.Id);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Status, Is.EqualTo(PlanStatus.Aborted));
        await _planRepository.Received().UpdateAsync(
            Arg.Is<AgentPlan>(p => p.Status == PlanStatus.Aborted), Arg.Any<CancellationToken>());
        await _notificationService.Received().SendPlanUpdateAsync(
            plan.UserId!, plan.Id, PlanStatus.Aborted, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Test]
    public async Task AbortAsync_TerminalPlan_ReturnsNullAndNoStateChange()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) });
        plan.Status = PlanStatus.Completed;
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.AbortAsync(plan.Id);

        Assert.That(result, Is.Null);
        await _planRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }
}
