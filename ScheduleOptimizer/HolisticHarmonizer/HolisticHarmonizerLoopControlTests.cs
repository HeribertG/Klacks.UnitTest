// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Deterministic, LLM-free tests for the <see cref="HolisticHarmonizerEngine"/> inner-loop control
/// plumbing: pre-flight health gating, empty-batch termination, and parse-error handling. The two
/// engine constructor seams (<see cref="IHarmonizerContextBuilder"/> and
/// <see cref="IPlanProposalProvider"/>) are substituted with NSubstitute so no real LLM is invoked.
/// Assertions are restricted to structural/control outcomes (which provider methods were called,
/// whether the working bitmap was mutated) and never touch LLM-dependent schedule quality.
/// </summary>
[TestFixture]
public class HolisticHarmonizerLoopControlTests
{
    private const string ModelId = "stub-model";
    private static readonly DateOnly PeriodFrom = new(2026, 1, 5);
    private static readonly DateOnly PeriodUntil = new(2026, 1, 8);

    [Test]
    public async Task ProviderUnhealthy_ShortCircuits_WithoutMutating()
    {
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildContext());

        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .PingAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: false, LatencyMs: 42, Error: "model offline"));

        var engine = new HolisticHarmonizerEngine(
            contextBuilder,
            provider,
            NullLogger<HolisticHarmonizerEngine>.Instance);

        var result = await engine.RunAsync(BuildRequest(), CancellationToken.None);

        result.FinalBitmap.ShouldBeSameAs(result.OriginalBitmap);
        result.FitnessAfter.ShouldBe(result.FitnessBefore);
        result.LlmParsingError.ShouldNotBeNull();
        result.LlmParsingError!.ShouldContain("Pre-flight");
        result.LlmParsingError!.ShouldContain("model offline");
        await provider.DidNotReceive().ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProviderHealthy_EmptyBatches_BreaksLoopWithoutMutating()
    {
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildContext());

        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .PingAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: true, LatencyMs: 10, Error: null));
        provider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse(Batches: [], RawResponse: "{\"batches\":[]}", ParsingError: null));

        var engine = new HolisticHarmonizerEngine(
            contextBuilder,
            provider,
            NullLogger<HolisticHarmonizerEngine>.Instance);

        var result = await engine.RunAsync(BuildRequest(), CancellationToken.None);

        result.Iterations.ShouldBeEmpty();
        result.FitnessAfter.ShouldBe(result.FitnessBefore);
        result.LlmParsingError.ShouldBeNull();
        await provider.Received(1).ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProviderHealthy_ParsingError_SurfacedAndDoesNotMutate()
    {
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildContext());

        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .PingAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: true, LatencyMs: 10, Error: null));
        provider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse(
                Batches: [],
                RawResponse: "not json at all",
                ParsingError: "balanced-brace scan found no JSON object"));

        var engine = new HolisticHarmonizerEngine(
            contextBuilder,
            provider,
            NullLogger<HolisticHarmonizerEngine>.Instance);

        var result = await engine.RunAsync(BuildRequest(), CancellationToken.None);

        result.LlmParsingError.ShouldBe("balanced-brace scan found no JSON object");
        result.FitnessAfter.ShouldBe(result.FitnessBefore);
        result.Iterations.ShouldBeEmpty();
    }

    private static HolisticHarmonizerEngineRequest BuildRequest()
        => new(
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            AgentIds: [Guid.NewGuid()],
            AnalyseToken: null,
            LlmModelId: ModelId,
            Language: "en");

    private static BitmapInput BuildContext()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Agent 0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "Agent 1", 100m, new HashSet<CellSymbol>()),
        };
        return new BitmapInput(agents, PeriodFrom, PeriodUntil, []);
    }
}
