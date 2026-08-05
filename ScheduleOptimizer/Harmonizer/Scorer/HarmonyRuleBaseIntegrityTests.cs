// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Common.Fuzzy;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Scorer;

/// <summary>
/// Guards two properties of the harmony rule base that silently degraded the conductor:
/// a rule whose antecedents are a strict superset of another rule with the same consequent can
/// never raise the aggregate (min activation, max aggregation) and is therefore dead, and an input
/// point that fires no rule at all must fall back to the neutral score instead of 0.0 - a 0.0 row
/// looks like the worst row in the plan and triggers the emergency unlock.
/// </summary>
[TestFixture]
public sealed class HarmonyRuleBaseIntegrityTests
{
    private const double NeutralNoFireScore = 0.5;
    private const double Tolerance = 1e-9;
    private const double GoodThreshold = 0.75;
    private const double DeviationThreshold = 0.25;

    private static readonly double[] GridSteps = [0.0, 0.25, 0.5, 0.75, 1.0];

    private static readonly string[] InputVariables =
    [
        HarmonyLinguisticVariables.BlockSizeUniformity,
        HarmonyLinguisticVariables.RestUniformity,
        HarmonyLinguisticVariables.BlockHomogeneity,
        HarmonyLinguisticVariables.TransitionCompliance,
        HarmonyLinguisticVariables.ShiftTypeRotation,
        HarmonyLinguisticVariables.PreferredShiftFraction,
        HarmonyLinguisticVariables.TargetHoursDeviation,
    ];

    [Test]
    public void RuleBase_ContainsNoRuleSubsumedByAnother()
    {
        var rules = HarmonyRuleBaseLoader.LoadDefault();

        var dead = new List<string>();
        foreach (var candidate in rules)
        {
            var candidateAntecedents = AntecedentSet(candidate);
            foreach (var other in rules)
            {
                if (ReferenceEquals(candidate, other)
                    || !string.Equals(candidate.ConsequentTerm, other.ConsequentTerm, StringComparison.Ordinal)
                    || !IsConjunctive(candidate)
                    || !IsConjunctive(other))
                {
                    continue;
                }

                if (AntecedentSet(other).IsProperSubsetOf(candidateAntecedents))
                {
                    dead.Add($"{candidate.Name} is subsumed by {other.Name}");
                }
            }
        }

        dead.ShouldBeEmpty();
    }

    [Test]
    public void EveryGridPoint_EitherFiresARuleOrReturnsTheNeutralScore()
    {
        var engine = BuildEngine();

        ForEachGridPoint(inputs =>
        {
            var result = engine.Infer(inputs);
            if (result.FiredRules.Count == 0)
            {
                result.CrispOutput.ShouldBe(NeutralNoFireScore, Tolerance);
            }
        });
    }

    [Test]
    public void GoodRegion_NeverScoresBelowTheNeutralScore()
    {
        var engine = BuildEngine();

        ForEachGridPoint(inputs =>
        {
            if (inputs[HarmonyLinguisticVariables.TargetHoursDeviation] > DeviationThreshold)
            {
                return;
            }

            var allHigh = InputVariables
                .Where(v => !string.Equals(v, HarmonyLinguisticVariables.TargetHoursDeviation, StringComparison.Ordinal))
                .All(v => inputs[v] >= GoodThreshold);

            if (!allHigh)
            {
                return;
            }

            engine.Infer(inputs).CrispOutput.ShouldBeGreaterThanOrEqualTo(NeutralNoFireScore);
        });
    }

    private static MamdaniInferenceEngine BuildEngine() => new(
        HarmonyLinguisticVariables.BuildInputs(),
        HarmonyLinguisticVariables.BuildOutput(),
        HarmonyRuleBaseLoader.LoadDefault(),
        noFireOutput: NeutralNoFireScore);

    private static void ForEachGridPoint(Action<Dictionary<string, double>> assert)
    {
        var indexes = new int[InputVariables.Length];
        while (true)
        {
            var inputs = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < InputVariables.Length; i++)
            {
                inputs[InputVariables[i]] = GridSteps[indexes[i]];
            }

            assert(inputs);

            var position = InputVariables.Length - 1;
            while (position >= 0 && ++indexes[position] == GridSteps.Length)
            {
                indexes[position] = 0;
                position--;
            }

            if (position < 0)
            {
                return;
            }
        }
    }

    private static bool IsConjunctive(FuzzyRule rule)
        => !string.Equals(rule.Operator, "OR", StringComparison.OrdinalIgnoreCase);

    private static HashSet<(string Variable, string Term)> AntecedentSet(FuzzyRule rule)
        => rule.Antecedents.Select(a => (a.Variable, a.Term)).ToHashSet();
}
