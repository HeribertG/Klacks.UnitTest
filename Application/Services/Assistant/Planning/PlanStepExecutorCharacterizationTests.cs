// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for PlanStepExecutor that pin what PlanStepExecutorTests leaves open: the order of
/// adjacent per-step gates, a single goal-candidate lookup per run inside the abort boundary, how the renewed
/// token carries from step to step, that the approval bypass covers only the first resumed step, the context
/// flags every invocation runs with, exception propagation, the published step counts (truncated for a run,
/// untruncated for AbortAsync), the StepsJson parser surface, the verify parameter assembly, and that every
/// log line is written through the ILogger&lt;PlanStepExecutor&gt; passed to the constructor.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.Planning;

[TestFixture]
public class PlanStepExecutorCharacterizationTests
{
    private const string TransientMessage = "Rate limit exceeded (429)";

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
    private IConfirmationPreviewCatalog _previewCatalog = null!;
    private RecordingLogger _logger = null!;
    private PlanStepExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _skillRegistry.GetSkillByName(Arg.Any<string>()).Returns(callInfo => Descriptor(callInfo.Arg<string>()));
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>())
            .Returns(callInfo => ClassifyByName(callInfo.Arg<SkillDescriptor>().Name));
        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
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
        _previewCatalog = Substitute.For<IConfirmationPreviewCatalog>();
        _previewCatalog.HasPreview(Arg.Any<string>()).Returns(false);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(new { id = "ok" }));
        _logger = new RecordingLogger();
        _sut = new PlanStepExecutor(
            _planRepository, _skillExecutor, _skillRegistry, _riskClassifier, _autonomyRepository,
            _notificationService, _backgroundTaskService, _triggerService, _goalCandidateRepository,
            _tokenIssuer, _previewCatalog, _logger);
    }

    private static SkillRiskClass ClassifyByName(string name)
    {
        if (name.StartsWith("get_", StringComparison.Ordinal))
        {
            return SkillRiskClass.ReadOnly;
        }

        if (name.StartsWith("irrev_", StringComparison.Ordinal))
        {
            return SkillRiskClass.Irreversible;
        }

        return name.StartsWith("sens_", StringComparison.Ordinal) ? SkillRiskClass.Sensitive : SkillRiskClass.Reversible;
    }

    private static SkillDescriptor Descriptor(string name) => new(
        name, "desc", SkillCategory.Crud,
        Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "test-user",
        UserPermissions = new List<string> { "Admin" }
    };

    private static PlanStep Step(int order, string skill, string? verify = null, Dictionary<string, object?>? parameters = null)
        => new(order, skill, parameters ?? new Dictionary<string, object?>(), verify, true);

    private AgentPlan Plan(IEnumerable<PlanStep> steps, string? userId = null, string? origin = null, Guid? sessionId = null)
        => PlanFromJson(JsonSerializer.Serialize(steps.ToList()), userId, origin, sessionId);

    private AgentPlan PlanFromJson(string stepsJson, string? userId = null, string? origin = null, Guid? sessionId = null)
    {
        var plan = new AgentPlan
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = userId ?? Guid.NewGuid().ToString(),
            SessionId = sessionId,
            Goal = "goal",
            StepsJson = stepsJson,
            Status = PlanStatus.Drafting,
            CurrentStepIndex = 0,
            Origin = origin ?? AgentPlanOrigin.UserGoal
        };
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        return plan;
    }

    private static GoalCandidate ApprovedCandidate(AgentPlan plan, Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = plan.Id,
        UserId = ownerId.ToString(),
        Status = GoalCandidateStatus.Approved,
        OwnerPermissionsCsv = "clients.read"
    };

    private void Returns(string skill, params SkillResult[] results)
    {
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == skill),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(results[0], results.Skip(1).ToArray());
    }

    private List<SkillInvocation> Invocations() => _skillExecutor.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(ISkillExecutor.ExecuteAsync))
        .Select(c => (SkillInvocation)c.GetArguments()[0]!)
        .ToList();

    private List<SkillExecutionContext> InvocationContexts() => _skillExecutor.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(ISkillExecutor.ExecuteAsync))
        .Select(c => (SkillExecutionContext)c.GetArguments()[1]!)
        .ToList();

    [Test]
    public async Task PreviewGate_RunsBeforeTheVerifySkillGate()
    {
        _previewCatalog.HasPreview("bulk_skill").Returns(true);
        var plan = Plan(new[] { Step(1, "bulk_skill", verify: "sens_verify") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Failed);
        result.LastErrorMessage.ShouldBe(ConfirmationBindingMessages.ChatOnlyStep);
        _skillRegistry.DidNotReceive().GetSkillByName("sens_verify");
    }

    [Test]
    public async Task VerifySkillGate_RunsBeforeTheApprovalPause()
    {
        _skillRegistry.GetSkillByName("unknown_verify").Returns((SkillDescriptor?)null);
        var plan = Plan(new[] { Step(1, "irrev_delete", verify: "unknown_verify") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Failed);
        result.CurrentStepIndex.ShouldBe(0);
        result.LastErrorMessage.ShouldBe(
            "Verify skill 'unknown_verify' of step 'irrev_delete' is not a registered skill. A verify step must name a "
            + "read-only skill from the catalogue; the plan was stopped before the step ran.");
        await _triggerService.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
        Invocations().ShouldBeEmpty();
    }

    [Test]
    public async Task NonReadOnlyVerifySkill_FailsWithTheRiskClassInTheText()
    {
        var plan = Plan(new[] { Step(1, "skill_a", verify: "irrev_verify") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.LastErrorMessage.ShouldBe(
            "Verify skill 'irrev_verify' of step 'skill_a' is classified Irreversible, but a verify step must be "
            + "read-only; the plan was stopped before the step ran.");
    }

    [Test]
    public async Task PausedStep_PersistsBeforeTheTrigger_AndNeverRenewsTheTokenForIt()
    {
        var plan = Plan(new[] { Step(1, "irrev_delete") });
        var context = Context() with { TokenRenewalOwnerId = Guid.NewGuid() };

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        await _tokenIssuer.DidNotReceive().IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _planRepository.UpdateAsync(plan, Arg.Any<CancellationToken>());
            _planRepository.UpdateAsync(plan, Arg.Any<CancellationToken>());
            _triggerService.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task CancellationBeforeAPreviewedStep_AbortsInsteadOfFailing()
    {
        _previewCatalog.HasPreview("bulk_skill").Returns(true);
        var plan = Plan(new[] { Step(1, "skill_a"), Step(2, "bulk_skill") });
        using var cts = new CancellationTokenSource();
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return SkillResult.SuccessResult(null);
            });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context(), cts.Token);

        result.Status.ShouldBe(PlanStatus.Aborted);
        result.CurrentStepIndex.ShouldBe(1);
        await _planRepository.Received().UpdateAsync(
            Arg.Is<AgentPlan>(p => p.Status == PlanStatus.Aborted), CancellationToken.None);
    }

    [Test]
    public async Task SelfReflectionResume_LooksUpTheGoalCandidateExactlyOnce()
    {
        var plan = Plan(new[] { Step(1, "irrev_delete"), Step(2, "skill_b") }, origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>())
            .Returns(ApprovedCandidate(plan, Guid.NewGuid()));

        await _sut.ApproveAndContinueAsync(plan.Id, Context());

        await _goalCandidateRepository.Received(1).GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>());
        await _autonomyRepository.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UserGoalPlan_NeverLooksUpAGoalCandidate_AndReadsTheAutonomyRowOnce()
    {
        var context = Context();
        var plan = Plan(new[] { Step(1, "skill_a"), Step(2, "skill_b") });

        await _sut.ExecutePlanAsync(plan.Id, context);

        await _goalCandidateRepository.DidNotReceive().GetByPlanIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _autonomyRepository.Received(1).GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptyPlan_CompletesWithoutReadingCandidateOrAutonomy()
    {
        var plan = PlanFromJson("[]", origin: AgentPlanOrigin.SelfReflection);

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        await _goalCandidateRepository.DidNotReceive().GetByPlanIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _autonomyRepository.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _planRepository.Received(1).UpdateAsync(plan, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CandidateLookupCancelled_IsInsideTheAbortBoundary()
    {
        var plan = Plan(new[] { Step(1, "skill_a") }, origin: AgentPlanOrigin.SelfReflection);
        using var cts = new CancellationTokenSource();
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>())
            .Returns<Task<GoalCandidate?>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context(), cts.Token);

        result.Status.ShouldBe(PlanStatus.Aborted);
        await _planRepository.Received(1).UpdateAsync(
            Arg.Is<AgentPlan>(p => p.Status == PlanStatus.Aborted), CancellationToken.None);
        Invocations().ShouldBeEmpty();
    }

    [Test]
    public async Task RenewedToken_CarriesForward_WhenTheNextRenewalFails()
    {
        var ownerId = Guid.NewGuid();
        _tokenIssuer.IssueForOwnerAsync(ownerId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                InternalTokenResult.Issued(new BearerToken("jwt-a"), new[] { Roles.Authorised }),
                InternalTokenResult.Refused("locked"));
        var plan = Plan(new[] { Step(1, "skill_a"), Step(2, "skill_b") });
        var context = Context() with { TokenRenewalOwnerId = ownerId, AccessToken = new BearerToken("caller-jwt") };

        await _sut.ExecutePlanAsync(plan.Id, context);

        var contexts = InvocationContexts();
        contexts.Select(c => c.AccessToken!.Value).ShouldBe(new[] { "jwt-a", "jwt-a" });
        contexts[0].UserPermissions.ShouldBe(Permissions.ExpandRoles(new[] { Roles.Authorised }));
    }

    [Test]
    public async Task OneRenewalPerStep_EvenWithAVerifyAndATransientRetry()
    {
        var ownerId = Guid.NewGuid();
        Returns("skill_a", SkillResult.Error(TransientMessage), SkillResult.SuccessResult(new { id = "x" }));
        var plan = Plan(new[] { Step(1, "skill_a", verify: "get_a") });
        var context = Context() with { TokenRenewalOwnerId = ownerId };

        var result = await _sut.ExecutePlanAsync(plan.Id, context);

        result.Status.ShouldBe(PlanStatus.Completed);
        await _tokenIssuer.Received(1).IssueForOwnerAsync(ownerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        InvocationContexts().ShouldAllBe(c => c.AccessToken!.Value == "owner-jwt");
        Invocations().Select(i => i.SkillName).ShouldBe(new[] { "skill_a", "skill_a", "get_a" });
    }

    [TestCase(true, Roles.Authorised)]
    [TestCase(false, null)]
    public async Task Renewal_CapsTheRoleOnlyForAnExternalAgent(bool external, string? expectedRole)
    {
        var ownerId = Guid.NewGuid();
        var plan = Plan(new[] { Step(1, "skill_a") });
        var context = Context() with
        {
            TokenRenewalOwnerId = ownerId,
            ExternalAgentAccessMode = external ? PersonalAccessTokenAccessMode.Write : null
        };

        await _sut.ExecutePlanAsync(plan.Id, context);

        await _tokenIssuer.Received(1).IssueForOwnerAsync(ownerId, expectedRole, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SelfReflectionResume_IssuesOnceForTheIdentity_ThenOncePerStep_AndRenewsForTheOwner()
    {
        var ownerId = Guid.NewGuid();
        var plan = Plan(new[] { Step(1, "irrev_delete"), Step(2, "skill_b") }, origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>())
            .Returns(ApprovedCandidate(plan, ownerId));

        var result = await _sut.ApproveAndContinueAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        await _tokenIssuer.Received(3).IssueForOwnerAsync(ownerId, null, Arg.Any<CancellationToken>());
        InvocationContexts().ShouldAllBe(c => c.TokenRenewalOwnerId == ownerId);
    }

    [Test]
    public async Task SelfReflectionResume_OwnerIdNotAGuid_FallsBackToTheCallerWithoutIssuing()
    {
        var plan = Plan(new[] { Step(1, "skill_a") }, origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;
        var candidate = ApprovedCandidate(plan, Guid.NewGuid());
        candidate.UserId = "not-a-guid";
        _goalCandidateRepository.GetByPlanIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        var context = Context();

        await _sut.ApproveAndContinueAsync(plan.Id, context);

        await _tokenIssuer.DidNotReceive().IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        InvocationContexts().Single().UserName.ShouldBe("test-user");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("'not-a-guid' is not a Guid"));
    }

    [Test]
    public async Task ApprovalBypass_CoversOnlyTheFirstResumedStep()
    {
        var userId = Guid.NewGuid();
        var plan = Plan(new[] { Step(1, "irrev_one"), Step(2, "irrev_two") }, userId: userId.ToString());
        plan.Status = PlanStatus.PausedForApproval;

        var result = await _sut.ApproveAndContinueAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.PausedForApproval);
        result.CurrentStepIndex.ShouldBe(1);
        Invocations().Select(i => i.SkillName).ShouldBe(new[] { "irrev_one" });
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<PlanPausedForApprovalTriggerEvent>(e => e.StepIndex == 1 && e.TotalSteps == 2 && e.SkillName == "irrev_two"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EveryInvocation_BypassesTheChatGate_AndHasNoUiActions()
    {
        var plan = Plan(new[] { Step(1, "skill_a", verify: "get_a") });
        var context = Context() with { SupportsUiActions = true, BypassAutonomyGate = false };

        await _sut.ExecutePlanAsync(plan.Id, context);

        var contexts = InvocationContexts();
        contexts.Count.ShouldBe(2);
        contexts.ShouldAllBe(c => c.BypassAutonomyGate && !c.SupportsUiActions);
    }

    [Test]
    public async Task VerifyFailure_UsesTheExactText_AndKeepsTheStepIndex()
    {
        Returns("get_a", SkillResult.Error("not there"));
        var plan = Plan(new[] { Step(1, "skill_x"), Step(2, "skill_a", verify: "get_a") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Failed);
        result.CurrentStepIndex.ShouldBe(1);
        result.LastErrorMessage.ShouldBe("Verify 'get_a' failed: not there");
    }

    [Test]
    public async Task VerifyFailure_WhileCancelled_Aborts()
    {
        using var cts = new CancellationTokenSource();
        _skillExecutor.ExecuteAsync(Arg.Is<SkillInvocation>(i => i.SkillName == "get_a"),
                Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return SkillResult.Error("cancelled");
            });
        var plan = Plan(new[] { Step(1, "skill_a", verify: "get_a") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context(), cts.Token);

        result.Status.ShouldBe(PlanStatus.Aborted);
        result.LastErrorMessage.ShouldBeNull();
    }

    [Test]
    public async Task VerifyTransientFailure_IsRetriedOnce()
    {
        Returns("get_a", SkillResult.Error(TransientMessage), SkillResult.SuccessResult(null));
        var plan = Plan(new[] { Step(1, "skill_a", verify: "get_a") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        Invocations().Count(i => i.SkillName == "get_a").ShouldBe(2);
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning
            && e.Message == $"Plan step get_a transient failure (attempt 1/1): {TransientMessage}");
    }

    [Test]
    public async Task FailedMutation_WithoutMessage_UsesTheFallbackText_AndNeverRunsTheVerify()
    {
        Returns("skill_a", new SkillResult { Success = false, Message = null });
        var plan = Plan(new[] { Step(1, "skill_a", verify: "get_a") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Failed);
        result.LastErrorMessage.ShouldBe("Skill returned no error message");
        Invocations().Select(i => i.SkillName).ShouldBe(new[] { "skill_a" });
    }

    [Test]
    public async Task OperationCanceledWithoutCancelledToken_Propagates()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<SkillResult>>(_ => throw new OperationCanceledException());
        var plan = Plan(new[] { Step(1, "skill_a") });

        await Should.ThrowAsync<OperationCanceledException>(() => _sut.ExecutePlanAsync(plan.Id, Context()));

        plan.Status.ShouldBe(PlanStatus.Executing);
    }

    [Test]
    public async Task SkillExecutorThrows_Propagates_AndLeavesThePlanExecuting()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<SkillResult>>(_ => throw new InvalidOperationException("boom"));
        var plan = Plan(new[] { Step(1, "skill_a") });

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.ExecutePlanAsync(plan.Id, Context()));

        plan.Status.ShouldBe(PlanStatus.Executing);
        await _planRepository.Received(1).UpdateAsync(plan, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BroadcastFailure_IsLoggedAndTheRunContinues()
    {
        _notificationService.SendPlanUpdateAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns<Task>(_ => throw new InvalidOperationException("hub down"));
        var plan = Plan(new[] { Step(1, "skill_a") });

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException
            && e.Message == $"Plan {plan.Id} update broadcast failed (status={PlanStatus.Completed})");
    }

    [Test]
    public async Task PersistFailure_Propagates()
    {
        _planRepository.UpdateAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));
        var plan = Plan(new[] { Step(1, "skill_a") });

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.ExecutePlanAsync(plan.Id, Context()));

        Invocations().ShouldBeEmpty();
    }

    [Test]
    public async Task Broadcasts_FollowEveryPersistWithTheTruncatedStepCount()
    {
        var plan = Plan(new[] { Step(1, "skill_a"), Step(2, "skill_b") });

        await _sut.ExecutePlanAsync(plan.Id, Context());

        Received.InOrder(() =>
        {
            _notificationService.SendPlanUpdateAsync(plan.UserId!, plan.Id, PlanStatus.Executing, 0, 2, null);
            _notificationService.SendPlanUpdateAsync(plan.UserId!, plan.Id, PlanStatus.Executing, 1, 2, null);
            _notificationService.SendPlanUpdateAsync(plan.UserId!, plan.Id, PlanStatus.Executing, 2, 2, null);
            _notificationService.SendPlanUpdateAsync(plan.UserId!, plan.Id, PlanStatus.Completed, 2, 2, null);
        });
    }

    [Test]
    public async Task PlanAboveTheStepBudget_RunsOnlyTheBudget_AndPublishesTheTruncatedCount()
    {
        var steps = Enumerable.Range(1, LLMLoopConstants.MaxPlanSteps + 1).Select(i => Step(i, $"skill_{i}")).ToList();
        var plan = Plan(steps);

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        result.CurrentStepIndex.ShouldBe(LLMLoopConstants.MaxPlanSteps);
        Invocations().Count.ShouldBe(LLMLoopConstants.MaxPlanSteps);
        await _notificationService.Received(1).SendPlanUpdateAsync(
            plan.UserId!, plan.Id, PlanStatus.Completed, LLMLoopConstants.MaxPlanSteps, LLMLoopConstants.MaxPlanSteps, null);
    }

    [Test]
    public async Task PauseTrigger_CarriesTheTruncatedStepCount()
    {
        var steps = new List<PlanStep> { Step(1, "irrev_first") };
        steps.AddRange(Enumerable.Range(2, LLMLoopConstants.MaxPlanSteps).Select(i => Step(i, $"skill_{i}")));
        var plan = Plan(steps);

        await _sut.ExecutePlanAsync(plan.Id, Context());

        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<PlanPausedForApprovalTriggerEvent>(e =>
                e.TotalSteps == LLMLoopConstants.MaxPlanSteps && e.SkillName == "irrev_first" && e.StepIndex == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbortAsync_PublishesTheUntruncatedStepCount()
    {
        var steps = Enumerable.Range(1, LLMLoopConstants.MaxPlanSteps + 1).Select(i => Step(i, $"skill_{i}")).ToList();
        var plan = Plan(steps);
        plan.Status = PlanStatus.PausedForApproval;

        await _sut.AbortAsync(plan.Id);

        await _notificationService.Received(1).SendPlanUpdateAsync(
            plan.UserId!, plan.Id, PlanStatus.Aborted, 0, LLMLoopConstants.MaxPlanSteps + 1, null);
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Information
            && e.Message == $"Plan {plan.Id} aborted while not actively running (was {PlanStatus.Aborted})");
    }

    [Test]
    public async Task AbortAsync_UnknownPlan_ReturnsNull()
    {
        var planId = Guid.NewGuid();
        _planRepository.GetByIdAsync(planId, Arg.Any<CancellationToken>()).Returns((AgentPlan?)null);

        var result = await _sut.AbortAsync(planId);

        result.ShouldBeNull();
        await _planRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PlanWithoutUserId_NeitherBroadcastsNorCompacts()
    {
        var plan = Plan(new[] { Step(1, "skill_a") }, userId: string.Empty, sessionId: Guid.NewGuid());

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        await _notificationService.DidNotReceive().SendPlanUpdateAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>());
        _backgroundTaskService.DidNotReceive().TriggerConversationCompaction(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>());
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("skipping task-boundary compaction"));
    }

    [Test]
    public async Task Parser_ReadsCamelCaseKeysAndJsonValueKinds()
    {
        var plan = PlanFromJson(
            """[{"skill":"skill_a","params":{"n":5,"d":1.5,"s":"x","b":true,"f":false,"nul":null,"obj":{"a":1},"arr":[1,2]}}]""");

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        var parameters = Invocations().Single().Parameters;
        parameters["n"].ShouldBe(5L);
        parameters["d"].ShouldBe(1.5d);
        parameters["s"].ShouldBe("x");
        parameters["b"].ShouldBe(true);
        parameters["f"].ShouldBe(false);
        parameters.ContainsKey("nul").ShouldBeFalse();
        parameters["obj"].ShouldBe("""{"a":1}""");
        parameters["arr"].ShouldBe("[1,2]");
        parameters.ContainsKey("N").ShouldBeTrue();
    }

    [Test]
    public async Task Parser_SkipsNonObjectsAndBlankSkills_AndPascalSkillWinsOverCamel()
    {
        var plan = PlanFromJson(
            """[1,"x",{"params":{}},{"Skill":"  ","skill":"skill_hidden"},{"Skill":null},{"Skill":"skill_a"}]""");

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        result.CurrentStepIndex.ShouldBe(1);
        Invocations().Select(i => i.SkillName).ShouldBe(new[] { "skill_a" });
    }

    [TestCase("{not json")]
    [TestCase("""{"Skill":"skill_a"}""")]
    [TestCase("   ")]
    public async Task Parser_MalformedOrNonArrayJson_CompletesWithNoSteps(string stepsJson)
    {
        var plan = PlanFromJson(stepsJson);

        var result = await _sut.ExecutePlanAsync(plan.Id, Context());

        result.Status.ShouldBe(PlanStatus.Completed);
        Invocations().ShouldBeEmpty();
    }

    [Test]
    public async Task Parser_NonStringSkill_ThrowsBeforeAnyStateChange()
    {
        var plan = PlanFromJson("""[{"Skill":5}]""");

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.ExecutePlanAsync(plan.Id, Context()));

        await _planRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StepResults_AreKeyedByOrder_SoPrevResolvesFromTheHighestOrderSoFar()
    {
        Returns("skill_high", SkillResult.SuccessResult(new { id = "from-high" }));
        Returns("skill_low", SkillResult.SuccessResult(new { id = "from-low" }));
        var plan = PlanFromJson(
            """[{"Skill":"skill_high","Order":5},{"Skill":"skill_low","Order":2},{"Skill":"skill_c","Params":{"ref":"$prev.id"}}]""");

        await _sut.ExecutePlanAsync(plan.Id, Context());

        Invocations().Single(i => i.SkillName == "skill_c").Parameters["ref"].ShouldBe("from-high");
    }

    [Test]
    public async Task ResumedPlan_CannotResolvePrevFromStepsBeforeThePause()
    {
        var plan = Plan(new[]
        {
            Step(1, "skill_a"),
            Step(2, "skill_b", parameters: new Dictionary<string, object?> { ["ref"] = "$prev.id" })
        });
        plan.Status = PlanStatus.PausedForApproval;
        plan.CurrentStepIndex = 1;

        await _sut.ApproveAndContinueAsync(plan.Id, Context());

        Invocations().Single().Parameters.ContainsKey("ref").ShouldBeFalse();
    }

    [Test]
    public async Task VerifyParameters_PayloadThenAliasesThenLiteralStepParams()
    {
        var entityId = Guid.NewGuid();
        Returns("skill_a", SkillResult.SuccessResult(new Dictionary<string, object?>
        {
            ["Id"] = entityId,
            ["Gone"] = null,
            ["Kept"] = "payload"
        }));
        var plan = Plan(new[]
        {
            Step(1, "skill_a", verify: "get_a", parameters: new Dictionary<string, object?>
            {
                ["kept"] = "literal",
                ["ref"] = "$prev.id",
                ["empty"] = null
            })
        });

        await _sut.ExecutePlanAsync(plan.Id, Context());

        var verify = Invocations().Single(i => i.SkillName == "get_a").Parameters;
        verify["id"].ShouldBe(entityId);
        verify["KEPT"].ShouldBe("literal");
        verify.ContainsKey("Gone").ShouldBeFalse();
        verify.ContainsKey("ref").ShouldBeFalse();
        verify.ContainsKey("empty").ShouldBeFalse();
    }

    [Test]
    public async Task VerifyParameters_PrimitivePayload_ContributesNothing()
    {
        Returns("skill_a", SkillResult.SuccessResult("just text"));
        var plan = Plan(new[]
        {
            Step(1, "skill_a", verify: "get_a", parameters: new Dictionary<string, object?> { ["scope"] = "all" })
        });

        await _sut.ExecutePlanAsync(plan.Id, Context());

        var verify = Invocations().Single(i => i.SkillName == "get_a").Parameters;
        verify.Keys.ShouldBe(new[] { "scope" });
    }

    [Test]
    public async Task Logging_AutonomyFallbackAndResumeFallback_GoThroughTheExecutorLogger()
    {
        var plan = Plan(new[] { Step(1, "skill_a") }, origin: AgentPlanOrigin.SelfReflection);
        plan.Status = PlanStatus.PausedForApproval;

        await _sut.ApproveAndContinueAsync(plan.Id, Context());

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message ==
            $"Plan {plan.Id} claims origin {AgentPlanOrigin.SelfReflection} but has no approved goal candidate; falling back to the user's autonomy level");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.StartsWith(
            $"Plan {plan.Id} resumed under origin {AgentPlanOrigin.SelfReflection} but has no approved goal candidate with frozen permissions"));
    }

    [Test]
    public async Task Logging_RenewalFailureTriggerSkipAndCompletion_GoThroughTheExecutorLogger()
    {
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Refused("locked"));
        var plan = Plan(new[] { Step(1, "skill_a") });

        await _sut.ExecutePlanAsync(plan.Id, Context() with { TokenRenewalOwnerId = Guid.NewGuid() });

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message ==
            $"Plan {plan.Id} could not renew its token between steps: locked; the remaining steps run with the previous one and will fail once it expires");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message ==
            $"Plan {plan.Id} completed all 1 step(s)");

        var paused = Plan(new[] { Step(1, "irrev_x") }, userId: "user-1");
        await _sut.ExecutePlanAsync(paused.Id, Context());

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message ==
            $"Plan {paused.Id} paused for approval at step 0 (irrev_x)");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message ==
            $"Plan {paused.Id} paused for approval but UserId user-1 is missing or not a Guid; skipping proactive trigger");
    }

    [Test]
    public async Task Logging_GateRejectionsAndFailures_GoThroughTheExecutorLogger()
    {
        _previewCatalog.HasPreview("bulk_skill").Returns(true);
        var previewed = Plan(new[] { Step(1, "bulk_skill") });
        await _sut.ExecutePlanAsync(previewed.Id, Context());

        var rejected = Plan(new[] { Step(1, "skill_a", verify: "sens_verify") });
        await _sut.ExecutePlanAsync(rejected.Id, Context());

        Returns("skill_fail", SkillResult.Error("no client"));
        var failed = Plan(new[] { Step(1, "skill_fail") });
        await _sut.ExecutePlanAsync(failed.Id, Context());

        Returns("get_v", SkillResult.Error("missing"));
        var verifyFailed = Plan(new[] { Step(1, "skill_ok", verify: "get_v") });
        await _sut.ExecutePlanAsync(verifyFailed.Id, Context());

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message ==
            $"Plan {previewed.Id} stopped at step 0: bulk_skill shows a server preview and runs only in the chat");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.StartsWith(
            $"Plan {rejected.Id} rejected at step 0 (skill_a): Verify skill 'sens_verify' of step 'skill_a' is classified Sensitive"));
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message ==
            $"Plan {failed.Id} failed at step 0 (skill_fail): no client");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message ==
            $"Plan {verifyFailed.Id} verify failed at step 0 (skill_ok/get_v): missing");
    }

    [Test]
    public async Task Logging_CooperativeAbortAndTriggerFailure_GoThroughTheExecutorLogger()
    {
        _triggerService.OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProactiveDispatchOutcome>>(_ => throw new InvalidOperationException("dispatch"));
        var paused = Plan(new[] { Step(1, "irrev_x") });
        await _sut.ExecutePlanAsync(paused.Id, Context());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var aborted = Plan(new[] { Step(1, "skill_a") });
        await _sut.ExecutePlanAsync(aborted.Id, Context(), cts.Token);

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException
            && e.Message == $"Plan {paused.Id} paused-for-approval trigger dispatch failed at step 0");
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message ==
            $"Plan {aborted.Id} aborted cooperatively at step 0");
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<PlanStepExecutor>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
