// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SlackOwnerBridgeBackgroundService: with the feature flag off, ExecuteAsync returns
/// immediately and never invokes ISlackOwnerBridgeService; with the flag on, a single bridge cycle
/// invokes it in a fresh scope; and an exception thrown by the bridge service during one cycle is
/// swallowed so a later cycle still runs normally (a single failing cycle must never take the
/// background service down).
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class SlackOwnerBridgeBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private ISlackOwnerBridgeService _bridgeService = null!;
    private ServiceProvider _serviceProvider = null!;
    private SlackOwnerBridgeBackgroundService? _sut;

    [SetUp]
    public void SetUp()
    {
        _bridgeService = Substitute.For<ISlackOwnerBridgeService>();

        var services = new ServiceCollection();
        services.AddSingleton(_bridgeService);
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
    public async Task ExecuteAsync_FlagDisabled_ReturnsImmediatelyAndNeverInvokesBridgeService()
    {
        _sut = CreateSut(slackOwnerBridgeEnabled: false);

        await _sut.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(_sut.ExecuteTask!, Task.Delay(CompletionTimeout));

        finished.ShouldBe(_sut.ExecuteTask, "the service must return immediately when the flag is off, not poll");
        await _bridgeService.DidNotReceive().RunCycleAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_FlagEnabled_InvokesBridgeServiceInAScopedProvider()
    {
        _bridgeService.RunCycleAsync(Arg.Any<CancellationToken>()).Returns(1);
        _sut = CreateSut(slackOwnerBridgeEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        await _bridgeService.Received(1).RunCycleAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_BridgeServiceThrows_ExceptionIsSwallowedAndNextCycleStillRuns()
    {
        _bridgeService.RunCycleAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"), _ => 0);
        _sut = CreateSut(slackOwnerBridgeEnabled: true);

        await _sut.RunCycleAsync(CancellationToken.None);
        await _sut.RunCycleAsync(CancellationToken.None);

        await _bridgeService.Received(2).RunCycleAsync(Arg.Any<CancellationToken>());
    }

    private SlackOwnerBridgeBackgroundService CreateSut(bool slackOwnerBridgeEnabled)
    {
        var options = Options.Create(new BackgroundServiceOptions { SlackOwnerBridge = slackOwnerBridgeEnabled });
        return new SlackOwnerBridgeBackgroundService(
            _serviceProvider, options, NullLogger<SlackOwnerBridgeBackgroundService>.Instance);
    }
}
