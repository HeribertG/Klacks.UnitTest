// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using Klacks.UnitTest.Autofill.Scenarios.Scenario3;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Names the acceptance stage that starves the shift-kind balancer on the compact M8 plans. The M9
/// measurement proved the short-package gate component innocent (opening it changed no byte), so
/// this census counts, on the scenario-3 final plan with its hoarded nights (MA-1 at 19, MA-5 at
/// 5), how many candidate pairs the balancer generates at all and which check consumes them —
/// slot filter, missing fairness gain, or which gate condition. Diagnosis only, never asserted.
/// </summary>
[TestFixture]
[Category("Autofill")]
[NonParallelizable]
[Explicit("Diagnosis: censuses the balancer's rejection reasons on the scenario-3 final plan")]
public sealed class ShiftKindBalancerRejectionCensusTests
{
    [Test]
    public void BalancerRejectionsOnTheScenarioThreeFinalPlan()
    {
        var definition = Scenario3EligibilityFixture.BuildL1();
        var config = definition.Config with { RandomSeed = 42 };

        var plan = TokenEvolutionLoop.Create().Run(definition.Context, config);
        var evaluator = TokenFitnessEvaluator.Create(definition.Context, config);

        var lines = new List<string>();
        var rebalanced = new ShiftKindBalancer().Apply(plan, definition.Context, evaluator, lines.Add);

        TestContext.Out.WriteLine("=== balancer census, scenario 3 L1, seed 42 ===");
        foreach (var line in lines)
        {
            TestContext.Out.WriteLine(line);
        }

        static string Nights(Klacks.ScheduleOptimizer.Models.CoreScenario s) => string.Join(
            ", ",
            s.Tokens.Where(t => t.ShiftTypeIndex == 2)
                .GroupBy(t => t.AgentId)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Count().ToString(CultureInfo.InvariantCulture)}"));
        TestContext.Out.WriteLine($"nights before: {Nights(plan)}");
        TestContext.Out.WriteLine($"nights after:  {Nights(rebalanced)}");
    }
}
