// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OpenOrderSummaryRescan, the event-driven run of the open-order summary after a completed ERP
/// import: it resolves OpenOrderDetector and IAgentTriggerDetectorRunner from a scope of its own (never from the
/// importing scope), runs exactly that detector through the runner, never lets a failure escape into the import,
/// and still honours cancellation.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class OpenOrderSummaryRescanTests
{
    private InMemoryShiftStore _store = null!;
    private IAgentTriggerDetectorRunner _runner = null!;
    private List<OpenOrderDetector> _resolvedDetectors = null!;
    private ServiceProvider _provider = null!;
    private RecordingLogger<OpenOrderSummaryRescan> _logger = null!;
    private OpenOrderSummaryRescan _sut = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryShiftStore();
        _runner = Substitute.For<IAgentTriggerDetectorRunner>();
        _runner.RunAsync(Arg.Any<IAgentTriggerDetector>(), Arg.Any<CancellationToken>())
            .Returns(new AgentTriggerDetectorRunOutcome(1, 1, 0));
        _resolvedDetectors = [];

        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var detector = new OpenOrderDetector(
                _store.Reader,
                ShiftGroupScopeReaderStub.WithoutAnyGroups(),
                new FixedCompanyClock(DateTimeOffset.UtcNow),
                NullLogger<OpenOrderDetector>.Instance);
            _resolvedDetectors.Add(detector);
            return detector;
        });
        services.AddScoped(_ => _runner);
        _provider = services.BuildServiceProvider();
        _logger = new RecordingLogger<OpenOrderSummaryRescan>();
        _sut = new OpenOrderSummaryRescan(_provider.GetRequiredService<IServiceScopeFactory>(), _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _store.Dispose();
    }

    [Test]
    public async Task RunAsync_RunsTheOpenOrderDetectorThroughTheRunnerOnce()
    {
        await _sut.RunAsync();

        var detector = _resolvedDetectors.ShouldHaveSingleItem();
        await _runner.Received(1).RunAsync(detector, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ResolvesAFreshScopePerRun()
    {
        await _sut.RunAsync();
        await _sut.RunAsync();

        Assert.That(_resolvedDetectors, Has.Count.EqualTo(2));
        Assert.That(_resolvedDetectors[0], Is.Not.SameAs(_resolvedDetectors[1]));
    }

    [Test]
    public async Task RunAsync_RunnerFails_IsLoggedAndNeverThrowsIntoTheImport()
    {
        _runner.RunAsync(Arg.Any<IAgentTriggerDetector>(), Arg.Any<CancellationToken>())
            .Returns<Task<AgentTriggerDetectorRunOutcome>>(_ => throw new InvalidOperationException("ledger down"));

        await Should.NotThrowAsync(() => _sut.RunAsync());

        Assert.That(_logger.Entries.Any(entry => entry.Level == LogLevel.Error), Is.True);
    }

    [Test]
    public void RunAsync_Cancelled_PropagatesTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _runner.RunAsync(Arg.Any<IAgentTriggerDetector>(), Arg.Any<CancellationToken>())
            .Returns<Task<AgentTriggerDetectorRunOutcome>>(_ => throw new OperationCanceledException(cancellation.Token));

        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RunAsync(cancellation.Token));
    }
}
