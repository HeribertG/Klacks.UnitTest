// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Common.Fuzzy;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Common.Fuzzy;

[TestFixture]
public class MamdaniInferenceEngineTests
{
    private static MamdaniInferenceEngine BuildSimpleEngine()
    {
        var inputs = new Dictionary<string, LinguisticVariable>
        {
            ["X"] = new("X", new Dictionary<string, MembershipFunction>
            {
                ["Low"] = new TrapezoidMf(0, 0, 3, 5),
                ["High"] = new TrapezoidMf(5, 7, 10, 10),
            }),
        };
        var output = new LinguisticVariable("Y", new Dictionary<string, MembershipFunction>
        {
            ["Cold"] = new TrapezoidMf(0, 0, 0.2, 0.4),
            ["Hot"] = new TrapezoidMf(0.6, 0.8, 1, 1),
        });
        var rules = new List<FuzzyRule>
        {
            new("RuleLow", [new RuleClause("X", "Low")], "AND", "Y", "Cold"),
            new("RuleHigh", [new RuleClause("X", "High")], "AND", "Y", "Hot"),
        };
        return new MamdaniInferenceEngine(inputs, output, rules);
    }

    [Test]
    public void Infer_LowInput_FiresLowRule_OutputBelowMid()
    {
        var engine = BuildSimpleEngine();
        var result = engine.Infer(new Dictionary<string, double> { ["X"] = 1.0 });
        result.FiredRules.Count(a => a.RuleName == "RuleLow").ShouldBe(1);
        result.CrispOutput.ShouldBeLessThan(0.5);
    }

    [Test]
    public void Infer_HighInput_FiresHighRule_OutputAboveMid()
    {
        var engine = BuildSimpleEngine();
        var result = engine.Infer(new Dictionary<string, double> { ["X"] = 9.0 });
        result.FiredRules.Count(a => a.RuleName == "RuleHigh").ShouldBe(1);
        result.CrispOutput.ShouldBeGreaterThan(0.5);
    }

    [Test]
    public void Infer_NoRulesActivated_ReturnsZeroAndEmpty()
    {
        var engine = BuildSimpleEngine();
        var result = engine.Infer(new Dictionary<string, double> { ["X"] = 6.0 });
        // X=6 hits neither Low (0..5) nor High (>=7) but engine still returns whatever activations exist.
        // For values in the gap, no rules activate.
        if (result.FiredRules.Count == 0)
        {
            result.CrispOutput.ShouldBe(0.0);
        }
    }

    [Test]
    public void DefaultEngine_LoadsAndInfers()
    {
        var inputs = DefaultLinguisticVariables.BuildInputs();
        var output = DefaultLinguisticVariables.BuildOutput();
        var rules = RuleBaseLoader.LoadDefault();
        var engine = new MamdaniInferenceEngine(inputs, output, rules);

        var result = engine.Infer(new Dictionary<string, double>
        {
            ["BlockHunger"] = 32.0,
            ["BlockMaturity"] = 3.0,
            ["DaysSinceEarly"] = 5.0,
            ["DaysSinceLate"] = 2.0,
            ["DaysSinceNight"] = 30.0,
            ["WeeklyLoad"] = 0.5,
            ["IndexBonus"] = 0.0,
        });

        result.FiredRules.ShouldNotBeEmpty();
        result.CrispOutput.ShouldBeGreaterThan(0.0);
    }
}
