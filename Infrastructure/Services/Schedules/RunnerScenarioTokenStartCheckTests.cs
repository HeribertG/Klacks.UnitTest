// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The planning runners check the source scenario token while the starting request is still running, before they take
/// a run lock: a hidden scenario is refused like a missing one and leaves no lock behind. Shown on the Wizard-2 runner;
/// Wizard 1, Wizard 3, AutoWizard and the benchmark call the same ScenarioTokenStartCheck.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class RunnerScenarioTokenStartCheckTests
{
    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 8);

    private readonly Guid _hiddenToken = Guid.NewGuid();
    private ServiceProvider _provider = null!;
    private AutofillStartGuard _startGuard = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestScenarioTokenAccess.Hiding(_hiddenToken));
        _provider = services.BuildServiceProvider();
        _startGuard = new AutofillStartGuard();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    [Test]
    public async Task HarmonizerStart_WithHiddenSourceScenario_IsRefusedAndHoldsNoRunLock()
    {
        var runner = new HarmonizerJobRunner(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHubContext<HarmonizerJobHub, IHarmonizerJobClient>>(),
            new HarmonizerJobRegistry(),
            _startGuard,
            new HarmonizerResultCache(),
            JobTerminalStateCacheTestFactory.Create<HarmonizerJobResultDto>(),
            Options.Create(new HarmonizerOptions()),
            Substitute.For<IHostApplicationLifetime>(),
            NullLogger<HarmonizerJobRunner>.Instance);

        var act = () => runner.StartAsync(
            new HarmonizerContextRequest(From, Until, [Guid.NewGuid()], AnalyseToken: _hiddenToken), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Scenario with token {_hiddenToken} not found");
        _startGuard.HasActiveRuns.ShouldBeFalse();
    }

    [Test]
    public async Task StartCheck_WithoutToken_DoesNotResolveTheGuard()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        await scopeFactory.EnsureScenarioTokenVisibleAsync(null, CancellationToken.None);

        scopeFactory.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Test]
    public async Task StartCheck_WithVisibleToken_Passes()
    {
        await Should.NotThrowAsync(() => _provider.GetRequiredService<IServiceScopeFactory>()
            .EnsureScenarioTokenVisibleAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
