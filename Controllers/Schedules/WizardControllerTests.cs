// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.Wizard;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Controllers.Schedules;

[TestFixture]
public class WizardControllerTests
{
    private IWizardJobRunner _runner = null!;
    private IWizardApplyService _applyService = null!;
    private IWizardBenchmarkService _benchmarkService = null!;
    private JobTerminalStateCache<WizardJobResultDto> _stateCache = null!;
    private WizardController _sut = null!;

    [SetUp]
    public void Setup()
    {
        _runner = Substitute.For<IWizardJobRunner>();
        _applyService = Substitute.For<IWizardApplyService>();
        _benchmarkService = Substitute.For<IWizardBenchmarkService>();
        _stateCache = new JobTerminalStateCache<WizardJobResultDto>();
        _sut = new WizardController(_runner, _applyService, _benchmarkService, _stateCache);
    }

    [Test]
    public async Task Start_ReturnsJobId()
    {
        var expectedJobId = Guid.NewGuid();
        _runner
            .StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedJobId);

        var result = await _sut.Start(
            new StartWizardRequest(
                new DateOnly(2026, 4, 20),
                new DateOnly(2026, 4, 30),
                new[] { Guid.NewGuid() },
                null,
                null),
            CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<StartWizardResponse>().JobId.ShouldBe(expectedJobId);
    }

    [Test]
    public void Cancel_ReturnsCancelledFlag()
    {
        var jobId = Guid.NewGuid();
        _runner.TryCancel(jobId).Returns(true);

        var result = _sut.Cancel(new CancelWizardRequest(jobId));

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<CancelWizardResponse>().Cancelled.ShouldBeTrue();
    }

    [Test]
    public async Task Apply_ReturnsCreatedWorkIds()
    {
        var jobId = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _applyService.ApplyAsync(jobId, false, Arg.Any<CancellationToken>())
            .Returns(new WizardApplyOutcome(ids, [], [], OverrideApplied: false));

        var result = await _sut.Apply(new ApplyWizardRequest(jobId), CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<ApplyWizardResponse>();
        response.CreatedWorkIds.ShouldBeEquivalentTo(ids);
        response.ComplianceViolations.ShouldBeEmpty();
        response.SkippedPlacements.ShouldBeEmpty();
        response.OverrideApplied.ShouldBeFalse();
    }

    [Test]
    public async Task Apply_ForwardsOverrideBlock_AndSurfacesPartitionReport()
    {
        var jobId = Guid.NewGuid();
        var skipped = new List<SkippedPlacementDto>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 4, 21), Guid.NewGuid(), "schedule.error-list.period-cap"),
        };
        _applyService.ApplyAsync(jobId, true, Arg.Any<CancellationToken>())
            .Returns(new WizardApplyOutcome([], [], skipped, OverrideApplied: true));

        var result = await _sut.Apply(new ApplyWizardRequest(jobId, OverrideBlock: true), CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<ApplyWizardResponse>();
        response.SkippedPlacements.ShouldBe(skipped);
        response.OverrideApplied.ShouldBeTrue();
        await _applyService.Received(1).ApplyAsync(jobId, true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_ReturnsBadRequest_WhenAgentLimitExceeded()
    {
        var agents = Enumerable.Range(0, WizardLimits.MaxAgents + 1).Select(_ => Guid.NewGuid()).ToArray();

        var result = await _sut.Start(
            new StartWizardRequest(
                new DateOnly(2026, 4, 20),
                new DateOnly(2026, 4, 30),
                agents,
                null,
                null),
            CancellationToken.None);

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        var error = badRequest.Value.ShouldBeOfType<WizardLimitErrorResponse>();
        error.Code.ShouldBe(WizardLimits.TooLargeErrorCode);
        error.Agents.ShouldBe(WizardLimits.MaxAgents + 1);
        await _runner.DidNotReceive().StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_ReturnsBadRequest_WhenShiftLimitExceeded()
    {
        var shifts = Enumerable.Range(0, WizardLimits.MaxShifts + 1).Select(_ => Guid.NewGuid()).ToArray();

        var result = await _sut.Start(
            new StartWizardRequest(
                new DateOnly(2026, 4, 20),
                new DateOnly(2026, 4, 30),
                new[] { Guid.NewGuid() },
                shifts,
                null),
            CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await _runner.DidNotReceive().StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Status_ReturnsRunning_WhenJobIsRunning()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(true);

        var result = _sut.Status(jobId);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<WizardJobStatusResponse>().Status.ShouldBe(WizardJobStatusValues.Running);
    }

    [Test]
    public void Status_ReturnsCompletedWithResult_WhenTerminalStateCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        var resultDto = new WizardJobResultDto(
            JobId: jobId,
            FinalHardViolations: 0,
            FinalStage1Completion: 1.0,
            TokenCount: 3,
            AvailableShiftSlots: 5,
            Tokens: [],
            Awards: [],
            Escalations: [],
            QualificationGaps: [],
            TimedOut: false);
        _stateCache.StoreCompleted(jobId, resultDto);

        var result = _sut.Status(jobId);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<WizardJobStatusResponse>();
        response.Status.ShouldBe(WizardJobStatusValues.Completed);
        response.Result.ShouldNotBeNull();
        response.Result.JobId.ShouldBe(jobId);
    }

    [Test]
    public void Status_ReturnsFailedWithReason_WhenFailureCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        _stateCache.StoreFailed(jobId, "boom");

        var result = _sut.Status(jobId);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var response = ok.Value.ShouldBeOfType<WizardJobStatusResponse>();
        response.Status.ShouldBe(WizardJobStatusValues.Failed);
        response.Reason.ShouldBe("boom");
    }

    [Test]
    public void Status_ReturnsUnknown_WhenJobNotTracked()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);

        var result = _sut.Status(jobId);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeOfType<WizardJobStatusResponse>().Status.ShouldBe(WizardJobStatusValues.Unknown);
    }

    [Test]
    public async Task Apply_ReturnsNotFound_WhenCacheEmpty()
    {
        var jobId = Guid.NewGuid();
        _applyService
            .ApplyAsync(jobId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<WizardApplyOutcome>(_ => throw new InvalidOperationException("No cached result"));

        var result = await _sut.Apply(new ApplyWizardRequest(jobId), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }
}
