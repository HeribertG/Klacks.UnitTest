// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// AutoWizardController is open to every planner since autofill is planning work (owner rule 2026-11-07). Status and Cancel
/// therefore go through the job access rule first: a job the caller may not address answers exactly like an unknown job
/// (status Unknown without a result, cancel false), so neither its scenario nor the employee names of its result leak, and
/// it is not cancelled.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Controllers.Schedules;

[TestFixture]
public class AutoWizardControllerJobAccessTests
{
    private IAutoWizardJobRunner _runner = null!;
    private JobTerminalStateCache<AutoWizardJobResultDto> _states = null!;
    private IAutoWizardJobAccessGuard _access = null!;
    private AutoWizardController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _runner = Substitute.For<IAutoWizardJobRunner>();
        _states = JobTerminalStateCacheTestFactory.Create<AutoWizardJobResultDto>();
        _access = Substitute.For<IAutoWizardJobAccessGuard>();
        _controller = new AutoWizardController(_runner, _states, _access);
    }

    private static T OkValue<T>(ActionResult<T> result) =>
        (T)result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldNotBeNull();

    [Test]
    public async Task Status_OfAJobTheCallerMayNotAddress_IsUnknownWithoutResult()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(true);
        await _states.StoreCompletedAsync(jobId, new AutoWizardJobResultDto(jobId, Guid.NewGuid(), Guid.NewGuid(), "Auto", 1, [], [], []));
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);

        var status = OkValue(await _controller.Status(jobId, CancellationToken.None));

        status.Status.ShouldBe(WizardJobStatusValues.Unknown);
        status.Result.ShouldBeNull();
    }

    [Test]
    public async Task Status_OfAnAddressableRunningJob_IsRunning()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(true);
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(true);

        var status = OkValue(await _controller.Status(jobId, CancellationToken.None));

        status.Status.ShouldBe(WizardJobStatusValues.Running);
    }

    [Test]
    public async Task Cancel_OfAJobTheCallerMayNotAddress_CancelsNothing()
    {
        var jobId = Guid.NewGuid();
        _runner.TryCancel(jobId).Returns(true);
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);

        var response = OkValue(await _controller.Cancel(new CancelAutoWizardRequest(jobId), CancellationToken.None));

        response.Cancelled.ShouldBeFalse();
        _runner.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }

    [Test]
    public async Task Cancel_OfAnAddressableJob_Cancels()
    {
        var jobId = Guid.NewGuid();
        _runner.TryCancel(jobId).Returns(true);
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(true);

        var response = OkValue(await _controller.Cancel(new CancelAutoWizardRequest(jobId), CancellationToken.None));

        response.Cancelled.ShouldBeTrue();
    }
}
