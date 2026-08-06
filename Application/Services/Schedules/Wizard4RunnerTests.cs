// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Objective;
using Klacks.ScheduleOptimizer.Wizard4;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class Wizard4RunnerTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly Until = new(2026, 11, 30);

    private static readonly DateOnly D = new(2026, 4, 20);

    private IHarmonizerApplyService _applyService = null!;
    private IAnalyseScenarioRepository _repository = null!;
    private IWizardRunCaptureRepository _captureRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private HarmonizerResultCache _resultCache = null!;
    private IWizard4CandidateLifecycleService _lifecycleService = null!;
    private IWorkNotificationService _notificationService = null!;
    private Wizard4Runner _runner = null!;

    [SetUp]
    public void Setup()
    {
        _applyService = Substitute.For<IHarmonizerApplyService>();
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _captureRepository = Substitute.For<IWizardRunCaptureRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _resultCache = new HarmonizerResultCache();

        _lifecycleService = Substitute.For<IWizard4CandidateLifecycleService>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _runner = new Wizard4Runner(
            Substitute.For<IHarmonizerContextBuilder>(),
            Substitute.For<IWizardContextBuilder>(),
            Substitute.For<IWizard4OptimizationCore>(),
            _resultCache,
            _applyService,
            _repository,
            _captureRepository,
            _unitOfWork,
            Substitute.For<IScheduleSnapshotMarkerService>(),
            Substitute.For<IWizard4SnapshotGuard>(),
            _lifecycleService,
            _notificationService,
            Substitute.For<ILogger<Wizard4Runner>>());
    }

    private static HarmonyBitmap Bitmap(CellSymbol symbol)
    {
        var cells = new Cell[1, 1];
        cells[0, 0] = symbol == CellSymbol.Free
            ? Cell.Free()
            : new Cell(symbol, Guid.NewGuid(), [], false, D.ToDateTime(new TimeOnly(8, 0)), D.ToDateTime(new TimeOnly(16, 0)), 8m);
        return new HarmonyBitmap([new BitmapAgent("A", "A", 8m, new HashSet<CellSymbol>())], [D], cells);
    }

    private static Wizard4OptimizationResult Result(double baselineScalar, double bestFitness)
    {
        var gate = new GateResult(0, 0, 0, 0);
        var sub = new ObjectiveSubScores(0.9, 0.9, 1.0);
        var diag = new ObjectiveDiagnostics(0.8, 1.0, 0.0);
        var objResult = new ObjectiveResult(gate, bestFitness, sub, diag);
        return new Wizard4OptimizationResult(Bitmap(CellSymbol.Early), objResult, objResult, baselineScalar, bestFitness);
    }

    [Test]
    public async Task Materialize_ExistingCandidateForTheSameSelection_IsSuperseded()
    {
        // Nobody asked for either candidate. A stack of near-identical suggestions for the same period
        // is worse than one, so the older has to go when the newer lands.
        var scenarioId = Guid.NewGuid();
        var previous = new AnalyseScenario { Id = Guid.NewGuid(), Token = Guid.NewGuid() };
        var groupId = Guid.NewGuid();
        _repository
            .GetActiveCandidateAsync(Arg.Any<string>(), groupId, From, Until, Arg.Any<CancellationToken>())
            .Returns(previous);
        StubApply(scenarioId);

        await _runner.MaterializeCandidateIfImprovedAsync(
            Result(baselineScalar: 0.50, bestFitness: 0.90),
            Bitmap(CellSymbol.Early), groupId, CancellationToken.None, snapshotMarker: null, From, Until);

        await _lifecycleService.Received(1).SupersedeAsync(previous, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Materialize_NoPreviousCandidate_SupersedesNothing()
    {
        _repository
            .GetActiveCandidateAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((AnalyseScenario?)null);
        StubApply(Guid.NewGuid());

        await _runner.MaterializeCandidateIfImprovedAsync(
            Result(baselineScalar: 0.50, bestFitness: 0.90),
            Bitmap(CellSymbol.Early), Guid.NewGuid(), CancellationToken.None, snapshotMarker: null, From, Until);

        await _lifecycleService.DidNotReceiveWithAnyArgs().SupersedeAsync(default!, default);
    }

    [Test]
    public async Task Materialize_PushesTheCreatedCandidateWithTheRealPeriod()
    {
        var groupId = Guid.NewGuid();
        var scenarioId = Guid.NewGuid();
        StubApply(scenarioId);

        await _runner.MaterializeCandidateIfImprovedAsync(
            Result(baselineScalar: 0.50, bestFitness: 0.90),
            Bitmap(CellSymbol.Early), groupId, CancellationToken.None, snapshotMarker: null, From, Until);

        await _notificationService.Received(1).NotifyWizard4CandidatesChanged(
            Arg.Is<Wizard4CandidateNotificationDto>(n =>
                n.ScenarioId == scenarioId
                && n.GroupId == groupId
                && n.FromDate == From
                && n.UntilDate == Until
                && n.ChangeKind == Wizard4LifecycleConstants.ChangeKindCreated));
    }

    [Test]
    public async Task Materialize_NoImprovement_NeitherSupersedesNorPushes()
    {
        await _runner.MaterializeCandidateIfImprovedAsync(
            Result(baselineScalar: 0.90, bestFitness: 0.90),
            Bitmap(CellSymbol.Early), Guid.NewGuid(), CancellationToken.None, snapshotMarker: null, From, Until);

        await _lifecycleService.DidNotReceiveWithAnyArgs().SupersedeAsync(default!, default);
        await _notificationService.DidNotReceiveWithAnyArgs().NotifyWizard4CandidatesChanged(default!);
    }

    private void StubApply(Guid scenarioId)
    {
        var resource = new AnalyseScenarioResource { Id = scenarioId, Name = "Optimizer" };
        _applyService
            .ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((resource, (IReadOnlyList<Guid>)Array.Empty<Guid>(), (ScenarioComplianceReport?)null));
        _repository.Get(scenarioId).Returns(new AnalyseScenario { Id = scenarioId, Token = Guid.NewGuid() });
    }
    [Test]
    public async Task Materialize_CreatesAndCapturesCandidate_WhenImprovementExceedsThreshold()
    {
        var scenarioId = Guid.NewGuid();
        var resource = new AnalyseScenarioResource { Id = scenarioId, Name = "Optimizer" };
        _applyService
            .ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((resource, (IReadOnlyList<Guid>)Array.Empty<Guid>(), (ScenarioComplianceReport?)null));
        var scenario = new AnalyseScenario { Id = scenarioId, Token = Guid.NewGuid() };
        _repository.Get(scenarioId).Returns(scenario);

        var result = Result(baselineScalar: 0.50, bestFitness: 0.90);
        var seed = Bitmap(CellSymbol.Early);

        var created = await _runner.MaterializeCandidateIfImprovedAsync(
            result, seed, Guid.NewGuid(), CancellationToken.None, snapshotMarker: null, From, Until);

        created.ShouldNotBeNull();
        created!.Id.ShouldBe(scenarioId);
        await _applyService.Received(1).ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), "Optimizer", false, false);
        scenario.SubScoreJson.ShouldNotBeNull();
        scenario.ChurnRatio.ShouldNotBeNull();
        scenario.CreatedByUser.ShouldBe("wizard4");
        await _repository.Received(1).Put(scenario);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Materialize_WritesWizard4Capture_WithCreatedWorkIds()
    {
        var scenarioId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var runGroupId = Guid.NewGuid();
        var workIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var resource = new AnalyseScenarioResource
        {
            Id = scenarioId,
            Name = "Optimizer",
            GroupId = groupId,
            FromDate = new DateOnly(2026, 4, 1),
            UntilDate = new DateOnly(2026, 4, 30),
        };
        _applyService
            .ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((resource, (IReadOnlyList<Guid>)workIds, (ScenarioComplianceReport?)null));
        var scenario = new AnalyseScenario { Id = scenarioId, Token = Guid.NewGuid(), RunGroupId = runGroupId };
        _repository.Get(scenarioId).Returns(scenario);

        WizardRunCapture? captured = null;
        IReadOnlyList<Guid>? capturedWorkIds = null;
        await _captureRepository.AddAsync(Arg.Do<WizardRunCapture>(c => captured = c),
            Arg.Do<IReadOnlyList<Guid>>(ids => capturedWorkIds = ids),
            Arg.Any<CancellationToken>());

        var result = Result(baselineScalar: 0.50, bestFitness: 0.90);
        await _runner.MaterializeCandidateIfImprovedAsync(
            result, Bitmap(CellSymbol.Early), groupId, CancellationToken.None, snapshotMarker: null, From, Until);

        captured.ShouldNotBeNull();
        captured!.Engine.ShouldBe(WizardEngine.Wizard4);
        captured.ApplyKind.ShouldBe(WizardApplyKind.Scenario);
        captured.ScenarioId.ShouldBe(scenarioId);
        captured.GroupId.ShouldBe(groupId);
        captured.RunGroupId.ShouldBe(runGroupId);
        captured.ChurnAtApply.ShouldNotBeNull();
        captured.SubScoreJson.ShouldNotBeNullOrEmpty();
        capturedWorkIds.ShouldBe(workIds);
    }

    [Test]
    public async Task Materialize_CaptureFailureDoesNotBreakRun()
    {
        var scenarioId = Guid.NewGuid();
        var resource = new AnalyseScenarioResource { Id = scenarioId, Name = "Optimizer" };
        _applyService
            .ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((resource, (IReadOnlyList<Guid>)new List<Guid> { Guid.NewGuid() }, (ScenarioComplianceReport?)null));
        _repository.Get(scenarioId).Returns(new AnalyseScenario { Id = scenarioId, Token = Guid.NewGuid() });
        _captureRepository
            .AddAsync(Arg.Any<WizardRunCapture>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        var result = Result(baselineScalar: 0.50, bestFitness: 0.90);

        var created = await _runner.MaterializeCandidateIfImprovedAsync(
            result, Bitmap(CellSymbol.Early), Guid.NewGuid(), CancellationToken.None, snapshotMarker: null, From, Until);

        created.ShouldNotBeNull();
        created!.Id.ShouldBe(scenarioId);
    }

    [Test]
    public async Task Materialize_CreatesNoCandidate_WhenImprovementIsBelowThreshold()
    {
        var result = Result(baselineScalar: 0.50, bestFitness: 0.50);
        var seed = Bitmap(CellSymbol.Early);

        var created = await _runner.MaterializeCandidateIfImprovedAsync(
            result, seed, Guid.NewGuid(), CancellationToken.None, snapshotMarker: null, From, Until);

        created.ShouldBeNull();
        await _applyService.DidNotReceive().ApplyAsScenarioAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>());
    }
}
