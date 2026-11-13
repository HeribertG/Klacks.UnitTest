// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The user interface sends every person of the schedule grid as agents, the people borrowed into the group for single
/// days included. The start drops them before the run lock, the job scope and the chain see the agents, and keeps the
/// order of the rest (the planner's roster order is a rank).
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public sealed class AutoWizardJobRunnerBorrowedAgentTests : AutoWizardJobRunnerTestHarness
{
    private static readonly Guid BorrowedAgentId = Guid.NewGuid();
    private IBorrowedAgentFilter _borrowedAgents = null!;

    protected override void AddServices(ServiceCollection services)
    {
        _borrowedAgents = TestBorrowedAgents.Removing(BorrowedAgentId);
        services.AddSingleton(_borrowedAgents);
    }

    [Test]
    public async Task BorrowedAgentSentByTheClient_IsNotAnAgentOfTheChain()
    {
        var groupId = Guid.NewGuid();
        var sourceToken = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var request = Request(groupId, sourceToken) with { AgentIds = [second, BorrowedAgentId, first] };

        var (_, state) = await RunChainAsync(request);

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        await _borrowedAgents.Received(1).RemoveBorrowedAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { second, BorrowedAgentId, first })),
            Arg.Is<Guid?>(id => id == groupId),
            Arg.Is<Guid?>(token => token == sourceToken),
            Arg.Any<CancellationToken>());
        await _wizardRunner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.AgentIds.SequenceEqual(new[] { second, first })),
            Arg.Any<CancellationToken>());
        await _holisticRunner.Received(1).StartAsync(
            Arg.Is<HolisticHarmonizerRunInput>(r => r.AgentIds.SequenceEqual(new[] { second, first })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_RecordsTheAgentsWithoutTheBorrowedOne()
    {
        var agent = Guid.NewGuid();
        var request = Request(Guid.NewGuid()) with { AgentIds = [BorrowedAgentId, agent] };

        var jobId = await _runner.StartAsync(request, CancellationToken.None);

        _jobScopes.Find(jobId).ShouldNotBeNull().AgentIds.ShouldBe([agent]);
        await WaitForChainAsync(jobId);
    }
}
