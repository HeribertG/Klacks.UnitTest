// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PlanStepExecutor — covers happy path, HITL pause, verify-skill, failure handling,
/// $prev placeholder resolution, ApproveAndContinueAsync resume semantics, the task-boundary
/// compaction trigger fired on plan completion (but not on abort/failure), and the proactive
/// PlanPausedForApprovalTriggerEvent fired via IAgentTriggerService whenever a plan pauses for
/// approval. ISkillExecutor + IAgentPlanRepository + ILLMBackgroundTaskService + IAgentTriggerService
/// are mocked. Unless a test overrides it, every skill name resolves to a registered descriptor
/// classified as SkillRiskClass.Reversible, so plain steps run through at the default autonomy level
/// (Autonomous) without pausing; tests that exercise the pause decision mock
/// ISkillRegistry/ISkillRiskClassifier explicitly for the skill under test. CreatePlan defaults
/// AgentPlan.UserId to the non-Guid literal "user-1" (matching the pre-existing fixture data), so
/// existing tests never trigger the new proactive event by accident; tests that assert on the trigger
/// pass an explicit Guid-shaped UserId. CreatePlan defaults Origin to AgentPlanOrigin.UserGoal so all
/// pre-existing tests keep exercising the unchanged path; a handful of tests pass origin:
/// AgentPlanOrigin.SelfReflection to cover the Phase-4 effective-level override (irreversible runs
/// without pausing and without ever reading the user's autonomy row, Sensitive still pauses).
/// A further group of ApproveAndContinueAsync tests covers the resume-identity override: a SelfReflection
/// plan with a matching approved GoalCandidate resumes with UserName/UserPermissions/SessionId replaced by
/// that candidate's frozen OwnerPermissionsCsv, GoalSelfReflectionAuditConstants.AuditUserName, and
/// SessionId (UserId/TenantId are left as the resuming caller supplied them); a UserGoal plan, a
/// missing/unapproved candidate, and an empty OwnerPermissionsCsv all fall back to the supplied context
/// unchanged.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
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
    private ILLMBackgroundTaskService _backgroundTaskService = null!;
    private IAgentTriggerService _triggerService = null!;
    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private IInternalTokenIssuer _tokenIssuer = null!;
    private PlanStepExecutor _sut = null!;

    private const int TaskBoundaryMinMessages = 10;

    [SetUp]
    public void Setup()
    {
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _skillRegistry.GetSkillByName(Arg.Any<string>())
            .Returns(callInfo => BuildDefaultDescriptor(callInfo.Arg<string>()));
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.Reversible);
        _autonomyRepository.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _backgroundTaskService = Substitute.For<ILLMBackgroundTaskService>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _goalCandidateRepository.GetByPlanIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((GoalCandidate?)null);
        _tokenIssuer = Substitute.For<IInternalTokenIssuer>();
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken("owner-jwt"), new[] { Roles.Authorised }));
        _sut = new PlanStepExecutor(
            _planRepository,
            _skillExecutor,
            _skillRegistry,
            _riskClassifier,
            _autonomyRepository,
            _notificationService,
            _backgroundTaskService,
            _triggerService,
            _goalCandidateRepository,
            _tokenIssuer,
            NullLogger<PlanStepExecutor>.Instance);
    }

    private static SkillExecutionContext CreateSkillContext() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "test-user",
        UserPermissions = new List<string> { "Admin" }
    };

    private static AgentPlan CreatePlan(
        IEnumerable<PlanStep> steps, Guid? sessionId = null, string? userId = null, string? origin = null)
    {
        var stepsJson = JsonSerializer.Serialize(steps.ToList());
        return new AgentPlan
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = userId ?? "user-1",
            SessionId = sessionId,
            Goal = "test goal",
            StepsJson = stepsJson,
            Status = PlanStatus.Drafting,
            CurrentStepIndex = 0,
            Origin = origin ?? AgentPlanOrigin.UserGoal
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
        var destructiveDescriptor = BuildDefaultDescriptor("skill_destructive");
        _skillRegistry.GetSkillByName("skill_destructive").Returns(destructiveDescriptor);
        _riskClassifier.Classify(destructiveDescriptor).Returns(SkillRiskClass.Irreversible);
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
    public async Task ExecutePlanAsync_PausesForApproval_FiresPlanPausedTriggerWithTargetUserIdAndDedupKey()
    {
        var userId = Guid.NewGuid();
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_destructive", new(), null, false)
        }, userId: userId.ToString());
        var destructiveDescriptor = BuildDefaultDescriptor("skill_destructive");
        _skillRegistry.GetSkillByName("skill_destructive").Returns(destructiveDescriptor);
        _riskClassifier.Classify(destructiveDescriptor).Returns(SkillRiskClass.Irreversible);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<PlanPausedForApprovalTriggerEvent>(e =>
                e.PlanId == plan.Id &&
                e.StepIndex == 0 &&
                e.TargetUserId == userId &&
                e.DedupKey == $"{plan.Id}:0"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_TriggerServiceThrows_PlanStillPausesCleanlyWithoutCrashing()
    {
        var userId = Guid.NewGuid();
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_destructive", new(), null, false)
        }, userId: userId.ToString());
        var destructiveDescriptor = BuildDefaultDescriptor("skill_destructive");
        _skillRegistry.GetSkillByName("skill_destructive").Returns(destructiveDescriptor);
        _riskClassifier.Classify(destructiveDescriptor).Returns(SkillRiskClass.Irreversible);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _triggerService.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("trigger dispatch boom")));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        result.CurrentStepIndex.ShouldBe(0);
    }

    [Test]
    public async Task ExecutePlanAsync_CompletesWithoutPausing_DoesNotFirePlanPausedTrigger()
    {
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_a", new(), null, true),
            new PlanStep(2, "skill_b", new(), null, true)
        }, userId: Guid.NewGuid().ToString());
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        result.Status.ShouldBe(PlanStatus.Completed);
        await _triggerService.DidNotReceive().OnEventAsync(
            Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_PausesForApproval_UserIdNull_SkipsTriggerWithoutCrashing()
    {
        var plan = new AgentPlan
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = null,
            Goal = "test goal",
            StepsJson = JsonSerializer.Serialize(new[] { new PlanStep(1, "skill_destructive", new(), null, false) }),
            Status = PlanStatus.Drafting,
            CurrentStepIndex = 0
        };
        var destructiveDescriptor = BuildDefaultDescriptor("skill_destructive");
        _skillRegistry.GetSkillByName("skill_destructive").Returns(destructiveDescriptor);
        _riskClassifier.Classify(destructiveDescriptor).Returns(SkillRiskClass.Irreversible);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _triggerService.DidNotReceive().OnEventAsync(
            Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
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
    public async Task ApproveAndContinueAsync_SelfReflectionWithApprovedCandidate_ResumesUnderTheOwnersCurrentIdentity()
    {
        var candidateId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var plan = CreatePlan(
            new[]
            {
                new PlanStep(1, "skill_destructive", new(), null, false),
                new PlanStep(2, "skill_followup", new(), null, true)
            },
            origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 0;

        var candidate = new GoalCandidate
        {
            Id = candidateId,
            PlanId = plan.Id,
            UserId = ownerId.ToString(),
            Status = GoalCandidateStatus.Approved,
            // Still present, but no longer the source of rights — it only marks an approved candidate.
            OwnerPermissionsCsv = "clients.read,clients.write"
        };
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var suppliedContext = CreateSkillContext();
        var expectedSessionId = GoalSelfReflectionAuditConstants.SessionIdPrefix + candidateId;
        var result = await _sut.ApproveAndContinueAsync(plan.Id, suppliedContext);

        result.Status.ShouldBe(PlanStatus.Completed);
        await _skillExecutor.Received(2).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c =>
                c.UserName == GoalSelfReflectionAuditConstants.AuditUserName &&
                c.AccessToken!.Value == "owner-jwt" &&
                c.UserPermissions.Contains(Permissions.CanEditClients) &&
                !c.UserPermissions.Contains("clients.read") &&
                c.SessionId == expectedSessionId),
            Arg.Any<CancellationToken>());
        await _tokenIssuer.Received(1).IssueForOwnerAsync(ownerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApproveAndContinueAsync_SelfReflectionWithoutAToken_FallsBackToTheResumingCaller()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_followup", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 0;

        var candidate = new GoalCandidate
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            UserId = Guid.NewGuid().ToString(),
            Status = GoalCandidateStatus.Approved,
            OwnerPermissionsCsv = "clients.read"
        };
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Refused("the owner account is locked out"));
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var suppliedContext = CreateSkillContext();
        await _sut.ApproveAndContinueAsync(plan.Id, suppliedContext);

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c => c.UserName == suppliedContext.UserName),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApproveAndContinueAsync_UserGoalOrigin_ResumesUnderSuppliedContextUnchanged()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_followup", new(), null, true) },
            origin: AgentPlanOrigin.UserGoal);
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 0;

        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var suppliedContext = CreateSkillContext();
        var result = await _sut.ApproveAndContinueAsync(plan.Id, suppliedContext);

        result.Status.ShouldBe(PlanStatus.Completed);
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c =>
                c.UserName == suppliedContext.UserName &&
                c.UserPermissions.SequenceEqual(suppliedContext.UserPermissions) &&
                c.SessionId == suppliedContext.SessionId),
            Arg.Any<CancellationToken>());
        await _goalCandidateRepository.DidNotReceive().GetByPlanIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApproveAndContinueAsync_SelfReflectionWithoutApprovedCandidate_FallsBackToSuppliedContextWithoutCrashing()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_followup", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 0;

        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns((GoalCandidate?)null);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var suppliedContext = CreateSkillContext();
        var result = await _sut.ApproveAndContinueAsync(plan.Id, suppliedContext);

        result.Status.ShouldBe(PlanStatus.Completed);
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c =>
                c.UserName == suppliedContext.UserName &&
                c.SessionId == suppliedContext.SessionId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApproveAndContinueAsync_SelfReflectionWithEmptyOwnerPermissionsCsv_FallsBackToSuppliedContextWithoutCrashing()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_followup", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 0;

        var candidate = new GoalCandidate
        {
            PlanId = plan.Id,
            Status = GoalCandidateStatus.Approved,
            OwnerPermissionsCsv = "   "
        };
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var suppliedContext = CreateSkillContext();
        var result = await _sut.ApproveAndContinueAsync(plan.Id, suppliedContext);

        result.Status.ShouldBe(PlanStatus.Completed);
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c =>
                c.UserName == suppliedContext.UserName &&
                c.SessionId == suppliedContext.SessionId),
            Arg.Any<CancellationToken>());
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
    public async Task ExecutePlanAsync_PlanCompletesWithSessionId_TriggersTaskBoundaryCompaction()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) }, sessionId);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        _backgroundTaskService.Received(1).TriggerConversationCompaction(
            sessionId.ToString(), TaskBoundaryMinMessages);
    }

    [Test]
    public async Task ExecutePlanAsync_EmptyPlanCompletesWithSessionId_TriggersTaskBoundaryCompaction()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(Array.Empty<PlanStep>(), sessionId);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        _backgroundTaskService.Received(1).TriggerConversationCompaction(
            sessionId.ToString(), TaskBoundaryMinMessages);
    }

    [Test]
    public async Task ExecutePlanAsync_PlanCompletesWithoutSessionId_SkipsCompactionTriggerSilently()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_a", new(), null, true) });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        _backgroundTaskService.DidNotReceive().TriggerConversationCompaction(
            Arg.Any<string>(), Arg.Any<int>());
    }

    [Test]
    public async Task ExecutePlanAsync_PlanFailed_DoesNotTriggerCompaction()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_broken", new(), null, true) }, sessionId);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error("group not found"));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Failed));
        _backgroundTaskService.DidNotReceive().TriggerConversationCompaction(
            Arg.Any<string>(), Arg.Any<int>());
    }

    [Test]
    public async Task ExecutePlanAsync_PlanAborted_DoesNotTriggerCompaction()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "skill_a", new(), null, true),
            new PlanStep(2, "skill_b", new(), null, true)
        }, sessionId);
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
        _backgroundTaskService.DidNotReceive().TriggerConversationCompaction(
            Arg.Any<string>(), Arg.Any<int>());
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
        var destructiveDescriptor = BuildDefaultDescriptor("skill_destructive");
        _skillRegistry.GetSkillByName("skill_destructive").Returns(destructiveDescriptor);
        _riskClassifier.Classify(destructiveDescriptor).Returns(SkillRiskClass.Irreversible);
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
    public async Task ExecutePlanAsync_SelfReflectionOrigin_IrreversibleStep_RunsWithoutPauseDespiteLowUserLevel()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_irreversible", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        var descriptor = BuildDefaultDescriptor("skill_irreversible");
        _skillRegistry.GetSkillByName("skill_irreversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>())
            .Returns(new GoalCandidate { PlanId = plan.Id, Status = GoalCandidateStatus.Approved });
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Propose });
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        result.Status.ShouldBe(PlanStatus.Completed);
        await _autonomyRepository.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_SelfReflectionOriginWithoutApprovedCandidate_FallsBackToUserLevelAndPauses()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_irreversible", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        var descriptor = BuildDefaultDescriptor("skill_irreversible");
        _skillRegistry.GetSkillByName("skill_irreversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>())
            .Returns((GoalCandidate?)null);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Propose });

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_SelfReflectionOriginWithRejectedCandidate_FallsBackToUserLevelAndPauses()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_irreversible", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        var descriptor = BuildDefaultDescriptor("skill_irreversible");
        _skillRegistry.GetSkillByName("skill_irreversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>())
            .Returns(new GoalCandidate { PlanId = plan.Id, Status = GoalCandidateStatus.Rejected });
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Propose });

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_SelfReflectionOrigin_SensitiveStep_StillPauses()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_sensitive", new(), null, true) },
            origin: AgentPlanOrigin.SelfReflection);
        var descriptor = BuildDefaultDescriptor("skill_sensitive");
        _skillRegistry.GetSkillByName("skill_sensitive").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Sensitive);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_UserGoalOrigin_IrreversibleStep_StillPausesAtDefaultLevel()
    {
        var plan = CreatePlan(
            new[] { new PlanStep(1, "skill_irreversible", new(), null, true) },
            origin: AgentPlanOrigin.UserGoal);
        var descriptor = BuildDefaultDescriptor("skill_irreversible");
        _skillRegistry.GetSkillByName("skill_irreversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_UnknownSkill_PausesForApproval()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_unregistered", new(), null, true) });
        _skillRegistry.GetSkillByName("skill_unregistered").Returns((SkillDescriptor?)null);
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.PausedForApproval));
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_IrreversibleStep_AtAutonomous_PausesForApproval()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_irreversible", new(), null, true) });
        var descriptor = BuildDefaultDescriptor("skill_irreversible");
        _skillRegistry.GetSkillByName("skill_irreversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Autonomous });

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.PausedForApproval));
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_IrreversibleStep_AtFullyAutonomous_RunsWithoutPause()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_irreversible", new(), null, true) });
        var descriptor = BuildDefaultDescriptor("skill_irreversible");
        _skillRegistry.GetSkillByName("skill_irreversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.FullyAutonomous });
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
    }

    [Test]
    public async Task ExecutePlanAsync_ReversibleStep_AtAssisted_RunsWithoutPause()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_reversible", new(), null, true) });
        var descriptor = BuildDefaultDescriptor("skill_reversible");
        _skillRegistry.GetSkillByName("skill_reversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Reversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Assisted });
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
    }

    [Test]
    public async Task ExecutePlanAsync_ReversibleStep_AtPropose_PausesForApproval()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_reversible", new(), null, true) });
        var descriptor = BuildDefaultDescriptor("skill_reversible");
        _skillRegistry.GetSkillByName("skill_reversible").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Reversible);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _autonomyRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Propose });

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        Assert.That(result.Status, Is.EqualTo(PlanStatus.PausedForApproval));
        await _skillExecutor.DidNotReceive().ExecuteAsync(Arg.Any<SkillInvocation>(),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_SensitiveStep_AtFullyAutonomous_PausesForApproval()
    {
        var plan = CreatePlan(new[] { new PlanStep(1, "skill_sensitive", new(), null, true) });
        var descriptor = BuildDefaultDescriptor("skill_sensitive");
        _skillRegistry.GetSkillByName("skill_sensitive").Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Sensitive);
        var context = CreateSkillContext();
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
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

    [Test]
    public async Task ExecutePlanAsync_CreateEmployeeThenGetClientDetails_BridgesEmployeeIdToRealClientIdParam()
    {
        var employeeId = Guid.NewGuid();
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "create_employee", new(), "get_client_details", true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillRegistry.GetSkillByName("get_client_details")
            .Returns(BuildVerifyDescriptorFromSeeds("get_client_details"));

        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "create_employee"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { EmployeeId = employeeId, FirstName = "Ada", LastName = "Lovelace" }));
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "get_client_details"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i =>
                i.SkillName == "get_client_details" &&
                i.Parameters.ContainsKey("clientId") &&
                (Guid)i.Parameters["clientId"] == employeeId),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_CreateShiftThenGetShiftDetails_ResolvesShiftIdByCaseInsensitiveMatch()
    {
        var shiftId = Guid.NewGuid();
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "create_shift", new(), "get_shift_details", true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillRegistry.GetSkillByName("get_shift_details")
            .Returns(BuildVerifyDescriptorFromSeeds("get_shift_details"));

        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "create_shift"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new
            {
                ShiftId = shiftId,
                SealedOrderId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                MacroId = Guid.NewGuid()
            }));
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "get_shift_details"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i =>
                i.SkillName == "get_shift_details" &&
                i.Parameters.ContainsKey("shiftId") &&
                (Guid)i.Parameters["shiftId"] == shiftId),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_PlaceWorkThenCheckAvailability_DoesNotMisMapShiftIdIntoClientId()
    {
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "place_work", new(), "check_client_availability", true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _skillRegistry.GetSkillByName("check_client_availability")
            .Returns(BuildVerifyDescriptorFromSeeds("check_client_availability"));

        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "place_work"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new
            {
                ShiftId = shiftId,
                ShiftName = "Early",
                ClientId = clientId,
                Date = new DateOnly(2026, 6, 1)
            }));
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "check_client_availability"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }));

        var result = await _sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i =>
                i.SkillName == "check_client_availability" &&
                (Guid)i.Parameters["clientId"] == clientId &&
                (Guid)i.Parameters["ShiftId"] == shiftId),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecutePlanAsync_AmbiguousGenericIdVerify_LeavesParamUnbridgedAndLogsNote()
    {
        var recordingLogger = new RecordingLogger();
        var sut = new PlanStepExecutor(
            _planRepository, _skillExecutor, _skillRegistry, _riskClassifier,
            _autonomyRepository, _notificationService, _backgroundTaskService, _triggerService,
            _goalCandidateRepository, _tokenIssuer, recordingLogger);

        var plan = CreatePlan(new[]
        {
            new PlanStep(1, "create_shift", new(), "read_entity", true)
        });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        var verifyDescriptor = new SkillDescriptor(
            "read_entity", "desc", SkillCategory.Read,
            new[] { new SkillParameter("id", "the id", SkillParameterType.String, true) },
            Array.Empty<string>(), Array.Empty<LLMCapability>(), null);
        _skillRegistry.GetSkillByName("read_entity").Returns(verifyDescriptor);

        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "create_shift"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ShiftId = Guid.NewGuid(), ClientId = Guid.NewGuid() }));
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "read_entity"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { ok = true }));

        var result = await sut.ExecutePlanAsync(plan.Id, CreateSkillContext());

        Assert.That(result.Status, Is.EqualTo(PlanStatus.Completed));
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "read_entity" && !i.Parameters.ContainsKey("id")),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        Assert.That(recordingLogger.Messages.Any(m => m.Contains("parameter bridge") && m.Contains("'id'")),
            Is.True, "expected an ambiguity note to be logged for the unbridged 'id' parameter");
    }

    private static SkillDescriptor BuildDefaultDescriptor(string skillName) => new(
        skillName, "desc", SkillCategory.Crud,
        Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null);

    private static SkillDescriptor BuildVerifyDescriptorFromSeeds(string skillName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocateSkillSeeds()));
        var skill = doc.RootElement.GetProperty("skills").EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);
        if (skill.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Skill '{skillName}' not found in skill-seeds.json");
        }

        var parameters = new List<SkillParameter>();
        if (skill.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in paramsEl.EnumerateArray())
            {
                var name = p.GetProperty("name").GetString()!;
                var required = p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                parameters.Add(new SkillParameter(name, string.Empty, SkillParameterType.String, required));
            }
        }

        return new SkillDescriptor(
            skillName, "desc", SkillCategory.Read, parameters,
            Array.Empty<string>(), Array.Empty<LLMCapability>(), null);
    }

    private static string LocateSkillSeeds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate Klacks.Api/Application/Skills/Definitions/skill-seeds.json by walking up from the test base directory.");
    }

    private sealed class RecordingLogger : ILogger<PlanStepExecutor>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
