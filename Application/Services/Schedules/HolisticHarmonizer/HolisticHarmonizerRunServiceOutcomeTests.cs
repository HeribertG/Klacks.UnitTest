// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// Covers the outcome mapping of a run that the engine aborted on unusable model responses: without an
/// accepted batch it must surface as a failure and must not leave anything in the result cache.
/// </summary>
[TestFixture]
public sealed class HolisticHarmonizerRunServiceOutcomeTests
{
    private const string ModelId = "stub-model";
    private static readonly DateOnly PeriodFrom = new(2026, 1, 5);
    private static readonly DateOnly PeriodUntil = new(2026, 1, 8);

    private static HolisticHarmonizerRunService BuildService(
        IPlanProposalProvider provider,
        HarmonizerResultCache resultCache)
    {
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildContext());

        var engine = new HolisticHarmonizerEngine(
            contextBuilder,
            provider,
            new HolisticHarmonizerModelCapabilityCache(),
            NullLogger<HolisticHarmonizerEngine>.Instance);

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader
            .GetSetting(Arg.Any<string>())
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = "x", Value = ModelId });

        return new HolisticHarmonizerRunService(
            engine,
            resultCache,
            settingsReader,
            Substitute.For<IScheduleSnapshotMarkerService>(),
            NullLogger<HolisticHarmonizerRunService>.Instance);
    }

    private static IPlanProposalProvider BuildHealthyProviderReturningDuds()
    {
        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .CapabilityCheckAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: true, LatencyMs: 25, Error: null));
        provider
            .PingAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: true, LatencyMs: 10, Error: null));
        provider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse(
                Batches: [],
                RawResponse: "{\"batches\":[{\"steps\":[]}]}",
                ParsingError: null));
        return provider;
    }

    [Test]
    public async Task RunAsync_OnlyUnusableResponses_ReportsFailureAndCachesNothing()
    {
        var resultCache = new HarmonizerResultCache();
        var service = BuildService(BuildHealthyProviderReturningDuds(), resultCache);

        var outcome = await service.RunAsync(BuildInput(), CancellationToken.None);

        outcome.IsSuccess.ShouldBeFalse();
        outcome.FailureMessage.ShouldNotBeNullOrWhiteSpace();
        outcome.JobId.ShouldBeNull();
    }

    [Test]
    public async Task RunAsync_UnhealthyPreFlight_ReportsFailure()
    {
        var provider = Substitute.For<IPlanProposalProvider>();
        provider
            .CapabilityCheckAsync(ModelId, Arg.Any<CancellationToken>())
            .Returns(new PlanProposalPingResult(IsHealthy: false, LatencyMs: 42, Error: "model is text-only"));

        var resultCache = new HarmonizerResultCache();
        var service = BuildService(provider, resultCache);

        var outcome = await service.RunAsync(BuildInput(), CancellationToken.None);

        outcome.IsSuccess.ShouldBeFalse();
        outcome.FailureMessage.ShouldNotBeNull();
        outcome.FailureMessage!.ShouldContain("Pre-flight");
        await provider.DidNotReceive().ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>());
    }

    private static HolisticHarmonizerRunInput BuildInput()
        => new(
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            AgentIds: [Guid.NewGuid()],
            AnalyseToken: null,
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
