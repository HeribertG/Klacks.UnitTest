// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GoalPlanExecutionRetryBackgroundService: with the feature flag off, ExecuteAsync returns
/// immediately and never queries IGoalCandidateRepository; with the flag on, a candidate whose linked
/// plan is still PlanStatus.Drafting is handed to IGoalPlanExecutionService, while a candidate whose
/// plan has already moved past drafting (Executing, PausedForApproval, Completed, Aborted, Failed) is
/// never retried; a candidate whose linked plan cannot be found is skipped without throwing; and a
/// failure while retrying one candidate does not stop the remaining candidates in the same sweep.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class GoalPlanExecutionRetryBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private IAgentPlanRepository _planRepository = null!;
    private IGoalPlanExecutionService _executionService = null!;
    private ServiceProvider _serviceProvider = null!;
    private GoalPlanExecutionRetryBackgroundService? _sut;

    [SetUp]
    public void SetUp()
    {
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _planRepository = Substitute.For<IAgentPlanRepository>();
        _executionService = Substitute.For<IGoalPlanExecutionService>();

        var services = new ServiceCollection();
        services.AddSingleton(_goalCandidateRepository);
        services.AddSingleton(_planRepository);
        services.AddSingleton(_executionService);
        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_sut != null)
        {
            await _sut.StopAsync(CancellationToken.None);
            _sut.Dispose();
        }

        await _serviceProvider.DisposeAsync();
    }

    private static GoalCandidate MakeCandidate(Guid planId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid().ToString(),
        Title = "Tighten contract renewals",
        Rationale = "seen 3x this week",
        Status = GoalCandidateStatus.Approved,
        Confidence = GoalCandidateConfidence.High,
        SignalSource = "unstaffed_shift",
        DedupHash = "hash",
        OwnerPermissionsCsv = "CanEditClients",
        PlanId = planId
    };

    [Test]
    public async Task ExecuteAsync_FlagDisabled_ReturnsImmediatelyAndNeverQueriesRepository()
    {
        _sut = CreateSut(goalPlanExecutionRetryEnabled: false);

        await _sut.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(_sut.ExecuteTask!, Task.Delay(CompletionTimeout));

        finished.ShouldBe(_sut.ExecuteTask, "the service must return immediately when the flag is off, not poll");
        await _goalCandidateRepository.DidNotReceive().GetApprovedWithPlanAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_PlanStillDrafting_RetriesExecution()
    {
        var plan = new AgentPlan { Id = Guid.NewGuid(), Status = PlanStatus.Drafting };
        var candidate = MakeCandidate(plan.Id);
        _goalCandidateRepository.GetApprovedWithPlanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _sut = CreateSut(goalPlanExecutionRetryEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _executionService.Received(1).ExecuteForCandidateAsync(candidate.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_PlanAlreadyExecuting_IsNotRetried()
    {
        await AssertPlanStatusIsNeverRetriedAsync(PlanStatus.Executing);
    }

    [Test]
    public async Task RunCycleAsync_PlanPausedForApproval_IsNotRetried()
    {
        await AssertPlanStatusIsNeverRetriedAsync(PlanStatus.PausedForApproval);
    }

    [Test]
    public async Task RunCycleAsync_PlanCompleted_IsNotRetried()
    {
        await AssertPlanStatusIsNeverRetriedAsync(PlanStatus.Completed);
    }

    [Test]
    public async Task RunCycleAsync_PlanAborted_IsNotRetried()
    {
        await AssertPlanStatusIsNeverRetriedAsync(PlanStatus.Aborted);
    }

    [Test]
    public async Task RunCycleAsync_PlanFailed_IsNotRetried()
    {
        await AssertPlanStatusIsNeverRetriedAsync(PlanStatus.Failed);
    }

    private async Task AssertPlanStatusIsNeverRetriedAsync(string alreadyRunStatus)
    {
        var plan = new AgentPlan { Id = Guid.NewGuid(), Status = alreadyRunStatus };
        var candidate = MakeCandidate(plan.Id);
        _goalCandidateRepository.GetApprovedWithPlanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });
        _planRepository.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _sut = CreateSut(goalPlanExecutionRetryEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _executionService.DidNotReceive().ExecuteForCandidateAsync(candidate.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_LinkedPlanNotFound_IsSkippedWithoutThrowing()
    {
        var candidate = MakeCandidate(Guid.NewGuid());
        _goalCandidateRepository.GetApprovedWithPlanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });
        _planRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentPlan?)null);
        _sut = CreateSut(goalPlanExecutionRetryEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _executionService.DidNotReceive().ExecuteForCandidateAsync(candidate.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_OneCandidateFailsToRetry_RemainingCandidatesAreStillRetried()
    {
        var failingPlan = new AgentPlan { Id = Guid.NewGuid(), Status = PlanStatus.Drafting };
        var healthyPlan = new AgentPlan { Id = Guid.NewGuid(), Status = PlanStatus.Drafting };
        var failingCandidate = MakeCandidate(failingPlan.Id);
        var healthyCandidate = MakeCandidate(healthyPlan.Id);
        _goalCandidateRepository.GetApprovedWithPlanAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { failingCandidate, healthyCandidate });
        _planRepository.GetByIdAsync(failingPlan.Id, Arg.Any<CancellationToken>()).Returns(failingPlan);
        _planRepository.GetByIdAsync(healthyPlan.Id, Arg.Any<CancellationToken>()).Returns(healthyPlan);
        _executionService.When(x => x.ExecuteForCandidateAsync(failingCandidate.Id, Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("boom"));
        _sut = CreateSut(goalPlanExecutionRetryEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _executionService.Received(1).ExecuteForCandidateAsync(
            healthyCandidate.Id, Arg.Any<CancellationToken>());
    }

    private GoalPlanExecutionRetryBackgroundService CreateSut(bool goalPlanExecutionRetryEnabled)
    {
        var options = Options.Create(
            new BackgroundServiceOptions { GoalPlanExecutionRetry = goalPlanExecutionRetryEnabled });
        return new GoalPlanExecutionRetryBackgroundService(
            _serviceProvider, options, NullLogger<GoalPlanExecutionRetryBackgroundService>.Instance);
    }
}
