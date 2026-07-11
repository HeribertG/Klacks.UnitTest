// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class HolisticHarmonizerModelCheckServiceSortTests
{
    [Test]
    public void Sort_MeasuredGoodModels_RankByCompositeNotByLatency()
    {
        var fastButWeaker = Result("gemini-25-flash", healthy: true, latencyMs: 500, evalScore: 0.72m);
        var slowerButStronger = Result("claude-haiku-45", healthy: true, latencyMs: 2000, evalScore: 0.85m);

        var sorted = HolisticHarmonizerModelCheckService.Sort([fastButWeaker, slowerButStronger]);

        sorted[0].ModelId.ShouldBe("claude-haiku-45");
        sorted[1].ModelId.ShouldBe("gemini-25-flash");
    }

    [Test]
    public void Sort_MeasuredWeakModel_RanksBelowUnmeasuredModel()
    {
        var measuredWeak = Result("gemini-25-flash", healthy: true, latencyMs: 500, evalScore: 0.42m);
        var unmeasured = Result("claude-sonnet-46", healthy: true, latencyMs: 4000, evalScore: null);

        var sorted = HolisticHarmonizerModelCheckService.Sort([measuredWeak, unmeasured]);

        sorted[0].ModelId.ShouldBe("claude-sonnet-46");
        sorted[1].ModelId.ShouldBe("gemini-25-flash");
    }

    [Test]
    public void Sort_MeasuredGoodModel_RanksAboveFasterUnmeasuredModel()
    {
        var unmeasuredFast = Result("some-new-model", healthy: true, latencyMs: 100, evalScore: null);
        var measuredGood = Result("claude-haiku-45", healthy: true, latencyMs: 2000, evalScore: 0.85m);

        var sorted = HolisticHarmonizerModelCheckService.Sort([unmeasuredFast, measuredGood]);

        sorted[0].ModelId.ShouldBe("claude-haiku-45");
        sorted[1].ModelId.ShouldBe("some-new-model");
    }

    [Test]
    public void Sort_UnhealthyModel_RanksLastDespiteHighScore()
    {
        var healthy = Result("claude-haiku-45", healthy: true, latencyMs: 2000, evalScore: 0.85m);
        var unhealthyButMeasured = Result("broken-model", healthy: false, latencyMs: 0, evalScore: 0.95m);

        var sorted = HolisticHarmonizerModelCheckService.Sort([unhealthyButMeasured, healthy]);

        sorted[0].ModelId.ShouldBe("claude-haiku-45");
        sorted[1].ModelId.ShouldBe("broken-model");
    }

    [Test]
    public void Sort_UnmeasuredModels_FallBackToLatencyHeuristic()
    {
        var fast = Result("fast-model", healthy: true, latencyMs: 300, evalScore: null);
        var slow = Result("slow-model", healthy: true, latencyMs: 5000, evalScore: null);

        var sorted = HolisticHarmonizerModelCheckService.Sort([slow, fast]);

        sorted[0].ModelId.ShouldBe("fast-model");
        sorted[1].ModelId.ShouldBe("slow-model");
    }

    [Test]
    public void Sort_ThresholdBoundary_ScoreExactlyAtThresholdCountsAsMeasuredGood()
    {
        var atThreshold = Result("at-threshold", healthy: true, latencyMs: 3000, evalScore: 0.7m);
        var unmeasured = Result("unmeasured", healthy: true, latencyMs: 100, evalScore: null);

        var sorted = HolisticHarmonizerModelCheckService.Sort([unmeasured, atThreshold]);

        sorted[0].ModelId.ShouldBe("at-threshold");
        sorted[1].ModelId.ShouldBe("unmeasured");
    }

    private static HolisticHarmonizerModelCheckResult Result(
        string modelId,
        bool healthy,
        long latencyMs,
        decimal? evalScore) =>
        new(
            ModelId: modelId,
            DisplayName: modelId,
            ProviderId: "test",
            IsHealthy: healthy,
            LatencyMs: latencyMs,
            Error: null,
            EvalCompositeScore: evalScore);
}
