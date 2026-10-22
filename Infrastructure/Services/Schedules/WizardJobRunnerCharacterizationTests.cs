// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of WizardJobRunner.RunJobAsync, driven through the real StartAsync with a real DI scope, the real
/// GA on a tiny context (training overrides keep it to a few generations), real registry, start guard, result cache
/// and an InMemory terminal-state cache. Pins the terminal state, the hub calls (the awaited generation-0 progress
/// included), the result DTO against the cached best scenario, the order of the completion side effects, the rendered
/// log lines and that every path releases the run lock. The hard-timeout branch needs 110 s of wall clock and is not
/// reachable here.
/// </summary>

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardJobRunnerCharacterizationTests
{
    private const int Seed = 7;
    private const int PopulationSize = 8;
    private const int MaxGenerations = 12;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);
    private static readonly DateOnly From = new(2026, 11, 2);
    private static readonly DateOnly Until = new(2026, 11, 8);
    private static readonly Guid AgentA = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid AgentB = Guid.Parse("0b000000-0000-0000-0000-000000000002");
    private static readonly Guid ShiftOne = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid ShiftTwo = Guid.Parse("e0000000-0000-0000-0000-000000000002");

    private readonly Guid _analyseToken = Guid.NewGuid();
    private ServiceProvider _provider = null!;
    private IWizardContextBuilder _contextBuilder = null!;
    private IEligibilityMatrixBuilder _matrixBuilder = null!;
    private IHubContext<WizardJobHub, IWizardJobClient> _hub = null!;
    private IWizardJobClient _client = null!;
    private WizardResultCache _resultCache = null!;
    private JobTerminalStateCache<WizardJobResultDto> _stateCache = null!;
    private ConcurrentRecordingLogger<WizardJobRunner> _logger = null!;
    private WizardJobRunner _runner = null!;
    private CoreWizardContext _context = null!;
    private WizardJobResultDto? _completedDto;
    private ConcurrentQueue<WizardJobProgressDto> _progress = null!;
    private IReadOnlyCollection<EligibilitySlot>? _slots;
    private IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>? _incumbents;
    private bool _resultCachedAtEligibility;

    [SetUp]
    public void SetUp()
    {
        _context = BuildContext();
        _contextBuilder = Substitute.For<IWizardContextBuilder>();
        _contextBuilder.BuildContextAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>()).Returns(_context);
        _matrixBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        _matrixBuilder.BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _slots = call.ArgAt<IReadOnlyCollection<EligibilitySlot>>(1);
                _incumbents = call.ArgAt<IReadOnlySet<(string, Guid, DateOnly)>?>(2);
                _resultCachedAtEligibility = _progress.TryPeek(out var first) && _resultCache.TryGet(first.JobId, out _, out _, out _, out _, out _);
                return EligibilityMatrix.Empty;
            });

        var services = new ServiceCollection();
        services.AddSingleton(_contextBuilder);
        services.AddSingleton(_matrixBuilder);
        services.AddSingleton(TestScenarioTokenAccess.AllVisible());
        _provider = services.BuildServiceProvider();

        _progress = new ConcurrentQueue<WizardJobProgressDto>();
        _client = Substitute.For<IWizardJobClient>();
        _client.OnCompleted(Arg.Do<WizardJobResultDto>(dto => _completedDto = dto));
        _client.OnProgress(Arg.Do<WizardJobProgressDto>(p => _progress.Enqueue(p)));
        _hub = Substitute.For<IHubContext<WizardJobHub, IWizardJobClient>>();
        _hub.Clients.Group(Arg.Any<string>()).Returns(_client);

        _resultCache = new WizardResultCache();
        _stateCache = JobTerminalStateCacheTestFactory.Create<WizardJobResultDto>();
        _logger = new ConcurrentRecordingLogger<WizardJobRunner>();
        _runner = new WizardJobRunner(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _hub,
            new WizardJobRegistry(),
            new AutofillStartGuard(),
            _resultCache,
            _stateCache,
            Substitute.For<IHostApplicationLifetime>(),
            Options.Create(new WizardOptions { EvaluationParallelism = 1 }),
            _logger);
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    [Test]
    public async Task FinishedJob_StoresTheResult_BroadcastsIt_AndAnnouncesGenerationZeroFirst()
    {
        var jobId = await RunAsync();

        var state = await _stateCache.TryGetAsync(jobId);
        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Reason.ShouldBeNull();
        state.Result!.JobId.ShouldBe(jobId);

        _hub.Clients.Received().Group(SignalRConstants.WizardGroups.WizardJob(jobId));
        await _client.Received(1).OnCompleted(Arg.Any<WizardJobResultDto>());
        await _client.DidNotReceive().OnFailed(Arg.Any<string>());
        await _client.DidNotReceive().OnCancelled();

        _progress.TryPeek(out var first).ShouldBeTrue();
        first.ShouldBe(new WizardJobProgressDto(
            JobId: jobId,
            Generation: 0,
            MaxGenerations: MaxGenerations,
            BestHardViolations: 0,
            BestStage1Completion: 0d,
            BestStage2Score: 0d,
            EarlyStopping: false));
        _progress.ShouldAllBe(p => p.JobId == jobId && p.MaxGenerations == MaxGenerations);
    }

    [Test]
    public async Task FinishedJob_LogsStartContextCapsSeedLoopEntryAndLoopEnd_InThatOrder()
    {
        var jobId = await RunAsync();

        var messages = _logger.Entries.Select(e => e.Message).ToList();
        var all = string.Join(Environment.NewLine, messages);
        messages[0].ShouldBe(string.Format(CultureInfo.InvariantCulture,
            "Wizard job {0} starting (period {1} - {2}, 2 agents, 2 shifts, budget 90s)", jobId, From, Until), all);
        messages[1].ShouldBe($"Wizard job {jobId} context built", all);
        messages[2].ShouldBe(string.Format(CultureInfo.InvariantCulture,
            "Wizard job {0} effective caps: SchedulingMinPauseHours={1}h, SchedulingMaxDailyHours={2}h, SchedulingMaxConsecutiveDays={3}; firstAgent.MinRestHours={4}h, firstAgent.MaxDailyHours={5}h, firstAgent.MaxConsecutiveDays={6}",
            jobId, 11d, 10d, 6, 11d, 10d, 6), all);
        messages[3].ShouldBe($"Wizard job {jobId} using RandomSeed {Seed}", all);
        messages[4].ShouldBe(
            $"Wizard job {jobId} entering GA loop (population={PopulationSize}, maxGen={MaxGenerations}, agents=2, demandSlots=2)", all);

        var generationLines = messages.Where(m => m.StartsWith($"Wizard job {jobId} gen=", StringComparison.Ordinal)).ToList();
        generationLines.ShouldAllBe(m => Regex.IsMatch(m,
            "^" + Regex.Escape($"Wizard job {jobId} gen=") + @"\d+/" + MaxGenerations + @" viol=\d+ stage1=\d+\.\d% \+\d+ms$"));
        var runnerLines = messages.Where(m => !generationLines.Contains(m)).ToList();
        var loopEnd = runnerLines.FindIndex(m => m.StartsWith($"Wizard job {jobId} GA loop finished in ", StringComparison.Ordinal));
        loopEnd.ShouldBe(runnerLines.Count - 1, all);
        var dto = _completedDto!;
        runnerLines[loopEnd].ShouldMatch(
            "^" + Regex.Escape($"Wizard job {jobId} GA loop finished in ") + @"\d+" +
            Regex.Escape(string.Format(CultureInfo.InvariantCulture,
                "ms (tokens={0}, hardViol={1}, stage1={2:F1}%, timedOut=False)", dto.TokenCount, dto.FinalHardViolations, dto.FinalStage1Completion * 100)) + "$");
        runnerLines.Skip(5).Take(loopEnd - 5).ShouldAllBe(m => m.StartsWith($"Wizard job {jobId} [GA] ", StringComparison.Ordinal));
        _logger.Entries.ShouldAllBe(e => e.Level == LogLevel.Information);
    }

    [Test]
    public async Task FinishedJob_ResultDto_IsBuiltFromTheCachedBestScenario()
    {
        var jobId = await RunAsync();

        _resultCache.TryGet(jobId, out var best, out var token, out var escalations, out var subScoreJson, out var stage0).ShouldBeTrue();
        token.ShouldBe(_analyseToken);
        subScoreJson.ShouldNotBeNullOrEmpty();

        var dto = _completedDto!;
        dto.JobId.ShouldBe(jobId);
        dto.FinalHardViolations.ShouldBe(best!.FitnessStage0);
        dto.FinalStage1Completion.ShouldBe(best.FitnessStage1);
        dto.TokenCount.ShouldBe(best.Tokens.Count);
        dto.AvailableShiftSlots.ShouldBe(2);
        dto.TimedOut.ShouldBeFalse();
        dto.SubScoreJson.ShouldBe(subScoreJson);
        dto.Stage0Violations.ShouldBe(stage0);
        dto.Escalations.ShouldBeSameAs(escalations);
        Json(dto.Tokens).ShouldBe(Json(WizardJobRunner.MapTokens(best.Tokens)));

        var (awards, expectedEscalations) = WizardJobRunner.BuildAuctionTelemetry(_context, Seed, NullLogger.Instance);
        Json(dto.Awards).ShouldBe(Json(awards));
        Json(dto.Escalations).ShouldBe(Json(expectedEscalations));

        var expectedGaps = QualificationGapReportBuilder.BuildUnfillableSlots(EligibilityMatrix.Empty, _context.Shifts, _context.Agents, best.Tokens)
            .Concat(QualificationGapReportBuilder.BuildAssignedUnqualified(
                EligibilityMatrix.Empty,
                best.Tokens.Where(t => t.ShiftRefId != Guid.Empty).Select(t => (t.AgentId, string.Empty, t.ShiftRefId, t.Date))))
            .ToList();
        Json(dto.QualificationGaps).ShouldBe(Json(expectedGaps));

        var expectedUnfilled = WizardJobRunner.DiagnoseUnfilledSlots(jobId, _context, best.Tokens, NullLogger.Instance, CancellationToken.None);
        dto.UnfilledSlots.ShouldNotBeNull();
        Json(dto.UnfilledSlots).ShouldBe(Json(expectedUnfilled));
    }

    [Test]
    public async Task FinishedJob_AsksEligibilityForTheShiftSlotsWithoutIncumbents_AfterTheResultWasCached()
    {
        string? stateStatusAtBroadcast = null;
        _client.OnCompleted(Arg.Any<WizardJobResultDto>()).Returns(call =>
        {
            stateStatusAtBroadcast = _stateCache.TryGetAsync(call.Arg<WizardJobResultDto>().JobId).GetAwaiter().GetResult().Status;
            return Task.CompletedTask;
        });

        await RunAsync();

        await _contextBuilder.Received(1).BuildContextAsync(
            Arg.Is<WizardContextRequest>(r => r.AnalyseToken == _analyseToken && r.TrainingOverrides!.RandomSeed == Seed),
            Arg.Any<CancellationToken>());
        await _matrixBuilder.Received(1).BuildAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { AgentA, AgentB })),
            Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
            Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(),
            Arg.Any<CancellationToken>());
        _slots.ShouldBe(EligibilityMatrixBuilder.SlotsFromShifts(_context.Shifts));
        _incumbents.ShouldBeNull();
        _resultCachedAtEligibility.ShouldBeTrue("the best scenario is cached before the qualification gaps are built");
        stateStatusAtBroadcast.ShouldBe(WizardJobStatusValues.Completed);
    }

    [Test]
    public async Task CompletedBroadcastThatThrows_KeepsTheJobCompleted_AndOnlyWarns()
    {
        var broadcastFailure = new InvalidOperationException("hub down");
        _client.OnCompleted(Arg.Any<WizardJobResultDto>()).Returns(Task.FromException(broadcastFailure));

        var jobId = await RunAsync();

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Completed);
        await _client.DidNotReceive().OnFailed(Arg.Any<string>());
        var warning = _logger.Entries.Single(e => e.Level == LogLevel.Warning);
        warning.Message.ShouldBe($"Wizard job {jobId} completed but the broadcast failed");
        warning.Exception.ShouldBeSameAs(broadcastFailure);
        _logger.Entries.Last(e => !e.Message.StartsWith($"Wizard job {jobId} gen=", StringComparison.Ordinal)).ShouldBeSameAs(warning);
    }

    [Test]
    public async Task GenerationZeroBroadcastThatThrows_FailsTheJob_BeforeTheLoopStarts()
    {
        _client.OnProgress(Arg.Is<WizardJobProgressDto>(p => p.Generation == 0))
            .Returns(Task.FromException(new InvalidOperationException("hub down")));

        var jobId = await RunAsync();

        var state = await _stateCache.TryGetAsync(jobId);
        state.Status.ShouldBe(WizardJobStatusValues.Failed);
        state.Reason.ShouldBe("hub down");
        await _client.Received(1).OnFailed("hub down");
        _logger.Entries.ShouldNotContain(e => e.Message.Contains("entering GA loop"));
        _logger.Entries.Last().Level.ShouldBe(LogLevel.Error);
        _logger.Entries.Last().Message.ShouldBe($"Wizard job {jobId} failed");
        _resultCache.TryGet(jobId, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public async Task LaterProgressBroadcastThatThrows_OnlyWarns_AndTheJobCompletes()
    {
        _client.OnProgress(Arg.Is<WizardJobProgressDto>(p => p.Generation > 0))
            .Returns(Task.FromException(new InvalidOperationException("hub down")));

        var jobId = await RunAsync();

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Completed);
        var later = _progress.Where(p => p.Generation > 0).ToList();
        if (later.Count == 0)
        {
            Assert.Inconclusive("The GA reported no generation after 0 on this context.");
        }

        await WaitUntilAsync(() => _logger.Entries.ToList().Any(e => e.Level == LogLevel.Warning));
        var warnings = _logger.Entries.ToList().Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.ShouldAllBe(w => Regex.IsMatch(w.Message, "^" + Regex.Escape($"Failed to broadcast wizard progress for job {jobId} gen ") + @"\d+$"));
    }

    [Test]
    public async Task ContextBuildThatThrows_FailsTheJobWithItsMessage_AndLogsAnError()
    {
        var failure = new InvalidOperationException("boom");
        _contextBuilder.BuildContextAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CoreWizardContext>(failure));

        var jobId = await RunAsync();

        var state = await _stateCache.TryGetAsync(jobId);
        state.Status.ShouldBe(WizardJobStatusValues.Failed);
        state.Reason.ShouldBe("boom");
        state.Result.ShouldBeNull();
        await _client.Received(1).OnFailed("boom");
        await _client.DidNotReceive().OnCompleted(Arg.Any<WizardJobResultDto>());
        await _client.DidNotReceive().OnCancelled();
        await _client.DidNotReceive().OnProgress(Arg.Any<WizardJobProgressDto>());

        _logger.Entries.Count.ShouldBe(2);
        var error = _logger.Entries.Last();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldBe($"Wizard job {jobId} failed");
        error.Exception.ShouldBeSameAs(failure);
    }

    [Test]
    public async Task FailureBroadcastThatThrows_IsSwallowed_AndTheJobStaysFailed()
    {
        _contextBuilder.BuildContextAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CoreWizardContext>(new InvalidOperationException("boom")));
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
        _contextBuilder.BuildContextAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(1));
                return _context;
            });

        var jobId = await StartAsync();
        await reached.Task.WaitAsync(JobTimeout);
        _runner.TryCancel(jobId).ShouldBeTrue();
        await WaitAsync(jobId);

        var state = await _stateCache.TryGetAsync(jobId);
        state.Status.ShouldBe(WizardJobStatusValues.Cancelled);
        state.Reason.ShouldBeNull();
        await _client.Received(1).OnCancelled();
        await _client.DidNotReceive().OnFailed(Arg.Any<string>());
        await _client.DidNotReceive().OnCompleted(Arg.Any<WizardJobResultDto>());
        var last = _logger.Entries.Last();
        last.Level.ShouldBe(LogLevel.Information);
        last.Message.ShouldMatch("^" + Regex.Escape($"Wizard job {jobId} cancelled by user after ") + @"\d{2}:\d{2}:\d{2}(\.\d+)?$");
        _logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        await AssertRunLockReleasedAsync();
    }

    [Test]
    public async Task CancelBroadcastThatThrows_IsSwallowed_AndTheJobStaysCancelled()
    {
        _client.OnCancelled().Returns(Task.FromException(new InvalidOperationException("hub down")));

        var jobId = await StartAsync();
        _runner.TryCancel(jobId).ShouldBeTrue();
        await WaitAsync(jobId);

        (await _stateCache.TryGetAsync(jobId)).Status.ShouldBe(WizardJobStatusValues.Cancelled);
        await _contextBuilder.DidNotReceiveWithAnyArgs().BuildContextAsync(default!, default);
        _logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task FinishedJob_HandsTheCancellableJobToken_ToEveryScopedCollaborator()
    {
        await RunAsync();

        await _contextBuilder.Received(1).BuildContextAsync(Arg.Any<WizardContextRequest>(), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
        await _matrixBuilder.Received(1).BuildAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
            Arg.Any<IReadOnlySet<(string, Guid, DateOnly)>?>(), Arg.Is<CancellationToken>(t => t.CanBeCanceled));
    }

    [Test]
    public async Task FinishedJob_ReleasesTheRunLock_SoTheSamePeriodCanStartAgain()
    {
        await RunAsync();

        await AssertRunLockReleasedAsync();
    }

    private async Task AssertRunLockReleasedAsync()
    {
        _contextBuilder.BuildContextAsync(Arg.Any<WizardContextRequest>(), Arg.Any<CancellationToken>()).Returns(_context);
        var secondJobId = await StartAsync();
        await WaitAsync(secondJobId);
        (await _stateCache.TryGetAsync(secondJobId)).Found.ShouldBeTrue();
    }

    private WizardContextRequest Request() => new(
        From, Until, [AgentA, AgentB], [ShiftOne, ShiftTwo], _analyseToken,
        new WizardTrainingOverrides(PopulationSize: PopulationSize, MaxGenerations: MaxGenerations, RandomSeed: Seed));

    private Task<Guid> StartAsync() => _runner.StartAsync(Request(), CancellationToken.None);

    private async Task<Guid> RunAsync()
    {
        var jobId = await StartAsync();
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
                Assert.Fail("The wizard job did not finish in time.");
            }

            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected log entry did not arrive in time.");
            }

            await Task.Delay(25);
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static CoreWizardContext BuildContext() => new()
    {
        PeriodFrom = From,
        PeriodUntil = Until,
        Agents = [Agent(AgentA), Agent(AgentB)],
        Shifts =
        [
            new CoreShift(ShiftOne.ToString(), "FD", "2026-11-02", "06:00", "14:00", 8, 1, 0),
            new CoreShift(ShiftTwo.ToString(), "SD", "2026-11-03", "14:00", "22:00", 8, 1, 0),
        ],
    };

    private static CoreAgent Agent(Guid id) => new(
        Id: id.ToString(),
        CurrentHours: 0,
        GuaranteedHours: 16,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2);
}
