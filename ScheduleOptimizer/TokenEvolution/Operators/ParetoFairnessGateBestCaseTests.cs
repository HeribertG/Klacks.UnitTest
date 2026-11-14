// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

/// <summary>
/// The shift-kind balancer skips the full evaluation of a partial swap when the gate refuses its best case. That
/// skip is only result-neutral if a refused best case implies a refused candidate for every value the unmeasured
/// components can take. The values are drawn from small grids around the current plan so equal values, the trade
/// threshold and the mixed-package cap are hit often.
/// </summary>
[TestFixture]
public sealed class ParetoFairnessGateBestCaseTests
{
    private const int Draws = 200_000;
    private const int Seed = 20261113;
    private static readonly double[] Scores = [0.5, 0.505, 0.51, 0.52, 0.6];
    private static readonly int[] Counts = [0, 1, 2, 3];

    [Test]
    public void BestCaseOf_RefusedBestCase_RefusesTheRealCandidateForAnyUnmeasuredValue()
    {
        var random = new Random(Seed);
        var refusedBestCases = 0;
        var refusedWithoutBlockOrder = 0;

        for (var i = 0; i < Draws; i++)
        {
            var origin = Draw(random);
            var current = Draw(random);
            var candidate = Draw(random);

            var bestCase = ParetoFairnessGate.BestCaseOf(
                current, candidate.ShiftKindFairness, candidate.BlockOrder, candidate.MixedPackages);
            if (!ParetoFairnessGate.Accepts(origin, current, bestCase))
            {
                refusedBestCases++;
                ParetoFairnessGate.Accepts(origin, current, candidate).ShouldBeFalse(
                    $"best case refused but candidate accepted: origin={origin} current={current} candidate={candidate}");
            }

            var withoutBlockOrder = ParetoFairnessGate.BestCaseOf(
                current, candidate.ShiftKindFairness, double.PositiveInfinity, candidate.MixedPackages);
            if (!ParetoFairnessGate.Accepts(origin, current, withoutBlockOrder))
            {
                refusedWithoutBlockOrder++;
                ParetoFairnessGate.Accepts(origin, current, bestCase).ShouldBeFalse(
                    $"refused without block order but accepted with it: origin={origin} current={current} candidate={candidate}");
            }
        }

        refusedBestCases.ShouldBeGreaterThan(0);
        refusedWithoutBlockOrder.ShouldBeGreaterThan(0);
    }

    private static ParetoGateSnapshot Draw(Random random) => new(
        Legality: Pick(random, Counts),
        Stage0: Pick(random, Counts),
        Stage1: Pick(random, Scores),
        Stage2: Pick(random, Scores),
        BlockOrder: Pick(random, Scores),
        Blacklist: Pick(random, Scores),
        ShiftKindFairness: Pick(random, Scores),
        OverlongPackages: Pick(random, Counts),
        MixedPackages: Pick(random, Counts),
        ShortPackages: Pick(random, Counts),
        MissingLevels: Pick(random, Counts));

    private static T Pick<T>(Random random, T[] values) => values[random.Next(values.Length)];
}
