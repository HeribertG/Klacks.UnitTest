// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class KlacksyModelCheckServiceSortTests
{
    [Test]
    public void Sort_MeasuredGoodModels_RankByCompositeNotByCost()
    {
        var cheapButWeaker = Result("deepseek-v4-flash", qualifies: true, cost: 0.1m, evalScore: 0.80m);
        var pricierButStronger = Result("claude-haiku-45", qualifies: true, cost: 1.0m, evalScore: 0.84m);

        var sorted = KlacksyModelCheckService.Sort([cheapButWeaker, pricierButStronger]);

        sorted[0].ModelId.ShouldBe("claude-haiku-45");
        sorted[1].ModelId.ShouldBe("deepseek-v4-flash");
    }

    [Test]
    public void Sort_MeasuredWeakModel_RanksBelowUnmeasuredModel()
    {
        var measuredWeak = Result("gemini-25-flash", qualifies: true, cost: 0.05m, evalScore: 0.54m);
        var unmeasured = Result("claude-sonnet-46", qualifies: true, cost: 3.0m, evalScore: null);

        var sorted = KlacksyModelCheckService.Sort([measuredWeak, unmeasured]);

        sorted[0].ModelId.ShouldBe("claude-sonnet-46");
        sorted[1].ModelId.ShouldBe("gemini-25-flash");
    }

    [Test]
    public void Sort_MeasuredGoodModel_RanksAboveCheaperUnmeasuredModel()
    {
        var unmeasuredCheap = Result("some-new-model", qualifies: true, cost: 0.0m, evalScore: null);
        var measuredGood = Result("claude-haiku-45", qualifies: true, cost: 1.0m, evalScore: 0.84m);

        var sorted = KlacksyModelCheckService.Sort([unmeasuredCheap, measuredGood]);

        sorted[0].ModelId.ShouldBe("claude-haiku-45");
        sorted[1].ModelId.ShouldBe("some-new-model");
    }

    [Test]
    public void Sort_NotQualifyingModel_RanksLastDespiteHighScore()
    {
        var qualifying = Result("claude-haiku-45", qualifies: true, cost: 1.0m, evalScore: 0.84m);
        var notQualifying = Result("broken-model", qualifies: false, cost: 0.0m, evalScore: 0.95m);

        var sorted = KlacksyModelCheckService.Sort([notQualifying, qualifying]);

        sorted[0].ModelId.ShouldBe("claude-haiku-45");
        sorted[1].ModelId.ShouldBe("broken-model");
    }

    [Test]
    public void Sort_UnmeasuredModels_FallBackToCostHeuristic()
    {
        var cheap = Result("cheap-model", qualifies: true, cost: 0.1m, evalScore: null);
        var expensive = Result("expensive-model", qualifies: true, cost: 5.0m, evalScore: null);

        var sorted = KlacksyModelCheckService.Sort([expensive, cheap]);

        sorted[0].ModelId.ShouldBe("cheap-model");
        sorted[1].ModelId.ShouldBe("expensive-model");
    }

    private static KlacksyModelCheckResult Result(string modelId, bool qualifies, decimal cost, decimal? evalScore) =>
        new(
            ModelId: modelId,
            DisplayName: modelId,
            ProviderId: "test",
            IsHealthy: qualifies,
            SupportsToolCalling: qualifies,
            LatencyMs: 1000,
            ContextWindow: 128000,
            CostPerInputToken: cost,
            CostPerOutputToken: cost,
            Qualifies: qualifies,
            Error: null,
            EvalCompositeScore: evalScore);
}
