// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the autofill presence of an AutoWizard run: recorded before the start returns, under the group the run's
/// scenarios will belong to, never for a refused start, and ended only after the intermediate scenarios are deleted -
/// on failure and on cancel as well, where the intermediates are deleted with a live token.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public sealed class AutoWizardJobRunnerPresenceTests : AutoWizardJobRunnerTestHarness
{
    [Test]
    public async Task Start_RecordsThePresenceBeforeReturning_UnderTheRequestedGroup()
    {
        var groupId = Guid.NewGuid();

        var jobId = await _runner.StartAsync(Request(groupId), CancellationToken.None);

        await _presenceTracker.Received(1).BeginAsync(groupId, PeriodFrom, PeriodUntil);
        await WaitForChainAsync(jobId);
    }

    [Test]
    public async Task Start_WithoutGroupOnASourceScenario_RecordsThePresenceUnderTheSourceScenariosGroup()
    {
        var sourceToken = Guid.NewGuid();
        var sourceGroupId = Guid.NewGuid();
        _scenarioRepository.GetByTokenAsync(sourceToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Id = Guid.NewGuid(), Token = sourceToken, GroupId = sourceGroupId });

        await RunChainAsync(Request(groupId: null, analyseToken: sourceToken));

        await _presenceTracker.Received(1).BeginAsync(sourceGroupId, PeriodFrom, PeriodUntil);
    }

    [Test]
    public async Task RefusedStart_RecordsNoPresence()
    {
        var wizardStageGate = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _wizardRunner.StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>()).Returns(wizardStageGate.Task);
        var request = Request(Guid.NewGuid());
        var firstJobId = await _runner.StartAsync(request, CancellationToken.None);

        await Should.ThrowAsync<AutofillRunConflictException>(() => _runner.StartAsync(request, CancellationToken.None));

        await _presenceTracker.ReceivedWithAnyArgs(1).BeginAsync(default, default, default);
        wizardStageGate.SetResult(_wizardStageJobId);
        await WaitForChainAsync(firstJobId);
    }

    [Test]
    public async Task CompletedChain_EndsThePresence_OnlyAfterTheIntermediatesAreDeleted()
    {
        int? deletedWhenEnded = null;
        _presenceTracker.When(tracker => tracker.EndAsync(_presenceRunId)).Do(_ => deletedWhenEnded = DeletedScenarioIds().Count);

        var (_, state) = await RunChainAsync();
        await WaitForPresenceEndAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        deletedWhenEnded.ShouldBe(2);
        await _presenceTracker.Received(1).EndAsync(_presenceRunId);
    }

    [Test]
    public async Task FailedChain_EndsThePresence_Too()
    {
        _wizardRunner.StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>())
            .Returns<Guid>(_ => throw new InvalidOperationException("wizard stage refused"));

        var (_, state) = await RunChainAsync();
        await WaitForPresenceEndAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Failed);
        await _presenceTracker.Received(1).EndAsync(_presenceRunId);
    }

    [Test]
    public async Task CancelledChain_RemovesItsIntermediatesWithALiveToken_AndEndsThePresence()
    {
        _holisticRunner.StartAsync(Arg.Any<HolisticHarmonizerRunInput>(), Arg.Any<CancellationToken>())
            .Returns(call => BlockUntilCancelledAsync(call.ArgAt<CancellationToken>(1)));

        var jobId = await _runner.StartAsync(Request(Guid.NewGuid()), CancellationToken.None);
        await WaitUntilAsync(() => _holisticRunner.ReceivedCalls().Any(), "The chain did not reach stage 3 in time.");
        _runner.TryCancel(jobId).ShouldBeTrue();
        var (_, state) = await WaitForChainAsync(jobId);
        await WaitForPresenceEndAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Failed);
        await _mediator.Received(1).Send(
            Arg.Is<DeleteAnalyseScenarioCommand>(command => command.ScenarioId == _wizardScenarioId),
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
        await _presenceTracker.Received(1).EndAsync(_presenceRunId);
    }
}
