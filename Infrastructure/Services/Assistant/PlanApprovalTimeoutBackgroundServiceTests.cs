// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PlanApprovalTimeoutBackgroundService: with the feature flag off, ExecuteAsync returns
/// immediately and never queries IAgentPlanRepository; with the flag on, a single sweep aborts every
/// overdue plan it is handed with a populated LastErrorMessage and persists it; an empty result set
/// leaves the repository's UpdateAsync untouched; and a failure while aborting one plan is swallowed so
/// the remaining plans in the same sweep are still processed.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class PlanApprovalTimeoutBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private IAgentPlanRepository _planRepository = null!;
    private ServiceProvider _serviceProvider = null!;
    private PlanApprovalTimeoutBackgroundService? _sut;

    [SetUp]
    public void SetUp()
    {
        _planRepository = Substitute.For<IAgentPlanRepository>();

        var services = new ServiceCollection();
        services.AddSingleton(_planRepository);
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

    [Test]
    public async Task ExecuteAsync_FlagDisabled_ReturnsImmediatelyAndNeverQueriesRepository()
    {
        _sut = CreateSut(planApprovalTimeoutEnabled: false);

        await _sut.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(_sut.ExecuteTask!, Task.Delay(CompletionTimeout));

        finished.ShouldBe(_sut.ExecuteTask, "the service must return immediately when the flag is off, not poll");
        await _planRepository.DidNotReceive().GetStalePausedForApprovalAsync(
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_OverduePlanFound_AbortsItWithReasonAndPersists()
    {
        var plan = new AgentPlan { Id = Guid.NewGuid(), Status = PlanStatus.PausedForApproval };
        _planRepository.GetStalePausedForApprovalAsync(
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentPlan> { plan });
        _sut = CreateSut(planApprovalTimeoutEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        plan.Status.ShouldBe(PlanStatus.Aborted);
        plan.LastErrorMessage.ShouldNotBeNullOrWhiteSpace();
        await _planRepository.Received(1).UpdateAsync(plan, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_NoOverduePlans_NeverCallsUpdate()
    {
        _planRepository.GetStalePausedForApprovalAsync(
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentPlan>());
        _sut = CreateSut(planApprovalTimeoutEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _planRepository.DidNotReceive().UpdateAsync(Arg.Any<AgentPlan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_OnePlanFailsToUpdate_RemainingPlansAreStillAborted()
    {
        var failingPlan = new AgentPlan { Id = Guid.NewGuid(), Status = PlanStatus.PausedForApproval };
        var healthyPlan = new AgentPlan { Id = Guid.NewGuid(), Status = PlanStatus.PausedForApproval };
        _planRepository.GetStalePausedForApprovalAsync(
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentPlan> { failingPlan, healthyPlan });
        _planRepository.When(x => x.UpdateAsync(failingPlan, Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("boom"));
        _sut = CreateSut(planApprovalTimeoutEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _planRepository.Received(1).UpdateAsync(failingPlan, Arg.Any<CancellationToken>());
        await _planRepository.Received(1).UpdateAsync(healthyPlan, Arg.Any<CancellationToken>());
        healthyPlan.Status.ShouldBe(PlanStatus.Aborted);
    }

    private PlanApprovalTimeoutBackgroundService CreateSut(bool planApprovalTimeoutEnabled)
    {
        var options = Options.Create(new BackgroundServiceOptions { PlanApprovalTimeout = planApprovalTimeoutEnabled });
        return new PlanApprovalTimeoutBackgroundService(
            _serviceProvider, options, NullLogger<PlanApprovalTimeoutBackgroundService>.Instance);
    }
}
