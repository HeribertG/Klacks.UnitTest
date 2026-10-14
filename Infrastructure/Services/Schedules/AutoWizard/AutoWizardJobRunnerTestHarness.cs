// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shared harness for driving the real AutoWizardJobRunner through a whole chain: substituted stage runners, apply
/// services and autofill presence tracker, real registries and result caches, and InMemory-backed terminal state caches.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.DTOs.Schedules.Wizard;
using Klacks.Api.Application.Exceptions;
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

public abstract class AutoWizardJobRunnerTestHarness
{
    protected const string RequestLanguage = "de";
    protected static readonly TimeSpan ChainTimeout = TimeSpan.FromSeconds(20);
    protected static readonly DateOnly PeriodFrom = new(2026, 11, 2);
    protected static readonly DateOnly PeriodUntil = new(2026, 11, 8);

    protected readonly Guid _wizardStageJobId = Guid.NewGuid();
    protected readonly Guid _harmonizerStageJobId = Guid.NewGuid();
    protected readonly Guid _holisticStageJobId = Guid.NewGuid();
    protected readonly Guid _wizardScenarioId = Guid.NewGuid();
    protected readonly Guid _harmonizerScenarioId = Guid.NewGuid();
    protected readonly Guid _holisticScenarioId = Guid.NewGuid();
    protected readonly Guid _presenceRunId = Guid.NewGuid();

    protected IAutoWizardHubNotifier _hubNotifier = null!;
    protected IAutofillRunPresenceTracker _presenceTracker = null!;
    protected IWizardJobRunner _wizardRunner = null!;
    protected IHarmonizerJobRunner _harmonizerRunner = null!;
    protected IHolisticHarmonizerJobRunner _holisticRunner = null!;
    protected IWizardApplyService _wizardApply = null!;
    protected IHarmonizerApplyService _harmonizerApply = null!;
    protected IHolisticHarmonizerApplyService _holisticApply = null!;
    protected IHolisticHarmonizerReadinessCheck _readiness = null!;
    protected IAnalyseScenarioRepository _scenarioRepository = null!;
    protected IMediator _mediator = null!;
    protected JobTerminalStateCache<AutoWizardJobResultDto> _stateCache = null!;
    protected Dictionary<Guid, AnalyseScenario> _scenarios = null!;
    protected ServiceProvider? _provider;
    protected IHarmonizerContextBuilder _contextBuilder = null!;
    protected IEligibilityMatrixBuilder _matrixBuilder = null!;
    protected IPlanningRuleSetLoader _ruleSetLoader = null!;
    protected WizardResultCache _wizardResults = null!;
    protected HarmonizerResultCache _harmonizerResults = null!;
    protected AutoWizardJobRunner _runner = null!;

    [SetUp]
    public void SetUp()
    {
        _hubNotifier = Substitute.For<IAutoWizardHubNotifier>();
        _presenceTracker = Substitute.For<IAutofillRunPresenceTracker>();
        _presenceTracker.BeginAsync(default, default, default).ReturnsForAnyArgs(call =>
            new AutofillRunPresenceDto(_presenceRunId, call.ArgAt<Guid?>(0), call.ArgAt<DateOnly>(1), call.ArgAt<DateOnly>(2), null));

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

    protected void RebuildRunner(IHolisticHarmonizerReadinessCheck readiness)
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
        services.AddSingleton(TestGroupWriteVisibility.UnrestrictedGroups());
        services.AddSingleton(TestScenarioTokenAccess.AllVisible());
        _provider = services.BuildServiceProvider();

        _stateCache = JobTerminalStateCacheTestFactory.Create<AutoWizardJobResultDto>();

        _runner = new AutoWizardJobRunner(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _hubNotifier,
            new AutoWizardJobRegistry(),
            _presenceTracker,
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

    protected AnalyseScenarioResource Resource(Guid scenarioId) => new()
    {
        Id = scenarioId,
        Name = _scenarios?.GetValueOrDefault(scenarioId)?.Name ?? string.Empty,
        Token = Guid.NewGuid()
    };

    protected static StartAutoWizardRequest Request(Guid? groupId, Guid? analyseToken = null) =>
        new(PeriodFrom, PeriodUntil, [Guid.NewGuid()], [Guid.NewGuid()], groupId, analyseToken, RequestLanguage);

    protected Task<(Guid JobId, JobTerminalState<AutoWizardJobResultDto> State)> RunChainAsync()
        => RunChainAsync(Request(Guid.NewGuid()));

    protected async Task<(Guid JobId, JobTerminalState<AutoWizardJobResultDto> State)> RunChainAsync(StartAutoWizardRequest request)
    {
        var jobId = await _runner.StartAsync(request, CancellationToken.None);
        return await WaitForChainAsync(jobId);
    }

    protected async Task<(Guid JobId, JobTerminalState<AutoWizardJobResultDto> State)> WaitForChainAsync(Guid jobId)
    {
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

    protected IReadOnlyList<Guid> DeletedScenarioIds() =>
        _mediator.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<DeleteAnalyseScenarioCommand>()
            .Select(command => command.ScenarioId)
            .ToList();

    protected static async Task<Guid> BlockUntilCancelledAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return Guid.Empty;
    }

    protected Task WaitForPresenceEndAsync() => WaitUntilAsync(
        () => _presenceTracker.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IAutofillRunPresenceTracker.EndAsync)),
        "The AutoWizard chain did not end its presence in time.");

    protected static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + ChainTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(timeoutMessage);
            }

            await Task.Delay(50);
        }
    }
}
