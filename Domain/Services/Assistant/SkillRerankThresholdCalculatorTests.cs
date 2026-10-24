// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SkillRerankThresholdCalculator learns the Adaptive rerank threshold from live turns (owner decision 2026-10-24:
/// learning by doing, inside fixed limits). It compares the success of reranked and vector-ordered turns per margin
/// bucket, prefers the lower threshold unless reranking clearly pays for its latency, moves by at most one step per
/// run and never leaves the bounds.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SkillRerankThresholdCalculatorTests
{
    private const double Min = 0.0;
    private const double Max = 0.15;

    [Test]
    public void TooFewTurns_ChangeNothing()
    {
        var outcomes = Generate(150, (_, _) => true);

        SkillRerankThresholdCalculator.Next(outcomes, 0.03, Min, Max).ShouldBeNull();
    }

    [Test]
    public void OneOrderTooRare_ChangesNothing()
    {
        var outcomes = Generate(400, (_, _) => true, rerankedEvery: 50);

        SkillRerankThresholdCalculator.Next(outcomes, 0.03, Min, Max).ShouldBeNull();
    }

    [Test]
    public void RerankingHelpsAtSmallMargins_ThresholdSettlesThere()
    {
        var outcomes = Generate(4000, (margin, reranked) => margin < 0.07 ? reranked : true);

        SkillRerankThresholdCalculator.BestThreshold(outcomes, Min, Max).ShouldBe(0.07, tolerance: 1e-9);
    }

    [Test]
    public void RerankingHelpsAboveTheCurrentThreshold_MovesUpByOneStepOnly()
    {
        var outcomes = Generate(4000, (margin, reranked) => margin < 0.07 ? reranked : true);

        SkillRerankThresholdCalculator.Next(outcomes, 0.03, Min, Max)
            .ShouldBe(0.03 + SkillRerankTuningDefaults.MaxStep, tolerance: 1e-9);
    }

    [Test]
    public void RerankingMakesNoDifference_MovesDownBecauseRerankingCostsLatency()
    {
        var outcomes = Generate(4000, (_, _) => true);

        SkillRerankThresholdCalculator.Next(outcomes, 0.03, Min, Max)
            .ShouldBe(0.03 - SkillRerankTuningDefaults.MaxStep, tolerance: 1e-9);
    }

    [Test]
    public void AlreadyAtTheBest_StaysThere()
    {
        var outcomes = Generate(4000, (margin, reranked) => margin < 0.05 ? reranked : true);

        SkillRerankThresholdCalculator.Next(outcomes, 0.05, Min, Max).ShouldBe(0.05, tolerance: 1e-9);
    }

    [Test]
    public void NeverLeavesTheBounds()
    {
        var helpsEverywhere = Generate(4000, (_, reranked) => reranked);
        var neverHelps = Generate(4000, (_, _) => true);

        SkillRerankThresholdCalculator.Next(helpsEverywhere, Max, Min, Max).ShouldBe(Max, tolerance: 1e-9);
        SkillRerankThresholdCalculator.Next(neverHelps, Min, Min, Max).ShouldBe(Min, tolerance: 1e-9);
    }

    // Deterministic traffic: margins sweep 0..0.12 evenly, every second turn is reranked (both orders in every
    // bucket), and the success of a turn follows the given rule.
    private static List<SkillRerankOutcome> Generate(
        int count, Func<double, bool, bool> success, int rerankedEvery = 2)
    {
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var margin = (i * 7 % 120) / 1000.0 + 0.0005;
                var reranked = i % rerankedEvery == 0;
                return new SkillRerankOutcome(margin, reranked, success(margin, reranked));
            })
            .ToList();
    }
}
