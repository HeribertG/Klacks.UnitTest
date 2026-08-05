// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardRunCaptureMeasurementServiceTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid Shift = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 4, 10);

    private IWizardRunCaptureRepository _repository = null!;
    private WizardRunCaptureMeasurementService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IWizardRunCaptureRepository>();
        _sut = new WizardRunCaptureMeasurementService(
            _repository, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
    }

    private static WizardRunCapture Capture(DateTime? measuredAt = null, CaptureOutcome? outcome = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Engine = WizardEngine.TokenEvolution,
            ApplyKind = WizardApplyKind.Direct,
            PeriodFrom = new DateOnly(2026, 4, 1),
            PeriodUntil = new DateOnly(2026, 4, 30),
            CreateTime = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
            MeasuredAt = measuredAt,
            Outcome = outcome,
        };

    private static WizardRunProposalCell Cell(bool isDeleted)
        => new(AgentA, Day, Shift, new TimeOnly(8, 0), new TimeOnly(16, 0), isDeleted);

    [Test]
    public async Task MeasureAsync_ComputesAndPersistsChurnWithOutcome()
    {
        var capture = Capture();
        var data = new WizardRunMeasurementData(
            new[] { Cell(isDeleted: true) },
            new HashSet<(Guid, DateOnly)>());
        _repository.LoadMeasurementDataAsync(capture, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(data);

        await _sut.MeasureAsync(capture, CaptureOutcome.Accepted);

        await _repository.Received(1).SetMeasurementAsync(
            capture.Id, 1.0, 0.0, Arg.Any<DateTime>(), CaptureOutcome.Accepted, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MeasureAsync_EventExplainedChange_PersistsEventChurnNotCorrection()
    {
        var capture = Capture();
        var data = new WizardRunMeasurementData(
            new[] { Cell(isDeleted: true) },
            new HashSet<(Guid, DateOnly)> { (AgentA, Day) });
        _repository.LoadMeasurementDataAsync(capture, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(data);

        await _sut.MeasureAsync(capture, CaptureOutcome.Expired);

        await _repository.Received(1).SetMeasurementAsync(
            capture.Id, 0.0, 1.0, Arg.Any<DateTime>(), CaptureOutcome.Expired, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MeasureAsync_AlreadyMeasured_IsNoOp()
    {
        var capture = Capture(measuredAt: DateTime.UtcNow);

        await _sut.MeasureAsync(capture, CaptureOutcome.Accepted);

        await _repository.DidNotReceiveWithAnyArgs().LoadMeasurementDataAsync(default!, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().SetMeasurementAsync(
            default, default, default, default, default, default);
    }

    [Test]
    public async Task MeasureAsync_RejectedCapture_IsNoOp()
    {
        var capture = Capture(outcome: CaptureOutcome.Rejected);

        await _sut.MeasureAsync(capture, CaptureOutcome.Accepted);

        await _repository.DidNotReceiveWithAnyArgs().SetMeasurementAsync(
            default, default, default, default, default, default);
    }

    [Test]
    public async Task MeasureAsync_CaptureWithAnyOutcome_IsNoOp()
    {
        var capture = Capture(outcome: CaptureOutcome.Superseded);

        await _sut.MeasureAsync(capture, CaptureOutcome.Accepted);

        await _repository.DidNotReceiveWithAnyArgs().SetMeasurementAsync(
            default, default, default, default, default, default);
    }

    private WizardRunCapture ScenarioCapture(Guid scenarioId)
    {
        var capture = Capture();
        capture.ApplyKind = WizardApplyKind.Scenario;
        capture.ScenarioId = scenarioId;
        _repository.LoadMeasurementDataAsync(capture, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardRunMeasurementData(
                new[] { Cell(isDeleted: false) },
                new HashSet<(Guid, DateOnly)>()));
        return capture;
    }

    private void ScenarioState(Guid scenarioId, AnalyseScenarioStatus status, bool isDeleted = false)
        => _repository.GetScenarioStateAsync(scenarioId, Arg.Any<CancellationToken>())
            .Returns(new CaptureScenarioState(status, isDeleted));

    [Test]
    public async Task MeasureResolvedAsync_DirectApplyOnSeal_MeasuresAsAccepted()
    {
        var capture = Capture();
        _repository.LoadMeasurementDataAsync(capture, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardRunMeasurementData(
                new[] { Cell(isDeleted: false) },
                new HashSet<(Guid, DateOnly)>()));

        await _sut.MeasureResolvedAsync(capture, periodSealed: true);

        await _repository.Received(1).SetMeasurementAsync(
            capture.Id, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<DateTime>(),
            CaptureOutcome.Accepted, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MeasureResolvedAsync_DirectApplyOnFallback_MeasuresAsExpired()
    {
        var capture = Capture();
        _repository.LoadMeasurementDataAsync(capture, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WizardRunMeasurementData(
                new[] { Cell(isDeleted: false) },
                new HashSet<(Guid, DateOnly)>()));

        await _sut.MeasureResolvedAsync(capture, periodSealed: false);

        await _repository.Received(1).SetMeasurementAsync(
            capture.Id, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<DateTime>(),
            CaptureOutcome.Expired, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MeasureResolvedAsync_PromotedScenarioOnSeal_MeasuresAsAccepted()
    {
        var scenarioId = Guid.NewGuid();
        var capture = ScenarioCapture(scenarioId);
        ScenarioState(scenarioId, AnalyseScenarioStatus.Accepted);

        await _sut.MeasureResolvedAsync(capture, periodSealed: true);

        await _repository.Received(1).SetMeasurementAsync(
            capture.Id, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<DateTime>(),
            CaptureOutcome.Accepted, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MeasureResolvedAsync_UndecidedScenarioOnSeal_StampsSupersededWithoutMeasuring()
    {
        var scenarioId = Guid.NewGuid();
        var capture = ScenarioCapture(scenarioId);
        ScenarioState(scenarioId, AnalyseScenarioStatus.Active);

        await _sut.MeasureResolvedAsync(capture, periodSealed: true);

        await _repository.Received(1).SetOutcomeAsync(
            capture.Id, CaptureOutcome.Superseded, Arg.Any<CancellationToken>());
        await _repository.DidNotReceiveWithAnyArgs().LoadMeasurementDataAsync(default!, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().SetMeasurementAsync(
            default, default, default, default, default, default);
    }

    [Test]
    public async Task MeasureResolvedAsync_UndecidedScenarioOnFallback_StampsExpired()
    {
        var scenarioId = Guid.NewGuid();
        var capture = ScenarioCapture(scenarioId);
        ScenarioState(scenarioId, AnalyseScenarioStatus.Active);

        await _sut.MeasureResolvedAsync(capture, periodSealed: false);

        await _repository.Received(1).SetOutcomeAsync(
            capture.Id, CaptureOutcome.Expired, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MeasureResolvedAsync_DeletedScenario_StampsRejectedWithoutMeasuring()
    {
        var scenarioId = Guid.NewGuid();
        var capture = ScenarioCapture(scenarioId);
        ScenarioState(scenarioId, AnalyseScenarioStatus.Active, isDeleted: true);

        await _sut.MeasureResolvedAsync(capture, periodSealed: true);

        await _repository.Received(1).SetOutcomeAsync(
            capture.Id, CaptureOutcome.Rejected, Arg.Any<CancellationToken>());
        await _repository.DidNotReceiveWithAnyArgs().LoadMeasurementDataAsync(default!, default!, default);
    }

    [Test]
    public async Task MeasureResolvedAsync_RejectedScenario_StampsRejectedWithoutMeasuring()
    {
        var scenarioId = Guid.NewGuid();
        var capture = ScenarioCapture(scenarioId);
        ScenarioState(scenarioId, AnalyseScenarioStatus.Rejected);

        await _sut.MeasureResolvedAsync(capture, periodSealed: true);

        await _repository.Received(1).SetOutcomeAsync(
            capture.Id, CaptureOutcome.Rejected, Arg.Any<CancellationToken>());
        await _repository.DidNotReceiveWithAnyArgs().SetMeasurementAsync(
            default, default, default, default, default, default);
    }

    [Test]
    public async Task MeasureResolvedAsync_MissingScenarioRow_StampsRejected()
    {
        var scenarioId = Guid.NewGuid();
        var capture = ScenarioCapture(scenarioId);
        _repository.GetScenarioStateAsync(scenarioId, Arg.Any<CancellationToken>())
            .Returns((CaptureScenarioState?)null);

        await _sut.MeasureResolvedAsync(capture, periodSealed: true);

        await _repository.Received(1).SetOutcomeAsync(
            capture.Id, CaptureOutcome.Rejected, Arg.Any<CancellationToken>());
    }
}
