// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the gate that keeps the scheduled learning tick and an administrator's manual trigger from
/// running at the same time. The manual endpoint answers "started or not" rather than queueing, so the
/// refusal has to be a real answer with a reason, not a silent wait.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningRunLauncherTests
{
    private BlockingLoop _loop = null!;
    private ServiceProvider _provider = null!;
    private SkillLearningRunLauncher _launcher = null!;

    [SetUp]
    public void SetUp()
    {
        _loop = new BlockingLoop();
        var services = new ServiceCollection();
        services.AddScoped<ISkillLearningLoop>(_ => _loop);
        _provider = services.BuildServiceProvider();

        _launcher = new SkillLearningRunLauncher(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<SkillLearningRunLauncher>>());
    }

    [TearDown]
    public void TearDown()
    {
        _loop.Release();
        _provider.Dispose();
    }

    [Test]
    public async Task ARunOnAnIdleLauncher_Starts()
    {
        _loop.Release();

        var ticket = await _launcher.RunAsync();

        ticket.Started.ShouldBeTrue();
        ticket.Reason.ShouldBeNull();
        _loop.Runs.ShouldBe(1);
    }

    [Test]
    public async Task WhileARunIsUnderWay_ASecondOneIsRefusedWithAReason()
    {
        var first = Task.Run(() => _launcher.RunAsync());
        _loop.WaitUntilRunning();

        var second = await _launcher.RunAsync();

        second.Started.ShouldBeFalse();
        second.Reason.ShouldNotBeNullOrWhiteSpace();

        _loop.Release();
        (await first).Started.ShouldBeTrue();
        _loop.Runs.ShouldBe(1);
    }

    [Test]
    public async Task AfterARunFinished_TheNextOneStartsAgain()
    {
        _loop.Release();

        (await _launcher.RunAsync()).Started.ShouldBeTrue();
        (await _launcher.RunAsync()).Started.ShouldBeTrue();

        _loop.Runs.ShouldBe(2);
    }

    // A run rebuilds the knowledge index several times, so the endpoint may not hold the request open.
    [Test]
    public void TheDetachedStart_ReturnsBeforeTheRunFinished()
    {
        var ticket = _launcher.StartDetached();

        ticket.Started.ShouldBeTrue();
        _loop.WaitUntilRunning();
        _loop.Release();
    }

    [Test]
    public void AFailingRun_StillReleasesTheGate()
    {
        _loop.Release();
        _loop.Throw = true;

        _launcher.RunAsync().GetAwaiter().GetResult().Started.ShouldBeTrue();

        _loop.Throw = false;
        _launcher.RunAsync().GetAwaiter().GetResult().Started.ShouldBeTrue();
    }

    private sealed class BlockingLoop : ISkillLearningLoop
    {
        private readonly ManualResetEventSlim _running = new(false);
        private readonly ManualResetEventSlim _mayFinish = new(false);

        public int Runs { get; private set; }

        public bool Throw { get; set; }

        public void Release() => _mayFinish.Set();

        public void WaitUntilRunning() => _running.Wait(TimeSpan.FromSeconds(5));

        public Task<SkillLearningRunSummary> RunAsync(CancellationToken cancellationToken = default)
        {
            Runs++;
            _running.Set();
            _mayFinish.Wait(TimeSpan.FromSeconds(5));
            _running.Reset();

            return Throw
                ? throw new InvalidOperationException("run failed")
                : Task.FromResult(SkillLearningRunSummary.Empty);
        }
    }
}
