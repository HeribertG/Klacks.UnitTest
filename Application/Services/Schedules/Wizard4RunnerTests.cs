// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
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
    private static readonly DateOnly D = new(2026, 4, 20);

    private IHarmonizerApplyService _applyService = null!;
    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private HarmonizerResultCache _resultCache = null!;
    private Wizard4Runner _runner = null!;

    [SetUp]
    public void Setup()
    {
        _applyService = Substitute.For<IHarmonizerApplyService>();
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _resultCache = new HarmonizerResultCache();

        _runner = new Wizard4Runner(
            Substitute.For<IHarmonizerContextBuilder>(),
            Substitute.For<IWizardContextBuilder>(),
            Substitute.For<IWizard4OptimizationCore>(),
            _resultCache,
            _applyService,
            _repository,
            _unitOfWork,
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
    public async Task Materialize_CreatesAndCapturesCandidate_WhenImprovementExceedsThreshold()
    {
        var scenarioId = Guid.NewGuid();
        var resource = new AnalyseScenarioResource { Id = scenarioId, Name = "Optimizer" };
        _applyService
            .ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns((resource, (IReadOnlyList<Guid>)Array.Empty<Guid>()));
        var scenario = new AnalyseScenario { Id = scenarioId, Token = Guid.NewGuid() };
        _repository.Get(scenarioId).Returns(scenario);

        var result = Result(baselineScalar: 0.50, bestFitness: 0.90);
        var seed = Bitmap(CellSymbol.Early);

        var created = await _runner.MaterializeCandidateIfImprovedAsync(result, seed, Guid.NewGuid(), CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Id.ShouldBe(scenarioId);
        await _applyService.Received(1).ApplyAsScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), "Optimizer");
        scenario.SubScoreJson.ShouldNotBeNull();
        scenario.ChurnRatio.ShouldNotBeNull();
        scenario.CreatedByUser.ShouldBe("wizard4");
        await _repository.Received(1).Put(scenario);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Materialize_CreatesNoCandidate_WhenImprovementIsBelowThreshold()
    {
        var result = Result(baselineScalar: 0.50, bestFitness: 0.50);
        var seed = Bitmap(CellSymbol.Early);

        var created = await _runner.MaterializeCandidateIfImprovedAsync(result, seed, Guid.NewGuid(), CancellationToken.None);

        created.ShouldBeNull();
        await _applyService.DidNotReceive().ApplyAsScenarioAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }
}
