// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Schedules;

/// <summary>
/// Covers the status endpoint that lets a client which missed the SignalR events recover the outcome
/// of a holistic harmonizer run.
/// </summary>
[TestFixture]
public sealed class HolisticHarmonizerControllerTests
{
    private IHolisticHarmonizerJobRunner _runner = null!;
    private JobTerminalStateCache<HolisticHarmonizerRunResponse> _stateCache = null!;
    private HolisticHarmonizerController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _runner = Substitute.For<IHolisticHarmonizerJobRunner>();
        _stateCache = new JobTerminalStateCache<HolisticHarmonizerRunResponse>();
        _sut = new HolisticHarmonizerController(
            _runner,
            Substitute.For<IHolisticHarmonizerApplyService>(),
            null!,
            _stateCache);
    }

    private static HolisticHarmonizerRunResponse MakeResponse(Guid jobId) => new(
        JobId: jobId,
        LlmModelId: "stub-model",
        FitnessBefore: 0.4,
        FitnessAfter: 0.6,
        AcceptedSwaps: [],
        RejectedSwaps: [],
        Batches: [],
        AgentDisplayNames: [],
        QualificationGaps: [],
        LlmParsingError: null,
        LlmRawResponsePreview: null);

    [Test]
    public void Status_ReturnsRunning_WhenJobIsRunning()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(true);

        var ok = _sut.Status(jobId).Result.ShouldBeOfType<OkObjectResult>();

        ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>()
            .Status.ShouldBe(WizardJobStatusValues.Running);
    }

    [Test]
    public void Status_ReturnsCompletedWithResult_WhenTerminalStateCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        var response = MakeResponse(jobId);
        _stateCache.StoreCompleted(jobId, response);

        var ok = _sut.Status(jobId).Result.ShouldBeOfType<OkObjectResult>();

        var body = ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>();
        body.Status.ShouldBe(WizardJobStatusValues.Completed);
        body.Result.ShouldBe(response);
        body.Reason.ShouldBeNull();
    }

    [Test]
    public void Status_ReturnsFailedWithReason_WhenFailureCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        _stateCache.StoreFailed(jobId, "Model produced no usable response.");

        var ok = _sut.Status(jobId).Result.ShouldBeOfType<OkObjectResult>();

        var body = ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>();
        body.Status.ShouldBe(WizardJobStatusValues.Failed);
        body.Reason.ShouldBe("Model produced no usable response.");
        body.Result.ShouldBeNull();
    }

    [Test]
    public void Status_ReturnsCancelled_WhenCancelCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        _stateCache.StoreCancelled(jobId);

        var ok = _sut.Status(jobId).Result.ShouldBeOfType<OkObjectResult>();

        ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>()
            .Status.ShouldBe(WizardJobStatusValues.Cancelled);
    }

    [Test]
    public void Status_ReturnsUnknown_WhenJobNotTracked()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);

        var ok = _sut.Status(jobId).Result.ShouldBeOfType<OkObjectResult>();

        ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>()
            .Status.ShouldBe(WizardJobStatusValues.Unknown);
    }
}
