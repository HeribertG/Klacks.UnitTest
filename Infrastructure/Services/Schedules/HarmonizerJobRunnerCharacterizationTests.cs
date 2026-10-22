// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of HarmonizerJobRunner.RunJobAsync, driven through the real StartAsync with a real DI scope,
/// real registry, start guard, result cache and an InMemory terminal-state cache. Pins what a finished, failed and
/// cancelled job leaves behind (terminal state, hub calls, cached result, telemetry), the order of the completion side
/// effects, the rendered log lines, and that every path releases the run lock. The hard-timeout branch needs 140 s of
/// wall clock and is not reachable here.
/// </summary>

using System.Globalization;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.Harmonizer.Telemetry;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class HarmonizerJobRunnerCharacterizationTests
{
    private const int Seed = 4242;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);
    private static readonly DateOnly From = new(2026, 11, 2);
    private static readonly DateOnly Until = new(2026, 11, 8);
    private static readonly Guid AgentA = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid AgentB = Guid.Parse("0b000000-0000-0000-0000-000000000002");
    private static readonly Guid EarlyShift = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid LateShift = Guid.Parse("e0000000-0000-0000-0000-000000000002");

    private readonly Guid _analyseToken = Guid.NewGuid();
    private ServiceProvider _provider = null!;
    private IHarmonizerContextBuilder _contextBuilder = null!;
    private IScheduleSnapshotMarkerService _markerService = null!;
    private IEligibilityMatrixBuilder _matrixBuilder = null!;
    private IHarmonizerTelemetrySink _sink = null!;
    private IHubContext<HarmonizerJobHub, IHarmonizerJobClient> _hub = null!;
    private IHarmonizerJobClient _client = null!;
    private HarmonizerResultCache _resultCache = null!;
    private JobTerminalStateCache<HarmonizerJobResultDto> _stateCache = null!;
    private RecordingLogger<HarmonizerJobRunner> _logger = null!;
    private HarmonizerJobRunner _runner = null!;
    private ScheduleSnapshotMarker _marker = null!;
    private BitmapInput _input = null!;
    private HarmonizerJobResultDto? _completedDto;
    private List<HarmonizerRunTelemetry> _telemetry = null!;
    private IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>? _incumbents;
    private IReadOnlyCollection<EligibilitySlot>? _slots;

    [SetUp]
    public void SetUp()
    {
        _input = BuildInput();
        _marker = new ScheduleSnapshotMarker(From, Until, [AgentA, AgentB], _analyseToken, 3, 0, "fingerprint");
        _contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        _contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>()).Returns(_input);
        _markerService = Substitute.For<IScheduleSnapshotMarkerService>();
        _markerService.ComputeAsync(default, default, default!, default, default).ReturnsForAnyArgs(_marker);
        _matrixBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        _matrixBuilder.BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _slots = call.ArgAt<IReadOnlyCollection<EligibilitySlot>>(1);
                _incumbents = call.ArgAt<IReadOnlySet<(string, Guid, DateOnly)>?>(2);
                return EligibilityMatrix.Empty;
            });
        _telemetry = [];
        _sink = Substitute.For<IHarmonizerTelemetrySink>();
        _sink.RecordAsync(Arg.Do<HarmonizerRunTelemetry>(t => _telemetry.Add(t)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_contextBuilder);
        services.AddSingleton(_markerService);
        services.AddSingleton(_matrixBuilder);
        services.AddSingleton(_sink);
        services.AddSingleton(TestScenarioTokenAccess.AllVisible());
        _provider = services.BuildServiceProvider();

        _client = Substitute.For<IHarmonizerJobClient>();
        _client.OnCompleted(Arg.Do<HarmonizerJobResultDto>(dto => _completedDto = dto));
        _hub = Substitute.For<IHubContext<HarmonizerJobHub, IHarmonizerJobClient>>();
        _hub.Clients.Group(Arg.Any<string>()).Returns(_client);

        _resultCache = new HarmonizerResultCache();
        _stateCache = JobTerminalStateCacheTestFactory.Create<HarmonizerJobResultDto>();
        _logger = new RecordingLogger<HarmonizerJobRunner>();
        _runner = new HarmonizerJobRunner(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _hub,
            new HarmonizerJobRegistry(),
            new AutofillStartGuard(),
            _resultCache,
            _stateCache,
            Options.Create(new HarmonizerOptions()),
            Substitute.For<IHostApplicationLifetime>(),
            _logger);
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    [Test]
    public async Task FinishedJob_StoresTheResult_BroadcastsIt_AndLogsStartSeedAndFinish()
    {
        var jobId = await RunAsync();

        var state = await _stateCache.TryGetAsync(jobId);
        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Reason.ShouldBeNull();
        state.Result!.JobId.ShouldBe(jobId);

        _hub.Clients.Received().Group(SignalRConstants.HarmonizerGroups.HarmonizerJob(jobId));
        await _client.Received(1).OnCompleted(Arg.Any<HarmonizerJobResultDto>());
        await _client.DidNotReceive().OnFailed(Arg.Any<string>());
        await _client.DidNotReceive().OnCancelled();

        var messages = _logger.Entries.Select(e => e.Message).ToList();
        messages.Count.ShouldBe(3, string.Join(Environment.NewLine, messages));
        messages[0].ShouldBe(string.Format(CultureInfo.InvariantCulture,
            "Harmonizer job {0} starting in conductor-only mode (period {1} - {2}, 2 agents, budget 120s)", jobId, From, Until));
        messages[1].ShouldBe($"Harmonizer job {jobId} using RandomSeed {Seed}");
        var dto = _completedDto!;
        messages[2].ShouldMatch(
            "^" + Regex.Escape($"Harmonizer job {jobId} finished in ") + @"\d+" +
            Regex.Escape(string.Format(CultureInfo.InvariantCulture,
                "ms (fitness {0:F3} -> {1:F3}, timedOut=False)", dto.GlobalFitnessBefore, dto.GlobalFitnessAfter)) + "$");
        _logger.Entries.ShouldAllBe(e => e.Level == LogLevel.Information);
    }

    [Test]
    public async Task FinishedJob_ResultDto_IsBuiltFromTheSortedSeedAndTheCachedBestBitmap()
    {
        var jobId = await RunAsync();

        _resultCache.TryGet(jobId, out var original, out var best, out var token, out var subScoreJson, out var stage0, out var marker)
            .ShouldBeTrue();
        token.ShouldBe(_analyseToken);
        marker.ShouldBeSameAs(_marker);
        subScoreJson.ShouldNotBeNullOrEmpty();
        stage0.ShouldBe(0);

        var scorer = new HarmonyScorer();
        var fitness = new HarmonyFitnessEvaluator(scorer);
        var sortedSeed = RowSorter.Sort(BitmapBuilder.Build(_input));
        var dto = _completedDto!;
        dto.JobId.ShouldBe(jobId);
        dto.GlobalFitnessBefore.ShouldBe(fitness.Evaluate(sortedSeed).Fitness);
        dto.GlobalFitnessAfter.ShouldBe(fitness.Evaluate(best!).Fitness);
        dto.GenerationsRun.ShouldBe(0);
        dto.TimedOut.ShouldBeFalse();
        dto.QualificationGaps.ShouldBeEmpty();
        (dto.PlanningRuleWarnings?.Count ?? -1).ShouldBe(PlanningRuleNotificationMapper.ToSkippedRuleWarnings(null, From)?.Count ?? -1);
        dto.PlanningRuleRemaining.ShouldBeNull();

        dto.RowResults.Count.ShouldBe(2);
        for (var r = 0; r < dto.RowResults.Count; r++)
        {
            dto.RowResults[r].AgentId.ShouldBe(best!.Rows[r].Id);
            dto.RowResults[r].ScoreBefore.ShouldBe(scorer.Score(original!, r).Score);
            dto.RowResults[r].ScoreAfter.ShouldBe(scorer.Score(best, r).Score);
            dto.RowResults[r].EmergencyUnlockTriggered.ShouldBe(_telemetry.Single().Rows[r].EmergencyUnlockTriggered);
        }

        original!.Rows.Select(row => row.Id).ShouldBe(sortedSeed.Rows.Select(row => row.Id));
    }

    [Test]
    public async Task FinishedJob_ContextSnapshotAndEligibility_AreAskedWithTheRequestAndTheOriginalIncumbents()
    {
        var jobId = await RunAsync();

        await _contextBuilder.Received(1).BuildContextAsync(
            Arg.Is<HarmonizerContextRequest>(r => r.Seed == Seed && r.AnalyseToken == _analyseToken), Arg.Any<CancellationToken>());
        await _markerService.Received(1).ComputeAsync(
            From, Until, Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { AgentA, AgentB })), _analyseToken, Arg.Any<CancellationToken>());
        await _matrixBuilder.Received(1).BuildAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { AgentA, AgentB })),
            Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
            Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(),
            Arg.Any<CancellationToken>());

        _incumbents.ShouldNotBeNull();
        _incumbents!.OrderBy(i => i.AgentId).ThenBy(i => i.Date).ShouldBe(new[]
        {
            (AgentA.ToString(), EarlyShift, From),
            (AgentA.ToString(), EarlyShift, From.AddDays(1)),
            (AgentB.ToString(), LateShift, From.AddDays(2)),
        });

        _resultCache.TryGet(jobId, out _, out var best, out _, out _, out _, out _).ShouldBeTrue();
        var expectedSlots = QualificationGapReportBuilder.ExtractBitmapAssignments(best!)
            .Select(a => new EligibilitySlot(a.ShiftId, a.Date))
            .Distinct()
            .ToList();
        _slots.ShouldBe(expectedSlots);
    }

    [Test]
    public async Task FinishedJob_SideEffects_RunInTheOrder_CacheThenStateThenBroadcastThenLogThenTelemetry()
    {
        var resultCachedAtBroadcast = false;
        string? stateStatusAtBroadcast = null;
        var logCountAtBroadcast = -1;
        var broadcastBeforeTelemetry = false;
        var logCountAtTelemetry = -1;
        Guid? currentJobId = null;

        _client.OnCompleted(Arg.Any<HarmonizerJobResultDto>()).Returns(call =>
        {
            var dto = call.Arg<HarmonizerJobResultDto>();
            _completedDto = dto;
            resultCachedAtBroadcast = _resultCache.TryGet(dto.JobId, out _, out _, out _, out _, out _, out _);
            stateStatusAtBroadcast = _stateCache.TryGetAsync(dto.JobId).GetAwaiter().GetResult().Status;
            logCountAtBroadcast = _logger.Entries.Count;
            currentJobId = dto.JobId;
            return Task.CompletedTask;
        });
        _sink.RecordAsync(Arg.Any<HarmonizerRunTelemetry>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            broadcastBeforeTelemetry = currentJobId != null;
            logCountAtTelemetry = _logger.Entries.Count;
            return Task.CompletedTask;
        });

        var jobId = await RunAsync();

        resultCachedAtBroadcast.ShouldBeTrue();
        stateStatusAtBroadcast.ShouldBe(WizardJobStatusValues.Completed);
        logCountAtBroadcast.ShouldBe(2, "start and seed are logged, the finish line comes after the broadcast");
        broadcastBeforeTelemetry.ShouldBeTrue();
        logCountAtTelemetry.ShouldBe(3, "the finish line is logged before the telemetry is emitted");

        var telemetry = _telemetry.Single();
        var dto = _completedDto!;
        telemetry.JobId.ShouldBe(jobId);
        telemetry.PeriodFrom.ShouldBe(From);
        telemetry.PeriodUntil.ShouldBe(Until);
        telemetry.RowCount.ShouldBe(2);
        telemetry.InitialFitness.ShouldBe(dto.GlobalFitnessBefore);
        telemetry.FinalFitness.ShouldBe(dto.GlobalFitnessAfter);
        telemetry.EmergencyThreshold.ShouldBe(0.5);
        telemetry.GenerationsRun.ShouldBe(dto.GenerationsRun);
        telemetry.TotalEmergencyUnlocks.ShouldBe(telemetry.Rows.Count(r => r.EmergencyUnlockTriggered));
        telemetry.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        telemetry.Rows.Select(r => r.RowIndex).ShouldBe(new[] { 0, 1 });
        telemetry.Rows.Select(r => r.AgentId).ShouldBe(dto.RowResults.Select(r => r.AgentId));
    }

    [Test]
    public async Task CompletedBroadcastThatThrows_KeepsTheJobCompleted_AndOnlyWarns()
    {
        var broadcastFailure = new InvalidOperationException("hub down");
        _client.OnCompleted(Arg.Any<HarmonizerJobResultDto>()).Returns(Task.FromException(broadcastFailure));

        var jobId = await RunAsync();

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Completed);
        await _client.DidNotReceive().OnFailed(Arg.Any<string>());
        var warning = _logger.Entries.Single(e => e.Level == LogLevel.Warning);
        warning.Message.ShouldBe($"Harmonizer job {jobId} completed but the broadcast failed");
        warning.Exception.ShouldBeSameAs(broadcastFailure);
        _logger.Entries.Last().Message.ShouldStartWith($"Harmonizer job {jobId} finished in ");
        _telemetry.Count.ShouldBe(1);
    }

    [Test]
    public async Task TelemetrySinkThatThrows_KeepsTheJobCompleted_AndOnlyWarns()
    {
        var sinkFailure = new InvalidOperationException("sink down");
        _sink.RecordAsync(Arg.Any<HarmonizerRunTelemetry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(sinkFailure));

        var jobId = await RunAsync();

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Completed);
        var warning = _logger.Entries.Last();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe($"Harmonizer telemetry sink failed for job {jobId}");
        warning.Exception.ShouldBeSameAs(sinkFailure);
    }

    [Test]
    public async Task ContextBuildThatThrows_FailsTheJobWithItsMessage_AndLogsAnError()
    {
        var failure = new InvalidOperationException("boom");
        _contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BitmapInput>(failure));

        var jobId = await RunAsync();

        var state = await _stateCache.TryGetAsync(jobId);
        state.Status.ShouldBe(WizardJobStatusValues.Failed);
        state.Reason.ShouldBe("boom");
        state.Result.ShouldBeNull();
        await _client.Received(1).OnFailed("boom");
        await _client.DidNotReceive().OnCompleted(Arg.Any<HarmonizerJobResultDto>());
        await _client.DidNotReceive().OnCancelled();
        await _markerService.DidNotReceiveWithAnyArgs().ComputeAsync(default, default, default!, default, default);
        _resultCache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeFalse();

        var error = _logger.Entries.Last();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe($"Harmonizer job {jobId} failed");
        error.Exception.ShouldBeSameAs(failure);
        _logger.Entries.Count.ShouldBe(2);
    }

    [Test]
    public async Task FailureBroadcastThatThrows_IsSwallowed_AndTheJobStaysFailed()
    {
        _contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BitmapInput>(new InvalidOperationException("boom")));
        _client.OnFailed(Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException("hub down")));

        var jobId = await RunAsync();

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Failed);
        _logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
        _logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
        await AssertRunLockReleasedAsync();
    }

    [Test]
    public async Task CancelWhileTheContextIsBuilt_StoresCancelled_BroadcastsIt_AndLogsTheUserCancel()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(1));
                return _input;
            });

        var jobId = await _runner.StartAsync(Request(), CancellationToken.None);
        await reached.Task.WaitAsync(JobTimeout);
        _runner.TryCancel(jobId).ShouldBeTrue();
        await WaitAsync(jobId);

        var state = await _stateCache.TryGetAsync(jobId);
        state.Status.ShouldBe(WizardJobStatusValues.Cancelled);
        state.Reason.ShouldBeNull();
        await _client.Received(1).OnCancelled();
        await _client.DidNotReceive().OnFailed(Arg.Any<string>());
        await _client.DidNotReceive().OnCompleted(Arg.Any<HarmonizerJobResultDto>());
        _logger.Entries.Last().Message.ShouldBe($"Harmonizer job {jobId} cancelled by user");
        _logger.Entries.Last().Level.ShouldBe(LogLevel.Information);
        _logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        await AssertRunLockReleasedAsync();
    }

    [Test]
    public async Task CancelBroadcastThatThrows_IsSwallowed_AndTheJobStaysCancelled()
    {
        _client.OnCancelled().Returns(Task.FromException(new InvalidOperationException("hub down")));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(1));
                return _input;
            });

        var jobId = await _runner.StartAsync(Request(), CancellationToken.None);
        await reached.Task.WaitAsync(JobTimeout);
        _runner.TryCancel(jobId);
        await WaitAsync(jobId);

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Cancelled);
        _logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task CancelDuringTheClientJoinDelay_NeverBuildsAContext_AndStoresCancelled()
    {
        var jobId = await _runner.StartAsync(Request(), CancellationToken.None);
        _runner.TryCancel(jobId).ShouldBeTrue();
        await WaitAsync(jobId);

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Cancelled);
        await _contextBuilder.DidNotReceiveWithAnyArgs().BuildContextAsync(default!, default);
        await _client.Received(1).OnCancelled();
    }

    [Test]
    public async Task FinishedJob_HandsTheCancellableJobToken_ToEveryScopedCollaborator()
    {
        await RunAsync();

        await _contextBuilder.Received(1).BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
        await _markerService.Received(1).ComputeAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<Guid?>(), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
        await _matrixBuilder.Received(1).BuildAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
            Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
        await _sink.Received(1).RecordAsync(Arg.Any<HarmonizerRunTelemetry>(), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
    }

    [Test]
    public async Task ConductorOnlyRun_BroadcastsTheSeedGenerationAsProgress()
    {
        var progress = new System.Collections.Concurrent.ConcurrentQueue<HarmonizerJobProgressDto>();
        _client.OnProgress(Arg.Do<HarmonizerJobProgressDto>(p => progress.Enqueue(p)));

        var jobId = await RunAsync();

        await WaitUntilAsync(() => !progress.IsEmpty);
        progress.TryPeek(out var first).ShouldBeTrue();
        first!.JobId.ShouldBe(jobId);
        first.Generation.ShouldBe(0);
        first.MaxGenerations.ShouldBe(0);
        first.EarlyStopping.ShouldBeFalse();
    }

    [Test]
    public async Task ProgressBroadcastThatThrows_OnlyWarns_AndTheJobCompletes()
    {
        _client.OnProgress(Arg.Any<HarmonizerJobProgressDto>()).Returns(Task.FromException(new InvalidOperationException("hub down")));

        var jobId = await RunAsync();

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Completed);
        await WaitUntilAsync(() => _logger.Entries.ToList().Any(e => e.Level == LogLevel.Warning));
        _logger.Entries.ToList().Single(e => e.Level == LogLevel.Warning).Message
            .ShouldBe($"Failed to broadcast harmonizer progress for job {jobId} gen 0");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected event did not arrive in time.");
            }

            await Task.Delay(25);
        }
    }

    [Test]
    public async Task FinishedJob_ReleasesTheRunLock_SoTheSamePeriodCanStartAgain()
    {
        await RunAsync();

        await AssertRunLockReleasedAsync();
    }

    private async Task AssertRunLockReleasedAsync()
    {
        _contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>()).Returns(_input);
        var secondJobId = await _runner.StartAsync(Request(), CancellationToken.None);
        await WaitAsync(secondJobId);
        (await _stateCache.TryGetAsync(secondJobId)).Found.ShouldBeTrue();
    }

    private HarmonizerContextRequest Request() =>
        new(From, Until, [AgentA, AgentB], AnalyseToken: _analyseToken, Seed: Seed);

    private async Task<Guid> RunAsync()
    {
        var jobId = await _runner.StartAsync(Request(), CancellationToken.None);
        await WaitAsync(jobId);
        return jobId;
    }

    private async Task WaitAsync(Guid jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (_runner.IsRunning(jobId))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The harmonizer job did not finish in time.");
            }

            await Task.Delay(25);
        }
    }

    private static BitmapInput BuildInput()
    {
        var agents = new List<BitmapAgent>
        {
            new(AgentA.ToString(), "Agent A", 16m, new HashSet<CellSymbol> { CellSymbol.Early }),
            new(AgentB.ToString(), "Agent B", 16m, new HashSet<CellSymbol> { CellSymbol.Early }),
        };
        var assignments = new List<BitmapAssignment>
        {
            Assignment(AgentA, From, CellSymbol.Early, EarlyShift, 6),
            Assignment(AgentA, From.AddDays(1), CellSymbol.Early, EarlyShift, 6),
            Assignment(AgentB, From.AddDays(2), CellSymbol.Late, LateShift, 14),
        };
        return new BitmapInput(agents, From, Until, assignments);
    }

    private static BitmapAssignment Assignment(Guid agent, DateOnly date, CellSymbol symbol, Guid shift, int startHour)
    {
        var start = date.ToDateTime(new TimeOnly(startHour, 0), DateTimeKind.Utc);
        return new BitmapAssignment(agent.ToString(), date, symbol, shift, [Guid.NewGuid()], false, start, start.AddHours(8), 8m);
    }
}
