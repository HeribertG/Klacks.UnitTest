// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drives the real AutoWizardJobRunner through a whole chain with substituted stage runners and apply services,
/// real registries and result caches, and InMemory-backed terminal state caches, to pin how the third stage
/// (Holistic Harmonizer) ends the chain: skipped before it starts when readiness says "not ready"; any failure of
/// the stage falls back to the Harmonizer scenario (stage 3 only polishes), naming a missing prerequisite when the
/// re-check finds one and the failure otherwise; the outcome note lands on the kept Harmonizer scenario while only
/// the intermediates are deleted; and in the default deterministic mode (no WIZARD3_MODE, no model) stage 3 runs.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.DTOs.Schedules.Wizard;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Schedules.AutoWizard;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.Api.Domain.Interfaces.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public class AutoWizardJobRunnerHolisticStageTests
{
    private const string RequestLanguage = "de";
    private const string TextOnlyReason = "The configured model 'text-model' cannot read the schedule image.";
    private static readonly TimeSpan ChainTimeout = TimeSpan.FromSeconds(20);
    private static readonly DateOnly PeriodFrom = new(2026, 11, 2);
    private static readonly DateOnly PeriodUntil = new(2026, 11, 8);

    private readonly Guid _wizardStageJobId = Guid.NewGuid();
    private readonly Guid _harmonizerStageJobId = Guid.NewGuid();
    private readonly Guid _holisticStageJobId = Guid.NewGuid();
    private readonly Guid _wizardScenarioId = Guid.NewGuid();
    private readonly Guid _harmonizerScenarioId = Guid.NewGuid();
    private readonly Guid _holisticScenarioId = Guid.NewGuid();

    private IAutoWizardHubNotifier _hubNotifier = null!;
    private IWizardJobRunner _wizardRunner = null!;
    private IHarmonizerJobRunner _harmonizerRunner = null!;
    private IHolisticHarmonizerJobRunner _holisticRunner = null!;
    private IWizardApplyService _wizardApply = null!;
    private IHarmonizerApplyService _harmonizerApply = null!;
    private IHolisticHarmonizerApplyService _holisticApply = null!;
    private IHolisticHarmonizerReadinessCheck _readiness = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IMediator _mediator = null!;
    private JobTerminalStateCache<AutoWizardJobResultDto> _stateCache = null!;
    private Dictionary<Guid, AnalyseScenario> _scenarios = null!;
    private ServiceProvider? _provider;
    private IHarmonizerContextBuilder _contextBuilder = null!;
    private IEligibilityMatrixBuilder _matrixBuilder = null!;
    private IPlanningRuleSetLoader _ruleSetLoader = null!;
    private WizardResultCache _wizardResults = null!;
    private HarmonizerResultCache _harmonizerResults = null!;
    private AutoWizardJobRunner _runner = null!;

    [SetUp]
    public void SetUp()
    {
        _hubNotifier = Substitute.For<IAutoWizardHubNotifier>();

        _wizardRunner = Substitute.For<IWizardJobRunner>();
        _wizardRunner.StartAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>()).Returns(_wizardStageJobId);
        _harmonizerRunner = Substitute.For<IHarmonizerJobRunner>();
        _harmonizerRunner.StartAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>()).Returns(_harmonizerStageJobId);
        _holisticRunner = Substitute.For<IHolisticHarmonizerJobRunner>();
        _holisticRunner.StartAsync(Arg.Any<HolisticHarmonizerRunInput>(), Arg.Any<CancellationToken>()).Returns(_holisticStageJobId);

        var wizardResults = new WizardResultCache();
        wizardResults.Store(_wizardStageJobId, new CoreScenario(), analyseToken: null);
        var harmonizerResults = new HarmonizerResultCache();
        var emptyBitmap = new HarmonyBitmap([], [], new Cell[0, 0]);
        harmonizerResults.Store(_harmonizerStageJobId, emptyBitmap, emptyBitmap, sourceAnalyseToken: null);

        _scenarios = new Dictionary<Guid, AnalyseScenario>
        {
            [_wizardScenarioId] = new() { Id = _wizardScenarioId, Name = "AutoWizard-Wizard" },
            [_harmonizerScenarioId] = new() { Id = _harmonizerScenarioId, Name = "AutoWizard-Harmonizer", Description = "harmonized" },
            [_holisticScenarioId] = new() { Id = _holisticScenarioId, Name = "AutoWizard-Holistic" }
        };

        _wizardApply = Substitute.For<IWizardApplyService>();
        _wizardApply.ApplyAsScenarioAsync(
                _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>())
            .Returns((Resource(_wizardScenarioId), new WizardApplyOutcome([], [], [], false)));
        _harmonizerApply = Substitute.For<IHarmonizerApplyService>();
        _harmonizerApply.ApplyAsScenarioAsync(
                _harmonizerStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((Resource(_harmonizerScenarioId), (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));
        _holisticApply = Substitute.For<IHolisticHarmonizerApplyService>();
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((Resource(_holisticScenarioId), (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));

        _readiness = Substitute.For<IHolisticHarmonizerReadinessCheck>();
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(HolisticHarmonizerReadiness.Ready());

        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepository.Get(Arg.Any<Guid>()).Returns(call => _scenarios.GetValueOrDefault(call.Arg<Guid>()));
        _mediator = Substitute.For<IMediator>();

        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BitmapInput([], PeriodFrom, PeriodUntil, []));
        var matrixBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        matrixBuilder.BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(), Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);

        _ruleSetLoader = Substitute.For<IPlanningRuleSetLoader>();
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], []));

        _contextBuilder = contextBuilder;
        _matrixBuilder = matrixBuilder;
        _wizardResults = wizardResults;
        _harmonizerResults = harmonizerResults;
        RebuildRunner(_readiness);
    }

    private void RebuildRunner(IHolisticHarmonizerReadinessCheck readiness)
    {
        _provider?.Dispose();
        var services = new ServiceCollection();
        services.AddSingleton(_wizardApply);
        services.AddSingleton(_harmonizerApply);
        services.AddSingleton(_holisticApply);
        services.AddSingleton(readiness);
        services.AddSingleton(_scenarioRepository);
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(_mediator);
        services.AddSingleton(_contextBuilder);
        services.AddSingleton(_matrixBuilder);
        services.AddSingleton(_ruleSetLoader);
        _provider = services.BuildServiceProvider();

        _stateCache = JobTerminalStateCacheTestFactory.Create<AutoWizardJobResultDto>();

        _runner = new AutoWizardJobRunner(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _hubNotifier,
            new AutoWizardJobRegistry(),
            new AutofillStartGuard(),
            _wizardRunner,
            new WizardJobRegistry(),
            _wizardResults,
            _harmonizerRunner,
            new HarmonizerJobRegistry(),
            _harmonizerResults,
            _holisticRunner,
            new HolisticHarmonizerJobRegistry(),
            JobTerminalStateCacheTestFactory.Create<HolisticHarmonizerRunResponse>(),
            _stateCache,
            Substitute.For<IHostApplicationLifetime>(),
            NullLogger<AutoWizardJobRunner>.Instance);
    }

    [TearDown]
    public void TearDown() => _provider?.Dispose();

    private AnalyseScenarioResource Resource(Guid scenarioId) => new()
    {
        Id = scenarioId,
        Name = _scenarios?.GetValueOrDefault(scenarioId)?.Name ?? string.Empty,
        Token = Guid.NewGuid()
    };

    private async Task<(Guid JobId, JobTerminalState<AutoWizardJobResultDto> State)> RunChainAsync()
    {
        var request = new StartAutoWizardRequest(
            PeriodFrom, PeriodUntil, [Guid.NewGuid()], [Guid.NewGuid()], Guid.NewGuid(), null, "de");
        var jobId = await _runner.StartAsync(request, CancellationToken.None);

        var deadline = DateTime.UtcNow + ChainTimeout;
        while (_runner.IsRunning(jobId))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The AutoWizard chain did not finish in time.");
            }

            await Task.Delay(50);
        }

        return (jobId, await _stateCache.TryGetAsync(jobId));
    }

    private IReadOnlyList<Guid> DeletedScenarioIds() =>
        _mediator.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<DeleteAnalyseScenarioCommand>()
            .Select(command => command.ScenarioId)
            .ToList();

    [Test]
    public async Task NotReadyBeforeStageThree_SkipsIt_AndCompletesWithTheHarmonizerScenario()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(HolisticHarmonizerReadiness.NotReady(TextOnlyReason));

        var (jobId, state) = await RunChainAsync();

        await _holisticRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_harmonizerScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldBe(TextOnlyReason);
        await _hubNotifier.Received(1).NotifyCompletedAsync(jobId, Arg.Is<AutoWizardJobResultDto>(d => d.HarmonizationSkipped));
        await _hubNotifier.DidNotReceiveWithAnyArgs().NotifyFailedAsync(default!);
    }

    [Test]
    public async Task StageThreeFails_AndReadinessNowSaysTextOnly_IsSkippedNotFailed()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(HolisticHarmonizerReadiness.Ready(), HolisticHarmonizerReadiness.NotReady(TextOnlyReason));
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (_, state) = await RunChainAsync();

        await _holisticRunner.ReceivedWithAnyArgs(1).StartAsync(default!, default);
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_harmonizerScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldBe(TextOnlyReason);
        await _hubNotifier.DidNotReceiveWithAnyArgs().NotifyFailedAsync(default!);
    }

    [Test]
    public async Task StageThreeFails_WhileThePrerequisiteIsStillReady_FallsBackToTheHarmonizerScenario()
    {
        // Stage 3 only polishes a complete plan: its failure must not throw away the finished stage-2 plan.
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (jobId, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_harmonizerScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeTrue();
        state.Result.HarmonizationSkippedReason.ShouldNotBeNull();
        state.Result.HarmonizationSkippedReason!.ShouldContain("Holistic Harmonizer stage did not produce a result.");
        await _hubNotifier.Received(1).NotifyCompletedAsync(jobId, Arg.Is<AutoWizardJobResultDto>(d => d.HarmonizationSkipped));
        await _hubNotifier.DidNotReceiveWithAnyArgs().NotifyFailedAsync(default!);
    }

    [Test]
    public async Task InvalidApprovedHardRule_DoesNotStopTheChain_AndIsReportedAsAWarning()
    {
        var invalidRuleId = Guid.NewGuid();
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], [invalidRuleId]));

        var (_, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_holisticScenarioId);
        state.Result.ComplianceViolations.ShouldBeEmpty();
        var warning = state.Result.PlanningRuleWarnings.ShouldNotBeNull().ShouldHaveSingleItem();
        warning.Comment.ShouldBe(ScheduleValidationKeys.PlanningRuleInvalid);
        warning.Type.ShouldBe(ScheduleValidationType.Warning);
        warning.CommentParams![PlanningRuleNotificationMapper.RuleIdParam].ShouldBe(invalidRuleId.ToString());
        await _ruleSetLoader.Received(1).LoadRuleSetAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), PeriodFrom, PeriodUntil, Arg.Any<Guid?>(), Arg.Any<int>(),
            PlanningRuleSources.PlanningConstraints, InvalidHardRuleHandling.Report, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidApprovedHardRule_IsKeptApartFromTheWizardComplianceViolations()
    {
        var wizardViolation = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Warning,
            ClientId = Guid.NewGuid(),
            Date = PeriodFrom,
            Comment = ScheduleValidationKeys.PlanningRule,
        };
        _wizardApply.ApplyAsScenarioAsync(
                _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>())
            .Returns((Resource(_wizardScenarioId), new WizardApplyOutcome([], [wizardViolation], [], false)));
        var invalidRuleIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _ruleSetLoader.LoadRuleSetAsync(default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(new PlanningRuleSet([], [], [], [], invalidRuleIds));

        var (jobId, state) = await RunChainAsync();

        var violation = state.Result!.ComplianceViolations.ShouldHaveSingleItem();
        violation.Comment.ShouldBe(wizardViolation.Comment);
        violation.ClientId.ShouldBe(wizardViolation.ClientId);
        state.Result.PlanningRuleWarnings.ShouldNotBeNull().Count.ShouldBe(invalidRuleIds.Length);
        state.Result.PlanningRuleWarnings!.ShouldAllBe(w => w.Comment == ScheduleValidationKeys.PlanningRuleInvalid);
        await _hubNotifier.Received(1).NotifyCompletedAsync(jobId, Arg.Is<AutoWizardJobResultDto>(d =>
            d.ComplianceViolations.Count == 1 && d.PlanningRuleWarnings!.Count == invalidRuleIds.Length));
    }

    [Test]
    public async Task NoInvalidRule_ReportsNoPlanningRuleWarning()
    {
        var (_, state) = await RunChainAsync();

        state.Result!.PlanningRuleWarnings.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Test]
    public async Task DeterministicDefaultMode_WithoutAnyModel_RunsStageThree()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        RebuildRunner(new HolisticHarmonizerReadinessCheck(settingsReader, new HolisticHarmonizerModelCapabilityCache()));

        var (_, state) = await RunChainAsync();

        await _holisticRunner.ReceivedWithAnyArgs(1).StartAsync(default!, default);
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_holisticScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeFalse();
    }

    [Test]
    public async Task SkippedStageThree_NotesTheOutcomeOnTheHarmonizerScenario_AndDeletesOnlyTheIntermediate()
    {
        _readiness.CheckAsync(Arg.Any<CancellationToken>()).Returns(HolisticHarmonizerReadiness.NotReady(TextOnlyReason));

        await RunChainAsync();

        await _scenarioRepository.Received(1).Put(Arg.Is<AnalyseScenario>(s => s.Id == _harmonizerScenarioId));
        await _scenarioRepository.DidNotReceive().Put(Arg.Is<AnalyseScenario>(s => s.Id != _harmonizerScenarioId));
        _scenarios[_harmonizerScenarioId].Description.ShouldBe(
            "harmonized | " + AutoWizardStageOutcomePlanner.BuildHarmonizationSkippedNote(TextOnlyReason));
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId });
    }

    [Test]
    public async Task FailedStageThree_NotesTheFallbackOnTheHarmonizerScenario_AndDeletesOnlyTheIntermediate()
    {
        _holisticApply.ApplyAsScenarioAsync(
                _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns<(AnalyseScenarioResource, IReadOnlyList<Guid>, ScenarioComplianceReport?)>(
                _ => throw new InvalidOperationException("no holistic result cached"));

        var (_, state) = await RunChainAsync();

        await _scenarioRepository.Received(1).Put(Arg.Is<AnalyseScenario>(s => s.Id == _harmonizerScenarioId));
        _scenarios[_harmonizerScenarioId].Description.ShouldBe(
            "harmonized | " + AutoWizardStageOutcomePlanner.BuildHarmonizationSkippedNote(state.Result!.HarmonizationSkippedReason!));
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId });
    }

    [Test]
    public async Task EveryStageScenario_IsNamedWithItsOwnKind_InTheRequestLanguage()
    {
        await RunChainAsync();

        await _wizardApply.Received(1).ApplyAsScenarioAsync(
            _wizardStageJobId, Arg.Any<Guid?>(), false, Arg.Any<CancellationToken>(),
            ScenarioNameKind.AutoPlan, RequestLanguage);
        await _harmonizerApply.Received(1).ApplyAsScenarioAsync(
            _harmonizerStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(),
            ScenarioNameKind.AutoHarmonizer, RequestLanguage, Arg.Any<bool>(), Arg.Any<bool>());
        await _holisticApply.Received(1).ApplyAsScenarioAsync(
            _holisticStageJobId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>(),
            ScenarioNameKind.Auto, RequestLanguage, Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Test]
    public async Task StageThreeRuns_CompletesWithTheHolisticScenario_WithoutANote()
    {
        var (_, state) = await RunChainAsync();

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Result!.FinalScenarioId.ShouldBe(_holisticScenarioId);
        state.Result.HarmonizationSkipped.ShouldBeFalse();
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Put(default!);
        DeletedScenarioIds().ShouldBe(new[] { _wizardScenarioId, _harmonizerScenarioId }, ignoreOrder: true);
    }
}
