// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Loop;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Characterization of <see cref="HolisticHarmonizerEngine.RunAsync(HolisticHarmonizerEngineRequest, IProgress{HolisticHarmonizerProgress}, CancellationToken)"/>
/// taken from the code before the inner loop was split into helpers. Every scenario records a full trace (each
/// proposal request, each progress report, each evaluated batch and the run result) and compares it with the trace
/// the old code produced. The roulette intent selector is unseeded, so the traces only include the focused intent and
/// the candidate list of iteration 0 (always the baseline intent); the scenarios are built so that control flow
/// never depends on the intent picked later: the empty bitmap yields no candidates for any intent, and the
/// assignment bitmap is only used with responses that do not depend on the candidate list after iteration 0.
/// </summary>
[TestFixture]
public class HolisticHarmonizerEngineCharacterizationTests
{
    private const string ModelId = "stub-model";
    private const string ProbeIntent = HolisticIntent.ConsolidateBlock;
    private const string ProbeReason = "probe";
    private const string ParsingFailure = "balanced-brace scan found no JSON object";
    private const int LongRawLength = 700;
    private const int PreviewLength = 600;
    private const string FitnessFormat = "G12";
    private static readonly DateOnly PeriodFrom = new(2026, 1, 5);
    private static readonly DateOnly PeriodUntil = new(2026, 1, 8);
    private static readonly Guid AgentGuid = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AnalyseToken = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid EarlyShift = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid LateShift = new("cccccccc-0000-0000-0000-000000000002");

    [Test]
    public async Task EmptyBitmap_EveryBatchRejected_StopsOnPlateauAfterFiveIterations()
    {
        var run = await RunAsync(EmptyContext(), (_, index) => Batches(index, new PlanCellSwap(0, 0, 1, 0, ProbeReason)));

        run.Requests.ShouldAllBe(request => request.CandidateMoves != null && request.CandidateMoves.Count == 0);
        run.Trace.ShouldBe(Expected.EmptyBitmapPlateau);
    }

    [Test]
    public async Task AssignmentBitmap_FirstBatchOfAllCandidates_ThenSatisfied()
    {
        var run = await RunAsync(AssignmentContext(), (request, index) => index == 0
            ? Batches(index, AllCandidates(request))
            : Satisfied());

        run.Requests[0].CandidateMoves!.Count.ShouldBeGreaterThan(0);
        run.Trace.ShouldBe(Expected.AssignmentAccepted);
    }

    [Test]
    public async Task AssignmentBitmap_SelfProposedStepIsDropped_IterationReportsProgressWithoutEvaluation()
    {
        var run = await RunAsync(AssignmentContext(), (_, index) => index == 0
            ? Batches(index, new PlanCellSwap(0, 3, 1, 3, ProbeReason))
            : Satisfied());

        run.Requests[0].CandidateMoves!.Count.ShouldBeGreaterThan(0);
        run.Trace.ShouldBe(Expected.AssignmentDropped);
    }

    [Test]
    public async Task ParsingErrorThenEvaluatedBatchThenSatisfied_ClearsTheParsingError()
    {
        var run = await RunAsync(EmptyContext(), (_, index) => index switch
        {
            0 => Unparsable(new string('x', LongRawLength)),
            1 => Batches(index, new PlanCellSwap(0, 0, 1, 0, ProbeReason)),
            _ => Satisfied(),
        });

        run.Trace.ShouldBe(Expected.ParsingErrorRecovered);
    }

    [Test]
    public async Task ParsingErrorThenSatisfied_KeepsTheParsingErrorAndPreviewsTheLastRawResponse()
    {
        var run = await RunAsync(EmptyContext(), (_, index) => index == 0 ? Unparsable("broken") : Satisfied());

        run.Trace.ShouldBe(Expected.ParsingErrorThenSatisfied);
    }

