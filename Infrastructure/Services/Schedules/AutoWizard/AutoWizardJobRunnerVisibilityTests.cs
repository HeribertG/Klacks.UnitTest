// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The normal autofill is open to every planner (owner rule 2026-11-07), and its chain runs in the background without a
/// user, so the start is where group visibility applies: agents the caller may not see are dropped like unknown ids
/// (order of the rest kept, a repeated id kept once at its first position), and the job's planning group and its employees are recorded for the job access rule (IAutoWizardJobAccessGuard)
/// before the job id is returned, so a client that joins the hub right after the start is already judged correctly.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public sealed class AutoWizardJobRunnerVisibilityTests : AutoWizardJobRunnerTestHarness
{
    private static readonly Guid HiddenAgentId = Guid.NewGuid();

    protected override void AddServices(ServiceCollection services)
    {
        services.AddSingleton(TestGroupWriteVisibility.ClientsHidden(HiddenAgentId));
    }

    [Test]
    public async Task AgentsTheCallerMayNotSee_AreDroppedBeforeTheChainPlans()
    {
        var firstVisible = Guid.NewGuid();
        var secondVisible = Guid.NewGuid();
        var request = Request(Guid.NewGuid()) with { AgentIds = [secondVisible, HiddenAgentId, firstVisible] };

        var (_, state) = await RunChainAsync(request);

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        await _wizardRunner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.AgentIds.SequenceEqual(new[] { secondVisible, firstVisible })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RepeatedAgentIds_AreKeptOnceAtTheirFirstPosition()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var request = Request(Guid.NewGuid()) with { AgentIds = [second, first, second, HiddenAgentId, first] };

        var (_, state) = await RunChainAsync(request);

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        await _wizardRunner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.AgentIds.SequenceEqual(new[] { second, first })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_RecordsThePlanningGroupAndTheVisibleAgentsBeforeItReturnsTheJobId()
    {
        var groupId = Guid.NewGuid();
        var visibleAgent = Guid.NewGuid();
        var request = Request(groupId) with { AgentIds = [visibleAgent, HiddenAgentId] };

        var jobId = await _runner.StartAsync(request, CancellationToken.None);

        var scope = _jobScopes.Find(jobId).ShouldNotBeNull();
        scope.GroupId.ShouldBe(groupId);
        scope.AgentIds.ShouldBe([visibleAgent]);
        await WaitForChainAsync(jobId);
    }
}
