// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WizardRunCaptureRepositoryTests
{
    private DataBaseContext _context = null!;
    private WizardRunCaptureRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new WizardRunCaptureRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private static WizardRunCapture MakeCapture(Guid? scenarioId = null, WizardApplyKind kind = WizardApplyKind.Direct)
    {
        return new WizardRunCapture
        {
            Id = Guid.NewGuid(),
            Engine = WizardEngine.TokenEvolution,
            JobId = Guid.NewGuid(),
            ScenarioId = scenarioId,
            ApplyKind = kind,
            PeriodFrom = new DateOnly(2026, 4, 1),
            PeriodUntil = new DateOnly(2026, 4, 30),
            SubScoreJson = "{\"engine\":\"tokenEvolution\"}",
            Stage0Violations = 0,
        };
    }

    [Test]
    public async Task AddAsync_PersistsCaptureAndChildWorks()
    {
        var capture = MakeCapture();
        var workIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        await _sut.AddAsync(capture, workIds);

        var stored = await _context.WizardRunCapture.Include(c => c.Works).SingleAsync();
        stored.Engine.ShouldBe(WizardEngine.TokenEvolution);
        stored.Works.Count.ShouldBe(3);
        stored.Works.Select(w => w.WorkId).ShouldBe(workIds, ignoreOrder: true);
        stored.Works.ShouldAllBe(w => w.CaptureId == capture.Id);
    }

    [Test]
    public async Task AddAsync_WithNoWorkIds_PersistsCaptureWithoutChildren()
    {
        var capture = MakeCapture();

        await _sut.AddAsync(capture, Array.Empty<Guid>());

        (await _context.WizardRunCaptureWork.CountAsync()).ShouldBe(0);
        (await _context.WizardRunCapture.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task GetByScenarioIdAsync_ReturnsMatchingCapture()
    {
        var scenarioId = Guid.NewGuid();
        await _sut.AddAsync(MakeCapture(scenarioId, WizardApplyKind.Scenario), new[] { Guid.NewGuid() });
        await _sut.AddAsync(MakeCapture(Guid.NewGuid(), WizardApplyKind.Scenario), new[] { Guid.NewGuid() });

        var found = await _sut.GetByScenarioIdAsync(scenarioId);

        found.ShouldNotBeNull();
        found!.ScenarioId.ShouldBe(scenarioId);
    }

    [Test]
    public async Task GetByScenarioIdAsync_ReturnsNull_WhenNoMatch()
    {
        (await _sut.GetByScenarioIdAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Test]
    public async Task SetOutcomeAsync_UpdatesOutcome()
    {
        var capture = MakeCapture();
        await _sut.AddAsync(capture, new[] { Guid.NewGuid() });

        await _sut.SetOutcomeAsync(capture.Id, CaptureOutcome.Rejected);

        var stored = await _context.WizardRunCapture.SingleAsync();
        stored.Outcome.ShouldBe(CaptureOutcome.Rejected);
    }

    [Test]
    public async Task SetOutcomeAsync_IsNoOp_WhenCaptureMissing()
    {
        await Should.NotThrowAsync(() => _sut.SetOutcomeAsync(Guid.NewGuid(), CaptureOutcome.Rejected));
    }

    [Test]
    public async Task SupersedeOpenDirectCapturesAsync_StampsOnlyOpenOverlappingDirectCapturesOfSameEngine()
    {
        var older = MakeCapture();
        var newer = MakeCapture();
        var scenarioCapture = MakeCapture(scenarioId: Guid.NewGuid(), kind: WizardApplyKind.Scenario);
        var otherEngine = MakeCapture();
        otherEngine.Engine = WizardEngine.Harmonizer;
        var alreadyResolved = MakeCapture();
        alreadyResolved.Outcome = CaptureOutcome.Rejected;
        var nonOverlapping = MakeCapture();
        nonOverlapping.PeriodFrom = new DateOnly(2026, 6, 1);
        nonOverlapping.PeriodUntil = new DateOnly(2026, 6, 30);

        foreach (var capture in new[] { older, newer, scenarioCapture, otherEngine, alreadyResolved, nonOverlapping })
        {
            await _sut.AddAsync(capture, new[] { Guid.NewGuid() });
        }

        var count = await _sut.SupersedeOpenDirectCapturesAsync(
            newer.Id, WizardEngine.TokenEvolution, newer.PeriodFrom, newer.PeriodUntil);

        count.ShouldBe(1);
        (await _context.WizardRunCapture.SingleAsync(c => c.Id == older.Id)).Outcome
            .ShouldBe(CaptureOutcome.Superseded);
        (await _context.WizardRunCapture.SingleAsync(c => c.Id == newer.Id)).Outcome.ShouldBeNull();
        (await _context.WizardRunCapture.SingleAsync(c => c.Id == scenarioCapture.Id)).Outcome.ShouldBeNull();
        (await _context.WizardRunCapture.SingleAsync(c => c.Id == otherEngine.Id)).Outcome.ShouldBeNull();
        (await _context.WizardRunCapture.SingleAsync(c => c.Id == alreadyResolved.Id)).Outcome
            .ShouldBe(CaptureOutcome.Rejected);
        (await _context.WizardRunCapture.SingleAsync(c => c.Id == nonOverlapping.Id)).Outcome.ShouldBeNull();
    }

    [Test]
    public async Task SupersedeOpenDirectCapturesAsync_SkipsAlreadyMeasuredCaptures()
    {
        var measured = MakeCapture();
        measured.MeasuredAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await _sut.AddAsync(measured, new[] { Guid.NewGuid() });

        var count = await _sut.SupersedeOpenDirectCapturesAsync(
            Guid.NewGuid(), WizardEngine.TokenEvolution, measured.PeriodFrom, measured.PeriodUntil);

        count.ShouldBe(0);
        (await _context.WizardRunCapture.SingleAsync()).Outcome.ShouldBeNull();
    }

    [Test]
    public async Task GetScenarioStateAsync_ReturnsStateOfSoftDeletedScenario()
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "soft deleted",
            Status = AnalyseScenarioStatus.Active,
            IsDeleted = true,
        };
        _context.AnalyseScenarios.Add(scenario);
        await _context.SaveChangesAsync();

        var state = await _sut.GetScenarioStateAsync(scenario.Id);

        state.ShouldNotBeNull();
        state!.IsDeleted.ShouldBeTrue();
        state.Status.ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task GetScenarioStateAsync_ReturnsNull_WhenRowIsGone()
    {
        (await _sut.GetScenarioStateAsync(Guid.NewGuid())).ShouldBeNull();
    }
}