    [TestCase(LongRawLength)]
    [TestCase(PreviewLength)]
    public async Task ThreeParsingErrors_AbortWithThePreviewCutAtSixHundredCharacters(int rawLength)
    {
        var raw = new string('y', rawLength);

        var run = await RunAsync(EmptyContext(), (_, _) => Unparsable(raw));

        var expectedPreview = rawLength > PreviewLength ? raw[..PreviewLength] + "..." : raw;
        run.Result.LlmRawResponsePreview.ShouldBe(expectedPreview);
        run.Result.LlmParsingError.ShouldBe(ParsingFailure);
        run.Result.AbortedOnUnusableResponses.ShouldBeTrue();
        run.Requests.Count.ShouldBe(3);
        run.Progress.Count.ShouldBe(1);
    }

    [Test]
    public async Task ThreeBatchesWithoutSteps_AbortWithTheFallbackMessage()
    {
        var run = await RunAsync(EmptyContext(), (_, index) => Batches(index));

        run.Trace.ShouldBe(Expected.StepLessBatchesAbort);
    }

    [Test]
    public async Task UnhealthyPreflight_BuildsTheContextButNeverProposes()
    {
        var contextBuilder = ContextBuilder(EmptyContext());
        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .CapabilityCheckAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: false, LatencyMs: 42, Error: "model offline"));
        var progress = new RecordingProgress();

        var result = await CreateEngine(contextBuilder, provider).RunAsync(Request(), progress, CancellationToken.None);

        await contextBuilder.Received(1).BuildContextAsync(
            Arg.Is<HarmonizerContextRequest>(request =>
                request.PeriodFrom == PeriodFrom
                && request.PeriodUntil == PeriodUntil
                && request.AgentIds.Count == 1
                && request.AgentIds[0] == AgentGuid
                && request.AnalyseToken == AnalyseToken
                && request.ContextDaysBefore == 3
                && request.ContextDaysAfter == 5
                && request.Seed == null
                && request.LoadPlanningRules),
            Arg.Any<CancellationToken>());
        await provider.DidNotReceive().ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>());
        progress.Reports.ShouldBeEmpty();
        result.LlmParsingError.ShouldBe("Pre-flight check failed (42 ms): model offline");
        result.LlmRawResponsePreview.ShouldBeNull();
        result.AbortedOnUnusableResponses.ShouldBeTrue();
        result.Iterations.ShouldBeEmpty();
        result.FinalBitmap.ShouldBeSameAs(result.OriginalBitmap);
        result.FitnessAfter.ShouldBe(result.FitnessBefore);
        result.LlmModelId.ShouldBe(ModelId);
        result.InvalidPlanningRuleIds.ShouldBeEmpty();
        result.PlanningRuleHardCounts.ShouldBeNull();
    }

    [Test]
    public async Task AlreadyCancelledToken_ThrowsBeforeThePreflight()
    {
        var contextBuilder = ContextBuilder(EmptyContext());
        var provider = HealthyProvider();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateEngine(contextBuilder, provider).RunAsync(Request(), progress: null, cancellation.Token));

        await provider.DidNotReceive().CapabilityCheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await contextBuilder.DidNotReceive().BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancellationDuringTheProposal_ThrowsBeforeTheFirstBatchIsEvaluated()
    {
        var contextBuilder = ContextBuilder(EmptyContext());
        var provider = HealthyProvider();
        using var cancellation = new CancellationTokenSource();
        provider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Batches(0, new PlanCellSwap(0, 0, 1, 0, ProbeReason));
            });
        var progress = new RecordingProgress();

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateEngine(contextBuilder, provider).RunAsync(Request(), progress, cancellation.Token));

        await provider.Received(1).ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>());
        progress.Reports.Count.ShouldBe(1);
    }

    private static async Task<RunCapture> RunAsync(BitmapInput context, Func<PlanProposalRequest, int, PlanProposalResponse> script)
    {
        var requests = new List<PlanProposalRequest>();
        var provider = HealthyProvider();
        provider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<PlanProposalRequest>();
                requests.Add(request);
                return script(request, requests.Count - 1);
            });
        var progress = new RecordingProgress();

        var result = await CreateEngine(ContextBuilder(context), provider)
            .RunAsync(Request(language: null), progress, CancellationToken.None);

        var trace = BuildTrace(result, requests, progress.Reports);
        TestContext.Out.WriteLine("TRACE-BEGIN " + TestContext.CurrentContext.Test.Name);
        TestContext.Out.WriteLine(trace);
        TestContext.Out.WriteLine("TRACE-END");
        return new RunCapture(result, requests, progress.Reports, trace);
    }

    private static string BuildTrace(
        HolisticHarmonizerRunResult result,
        IReadOnlyList<PlanProposalRequest> requests,
        IReadOnlyList<HolisticHarmonizerProgress> progress)
    {
        var trace = new StringBuilder();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            trace.Append(CultureInfo.InvariantCulture,
                $"request {i}: model={request.ModelId} iter={request.IterationIndex} cap={request.MaxStepsPerBatch} lang={request.Language} priorRejections={request.PriorRejections.Count} png={request.PlanPng is { Length: > 0 }}");
            if (i == 0)
            {
                trace.Append(CultureInfo.InvariantCulture,
                    $" intent={request.FocusedIntent} candidates={request.CandidateMoves?.Count} plan={Rows(request.PlanText)} agents={Escape(request.AgentSummary)} fragmentation={Escape(request.FragmentationSummary)}");
            }
            else
            {
                trace.Append(CultureInfo.InvariantCulture, $" plan={Rows(request.PlanText)}");
            }

            trace.Append('\n');
        }

        foreach (var report in progress)
        {
            trace.Append(CultureInfo.InvariantCulture,
                $"progress: iter={report.IterationIndex} max={report.MaxIterations} best={Format(report.BestFitness)} accepted={report.AcceptedBatchCount} rejected={report.RejectedBatchCount}\n");
        }

        foreach (var evaluation in result.Iterations)
        {
            trace.Append(CultureInfo.InvariantCulture,
                $"evaluation: batch={evaluation.BatchId} intent={evaluation.Intent} result={evaluation.Result} stoppedAt={evaluation.StoppedAtStep} applied={evaluation.AppliedSteps.Count} reverted={evaluation.RevertedSteps.Count} reasons=[{string.Join(",", evaluation.Rejections.Select(rejection => rejection.Reason))}] score {Format(evaluation.ScoreBefore)} -> {Format(evaluation.ScoreAfter)}\n");
        }

        trace.Append(CultureInfo.InvariantCulture,
            $"result: model={result.LlmModelId} before={Format(result.FitnessBefore)} after={Format(result.FitnessAfter)} error={result.LlmParsingError ?? "<null>"} preview={result.LlmRawResponsePreview ?? "<null>"} aborted={result.AbortedOnUnusableResponses} finalIsOriginal={ReferenceEquals(result.FinalBitmap, result.OriginalBitmap)} invalidRules={result.InvalidPlanningRuleIds.Count} hardCounts={(result.PlanningRuleHardCounts is null ? "<null>" : "set")}\n");
        trace.Append(CultureInfo.InvariantCulture, $"final: {Rows(HarmonyBitmapTextRenderer.Render(result.FinalBitmap))}");
        return trace.ToString();
    }

    private static string Format(double value) => value.ToString(FitnessFormat, CultureInfo.InvariantCulture);

    private static string Escape(string text) => text.Replace("\r", string.Empty).Replace("\n", "|");

    private static string Rows(string planText)
        => string.Join("|", planText.Replace("\r", string.Empty).Split('\n')
            .Where(line => line.Length > 1 && line[0] == 'r' && char.IsDigit(line[1]))
            .Select(line => line.TrimEnd()));

    private static PlanCellSwap[] AllCandidates(PlanProposalRequest request)
        => (request.CandidateMoves ?? [])
            .Select(candidate => new PlanCellSwap(candidate.RowA, candidate.DayA, candidate.RowB, candidate.DayB, ProbeReason))
            .ToArray();

    private static PlanProposalResponse Batches(int index, params PlanCellSwap[] steps)
        => new(
            Batches: [new MutationBatch(BatchId(index), ProbeIntent, index, steps)],
            RawResponse: $"{{\"batches\":[{{\"steps\":{steps.Length}}}]}}",
            ParsingError: null);

    private static PlanProposalResponse Satisfied()
        => new(Batches: [], RawResponse: "{\"batches\":[]}", ParsingError: null, LlmSignaledSatisfied: true);

    private static PlanProposalResponse Unparsable(string raw)
        => new(Batches: [], RawResponse: raw, ParsingError: ParsingFailure);

    private static Guid BatchId(int index) => new($"00000000-0000-0000-0000-{index:D12}");

    private static HolisticHarmonizerEngine CreateEngine(IHarmonizerContextBuilder contextBuilder, IPlanProposalProvider provider)
        => new(contextBuilder, provider, new HolisticHarmonizerModelCapabilityCache(), NullLogger<HolisticHarmonizerEngine>.Instance);

    private static IHarmonizerContextBuilder ContextBuilder(BitmapInput context)
    {
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(context);
        return contextBuilder;
    }

    private static IPlanProposalProvider HealthyProvider()
    {
        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .CapabilityCheckAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: true, LatencyMs: 25, Error: null));
        return provider;
    }

    private static HolisticHarmonizerEngineRequest Request(string? language = "en")
        => new(
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            AgentIds: [AgentGuid],
            AnalyseToken: AnalyseToken,
            LlmModelId: ModelId,
            Language: language,
            ContextDaysBefore: 3,
            ContextDaysAfter: 5);

    private static BitmapInput EmptyContext()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Agent 0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "Agent 1", 100m, new HashSet<CellSymbol>()),
        };
        return new BitmapInput(agents, PeriodFrom, PeriodUntil, []);
    }

    private static BitmapInput AssignmentContext()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Agent 0", 16m, new HashSet<CellSymbol>()),
            new("agent-1", "Agent 1", 16m, new HashSet<CellSymbol>()),
        };
        var assignments = new List<BitmapAssignment>
        {
            Assignment("agent-0", 0, CellSymbol.Early),
            Assignment("agent-0", 2, CellSymbol.Early),
            Assignment("agent-1", 1, CellSymbol.Early),
            Assignment("agent-1", 3, CellSymbol.Early),
        };
        return new BitmapInput(agents, PeriodFrom, PeriodUntil, assignments);
    }

    private static BitmapAssignment Assignment(string agentId, int dayOffset, CellSymbol symbol)
    {
        var date = PeriodFrom.AddDays(dayOffset);
        var startHour = symbol == CellSymbol.Early ? 6 : 14;
        var start = date.ToDateTime(new TimeOnly(startHour, 0), DateTimeKind.Utc);
        var shiftId = symbol == CellSymbol.Early ? EarlyShift : LateShift;
        var workId = new Guid($"dddddddd-0000-0000-{dayOffset:D4}-{(agentId == "agent-0" ? 0 : 1):D12}");
        return new BitmapAssignment(agentId, date, symbol, shiftId, [workId], false, start, start.AddHours(8), 8m);
    }

    private sealed record RunCapture(
        HolisticHarmonizerRunResult Result,
        IReadOnlyList<PlanProposalRequest> Requests,
        IReadOnlyList<HolisticHarmonizerProgress> Progress,
        string Trace);

    private sealed class RecordingProgress : IProgress<HolisticHarmonizerProgress>
    {
        public List<HolisticHarmonizerProgress> Reports { get; } = [];

        public void Report(HolisticHarmonizerProgress value) => Reports.Add(value);
    }

    private static class Expected
    {
        public const string EmptyBitmapPlateau =
            "request 0: model=stub-model iter=0 cap=3 lang=en priorRejections=0 png=True intent=consolidate_block candidates=0 plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _ agents=r00 Agent 0: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]|r01 Agent 1: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]| fragmentation=FRAGMENTATION ANALYSIS: no row has 2+ work blocks separated by free gap days. Reply with {\"batches\":[]}.\n"
            + "request 1: model=stub-model iter=1 cap=2 lang=en priorRejections=1 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "request 2: model=stub-model iter=2 cap=1 lang=en priorRejections=2 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "request 3: model=stub-model iter=3 cap=1 lang=en priorRejections=3 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "request 4: model=stub-model iter=4 cap=1 lang=en priorRejections=4 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "progress: iter=0 max=10 best=0.529182389937 accepted=0 rejected=0\n"
            + "progress: iter=1 max=10 best=0.529182389937 accepted=0 rejected=1\n"
            + "progress: iter=2 max=10 best=0.529182389937 accepted=0 rejected=2\n"
            + "progress: iter=3 max=10 best=0.529182389937 accepted=0 rejected=3\n"
            + "progress: iter=4 max=10 best=0.529182389937 accepted=0 rejected=4\n"
            + "progress: iter=5 max=10 best=0.529182389937 accepted=0 rejected=5\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000000 intent=consolidate_block result=Rejected stoppedAt=0 applied=0 reverted=0 reasons=[NoEffect] score 0.529182389937 -> 0.529182389937\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000001 intent=consolidate_block result=Rejected stoppedAt=0 applied=0 reverted=0 reasons=[NoEffect] score 0.529182389937 -> 0.529182389937\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000002 intent=consolidate_block result=Rejected stoppedAt=0 applied=0 reverted=0 reasons=[NoEffect] score 0.529182389937 -> 0.529182389937\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000003 intent=consolidate_block result=Rejected stoppedAt=0 applied=0 reverted=0 reasons=[NoEffect] score 0.529182389937 -> 0.529182389937\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000004 intent=consolidate_block result=Rejected stoppedAt=0 applied=0 reverted=0 reasons=[NoEffect] score 0.529182389937 -> 0.529182389937\n"
            + "result: model=stub-model before=0.529182389937 after=0.529182389937 error=<null> preview=<null> aborted=False finalIsOriginal=False invalidRules=0 hardCounts=<null>\n"
            + "final: r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _";

        public const string AssignmentAccepted =
            "request 0: model=stub-model iter=0 cap=3 lang=en priorRejections=0 png=True intent=consolidate_block candidates=2 plan=r00 Agent 0     E    _    E    _|r01 Agent 1     _    E    _    E agents=r00 Agent 0: target=16h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]|r01 Agent 1: target=16h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]| fragmentation=FRAGMENTATION ANALYSIS (deterministic, pre-computed by the host):|- r00: 2 work blocks (lengths 1,1), free gap days [1]|- r01: 2 work blocks (lengths 1,1), free gap days [2]|TARGET ROWS for consolidate_block (most fragmented first): r00, r01|Use these rows as the primary candidates: propose batches that fill their gap days by swapping with neighbouring rows that work on those days (DayA == DayB).|\n"
            + "request 1: model=stub-model iter=1 cap=4 lang=en priorRejections=0 png=True plan=r00 Agent 0     E    E    _    _|r01 Agent 1     _    _    E    E\n"
            + "progress: iter=0 max=10 best=0.647691056911 accepted=0 rejected=0\n"
            + "progress: iter=1 max=10 best=0.796358974359 accepted=1 rejected=0\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000000 intent=consolidate_block result=Accepted stoppedAt= applied=2 reverted=0 reasons=[] score 0.647691056911 -> 0.796358974359\n"
            + "result: model=stub-model before=0.647691056911 after=0.796358974359 error=<null> preview=<null> aborted=False finalIsOriginal=False invalidRules=0 hardCounts=<null>\n"
            + "final: r00 Agent 0     E    E    _    _|r01 Agent 1     _    _    E    E";

        public const string AssignmentDropped =
            "request 0: model=stub-model iter=0 cap=3 lang=en priorRejections=0 png=True intent=consolidate_block candidates=2 plan=r00 Agent 0     E    _    E    _|r01 Agent 1     _    E    _    E agents=r00 Agent 0: target=16h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]|r01 Agent 1: target=16h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]| fragmentation=FRAGMENTATION ANALYSIS (deterministic, pre-computed by the host):|- r00: 2 work blocks (lengths 1,1), free gap days [1]|- r01: 2 work blocks (lengths 1,1), free gap days [2]|TARGET ROWS for consolidate_block (most fragmented first): r00, r01|Use these rows as the primary candidates: propose batches that fill their gap days by swapping with neighbouring rows that work on those days (DayA == DayB).|\n"
            + "request 1: model=stub-model iter=1 cap=3 lang=en priorRejections=0 png=True plan=r00 Agent 0     E    _    E    _|r01 Agent 1     _    E    _    E\n"
            + "progress: iter=0 max=10 best=0.647691056911 accepted=0 rejected=0\n"
            + "progress: iter=1 max=10 best=0.647691056911 accepted=0 rejected=0\n"
            + "result: model=stub-model before=0.647691056911 after=0.647691056911 error=<null> preview=<null> aborted=False finalIsOriginal=False invalidRules=0 hardCounts=<null>\n"
            + "final: r00 Agent 0     E    _    E    _|r01 Agent 1     _    E    _    E";

        public const string ParsingErrorRecovered =
            "request 0: model=stub-model iter=0 cap=3 lang=en priorRejections=0 png=True intent=consolidate_block candidates=0 plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _ agents=r00 Agent 0: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]|r01 Agent 1: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]| fragmentation=FRAGMENTATION ANALYSIS: no row has 2+ work blocks separated by free gap days. Reply with {\"batches\":[]}.\n"
            + "request 1: model=stub-model iter=1 cap=3 lang=en priorRejections=0 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "request 2: model=stub-model iter=2 cap=2 lang=en priorRejections=1 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "progress: iter=0 max=10 best=0.529182389937 accepted=0 rejected=0\n"
            + "progress: iter=2 max=10 best=0.529182389937 accepted=0 rejected=1\n"
            + "evaluation: batch=00000000-0000-0000-0000-000000000001 intent=consolidate_block result=Rejected stoppedAt=0 applied=0 reverted=0 reasons=[NoEffect] score 0.529182389937 -> 0.529182389937\n"
            + "result: model=stub-model before=0.529182389937 after=0.529182389937 error=<null> preview=<null> aborted=False finalIsOriginal=False invalidRules=0 hardCounts=<null>\n"
            + "final: r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _";

        public const string ParsingErrorThenSatisfied =
            "request 0: model=stub-model iter=0 cap=3 lang=en priorRejections=0 png=True intent=consolidate_block candidates=0 plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _ agents=r00 Agent 0: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]|r01 Agent 1: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]| fragmentation=FRAGMENTATION ANALYSIS: no row has 2+ work blocks separated by free gap days. Reply with {\"batches\":[]}.\n"
            + "request 1: model=stub-model iter=1 cap=3 lang=en priorRejections=0 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "progress: iter=0 max=10 best=0.529182389937 accepted=0 rejected=0\n"
            + "result: model=stub-model before=0.529182389937 after=0.529182389937 error=balanced-brace scan found no JSON object preview={\"batches\":[]} aborted=False finalIsOriginal=False invalidRules=0 hardCounts=<null>\n"
            + "final: r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _";

        public const string StepLessBatchesAbort =
            "request 0: model=stub-model iter=0 cap=3 lang=en priorRejections=0 png=True intent=consolidate_block candidates=0 plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _ agents=r00 Agent 0: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]|r01 Agent 1: target=100h maxWeekly=0h maxConsec=0 minPause=0h minRestDaysPerWeek=0 preferred=[]| fragmentation=FRAGMENTATION ANALYSIS: no row has 2+ work blocks separated by free gap days. Reply with {\"batches\":[]}.\n"
            + "request 1: model=stub-model iter=1 cap=3 lang=en priorRejections=0 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "request 2: model=stub-model iter=2 cap=3 lang=en priorRejections=0 png=True plan=r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _\n"
            + "progress: iter=0 max=10 best=0.529182389937 accepted=0 rejected=0\n"
            + "progress: iter=1 max=10 best=0.529182389937 accepted=0 rejected=0\n"
            + "progress: iter=2 max=10 best=0.529182389937 accepted=0 rejected=0\n"
            + "result: model=stub-model before=0.529182389937 after=0.529182389937 error=Model produced 3 consecutive responses without a usable batch. preview={\"batches\":[{\"steps\":0}]} aborted=True finalIsOriginal=False invalidRules=0 hardCounts=<null>\n"
            + "final: r00 Agent 0     _    _    _    _|r01 Agent 1     _    _    _    _";
    }
}
