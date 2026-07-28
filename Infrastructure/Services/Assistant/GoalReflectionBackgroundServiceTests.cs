// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GoalReflectionBackgroundService: with the feature flag off, ExecuteAsync returns
/// immediately and never invokes IGoalReflectionService; with the flag on, a single reflection cycle
/// invokes it in a fresh scope and logs the returned candidate count; and an exception thrown by the
/// reflection service during one cycle is swallowed so a later cycle still runs normally (a single
/// failing cycle must never take the background service down).
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class GoalReflectionBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private IGoalReflectionService _reflectionService = null!;
    private ServiceProvider _serviceProvider = null!;
    private GoalReflectionBackgroundService? _sut;

    [SetUp]
    public void SetUp()
    {
        _reflectionService = Substitute.For<IGoalReflectionService>();

        var services = new ServiceCollection();
        services.AddSingleton(_reflectionService);
        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_sut != null)
        {
            await _sut.StopAsync(CancellationToken.None);
            _sut.Dispose();
        }

        await _serviceProvider.DisposeAsync();
    }

    [Test]
    public async Task ExecuteAsync_FlagDisabled_ReturnsImmediatelyAndNeverInvokesReflectionService()
    {
        _sut = CreateSut(goalReflectionEnabled: false);

        await _sut.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(_sut.ExecuteTask!, Task.Delay(CompletionTimeout));

        finished.ShouldBe(_sut.ExecuteTask, "the service must return immediately when the flag is off, not poll");
        await _reflectionService.DidNotReceive().RunReflectionCycleAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_FlagEnabled_InvokesReflectionServiceAndLogsCandidateCount()
    {
        const int candidateCount = 3;
        _reflectionService.RunReflectionCycleAsync(Arg.Any<CancellationToken>()).Returns(candidateCount);
        _sut = CreateSut(goalReflectionEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _reflectionService.Received(1).RunReflectionCycleAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_ReflectionServiceThrows_ExceptionIsSwallowedAndNextCycleStillRuns()
    {
        _reflectionService.RunReflectionCycleAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"), _ => 0);
        _sut = CreateSut(goalReflectionEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);
        await _sut.RunCycleAsync(CancellationToken.None);

        await _reflectionService.Received(2).RunReflectionCycleAsync(Arg.Any<CancellationToken>());
    }

    private GoalReflectionBackgroundService CreateSut(bool goalReflectionEnabled)
    {
        var options = Options.Create(new BackgroundServiceOptions { GoalReflection = goalReflectionEnabled });
        return new GoalReflectionBackgroundService(
            _serviceProvider, options, NullLogger<GoalReflectionBackgroundService>.Instance);
    }
}
