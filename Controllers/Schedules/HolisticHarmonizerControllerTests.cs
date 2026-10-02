// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.UnitTest.TestHelpers;
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
    private IHolisticHarmonizerApplyService _applyService = null!;
    private JobTerminalStateCache<HolisticHarmonizerRunResponse> _stateCache = null!;
    private HolisticHarmonizerController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _runner = Substitute.For<IHolisticHarmonizerJobRunner>();
        _applyService = Substitute.For<IHolisticHarmonizerApplyService>();
        _stateCache = JobTerminalStateCacheTestFactory.Create<HolisticHarmonizerRunResponse>();
        _sut = new HolisticHarmonizerController(
            _runner,
            _applyService,
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
    public async Task Status_ReturnsRunning_WhenJobIsRunning()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(true);

        var response = await _sut.Status(jobId, CancellationToken.None);
        var ok = response.Result.ShouldBeOfType<OkObjectResult>();

        ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>()
            .Status.ShouldBe(WizardJobStatusValues.Running);
    }

    [Test]
    public async Task Status_ReturnsCompletedWithResult_WhenTerminalStateCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        var response = MakeResponse(jobId);
        await _stateCache.StoreCompletedAsync(jobId, response);

        var actionResult = await _sut.Status(jobId, CancellationToken.None);
        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();

        var body = ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>();
        body.Status.ShouldBe(WizardJobStatusValues.Completed);
        // Compared field by field, not as a whole object: since the terminal state travels through the
        // database, the store deliberately hands back a structurally equal COPY rather than the stored
        // instance. Instance equality was never the promise - it was a side effect of the in-process
        // dictionary, and that same side effect is why a status poll landing on a second API instance
        // used to come up empty. The promise is the same content, and the wire output is unchanged.
        body.Result.ShouldNotBeNull();
        body.Result.JobId.ShouldBe(response.JobId);
        body.Result.LlmModelId.ShouldBe(response.LlmModelId);
        body.Result.FitnessBefore.ShouldBe(response.FitnessBefore);
        body.Result.FitnessAfter.ShouldBe(response.FitnessAfter);
        body.Reason.ShouldBeNull();
    }

    [Test]
    public async Task Status_ReturnsFailedWithReason_WhenFailureCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        await _stateCache.StoreFailedAsync(jobId, "Model produced no usable response.");

        var actionResult = await _sut.Status(jobId, CancellationToken.None);
        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();

        var body = ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>();
        body.Status.ShouldBe(WizardJobStatusValues.Failed);
        body.Reason.ShouldBe("Model produced no usable response.");
        body.Result.ShouldBeNull();
    }

    [Test]
    public async Task Status_ReturnsCancelled_WhenCancelCached()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);
        await _stateCache.StoreCancelledAsync(jobId);

        var actionResult = await _sut.Status(jobId, CancellationToken.None);
        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();

        ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>()
            .Status.ShouldBe(WizardJobStatusValues.Cancelled);
    }

    [Test]
    public async Task Status_ReturnsUnknown_WhenJobNotTracked()
    {
        var jobId = Guid.NewGuid();
        _runner.IsRunning(jobId).Returns(false);

        var actionResult = await _sut.Status(jobId, CancellationToken.None);
        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();

        ok.Value.ShouldBeOfType<HolisticHarmonizerJobStatusResponse>()
            .Status.ShouldBe(WizardJobStatusValues.Unknown);
    }

    [Test]
    public async Task ApplyAsScenario_ForwardsLanguageToApplyService()
    {
        var jobId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        _applyService
            .ApplyAsScenarioAsync(jobId, groupId, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), "it", Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((new AnalyseScenarioResource { Name = "Piano IA" }, (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));

        var result = await _sut.ApplyAsScenario(new ApplyHolisticHarmonizerAsScenarioRequest(jobId, groupId, "it"), CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _applyService.Received(1)
            .ApplyAsScenarioAsync(jobId, groupId, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), "it", Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ApplyAsScenario_WithoutLanguage_PassesNullSoTheInstallationLanguageApplies()
    {
        var jobId = Guid.NewGuid();
        _applyService
            .ApplyAsScenarioAsync(jobId, null, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((new AnalyseScenarioResource { Name = "KI-Plan" }, (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));

        await _sut.ApplyAsScenario(new ApplyHolisticHarmonizerAsScenarioRequest(jobId, null), CancellationToken.None);

        await _applyService.Received(1)
            .ApplyAsScenarioAsync(jobId, null, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), null, Arg.Any<bool>(), Arg.Any<bool>());
    }
}
