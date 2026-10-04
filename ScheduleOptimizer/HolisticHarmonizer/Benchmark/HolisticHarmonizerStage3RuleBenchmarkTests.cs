// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * Opt-in benchmark for constraint-schema stage 4 (planning rules in Wizard 2/3), never part of a normal CI run.
 * Rules: MaxConsecutiveOfKind Night 3 (hard), RestAfterKind Night 2 (hard), TeamFairness NightDays over the plan
 * period, pro rata, max spread 1 (soft). Every agent gets the night window 23:00-06:00 and the night minimum overlap
 * 60 min (owner decision 7), so the autofill night 23:00-07:00 counts as night and the early/late shifts do not.
 * Arms per scenario (stages 1/2 run once, seed 42, prorated targets):
 *   S2              stage-2 plan without rules (= the stage-3 start of PROD_DET and PROD_DET_RULES)
 *   PROD_DET        stage 3 without rules (must equal the stage-3 benchmark numbers)
 *   PROD_DET_RULES  stage 3 with the rules on S2
 *   S2_RULES        stage 2 with the hard rules (Wizard-2 hook)
 *   S2R_DET_RULES   stage 3 with the rules on S2_RULES
 * Rule findings are recounted independently (original segments re-added to a RulePlan, plan-wide Evaluate).
 * Optional extra stage-3 arms with other soft-penalty weights: KLACKS_BENCH_RULE_LAMBDAS=0.25,1 (arm PROD_DET_RULES_L{lambda}).
 * Run: dotnet test --filter "FullyQualifiedName~HolisticHarmonizerStage3RuleBenchmarkTests" (KLACKS_BENCH_RULE_SCENARIO, KLACKS_BENCH_OUT)
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

[TestFixture, Explicit("Stage-4 planning-rule benchmark - run manually; slow.")]
[Category("HarmonizerStage3Benchmark")]
public class HolisticHarmonizerStage3RuleBenchmarkTests
{
    private const int FixedSeed = 42;
    private const int NightMinOverlap = 60;
    private const string ScenarioVariable = "KLACKS_BENCH_RULE_SCENARIO";
    private const string LambdaVariable = "KLACKS_BENCH_RULE_LAMBDAS";
    private const string OutputVariable = "KLACKS_BENCH_OUT";
    private const char ListSeparator = ',';

    private static readonly CoreNightWindow NightWindow = new(new TimeOnly(23, 0), new TimeOnly(6, 0));
    private static readonly Guid MaxNightsRuleId = new("40000000-0000-0000-0000-000000000001");
    private static readonly Guid RestAfterNightsRuleId = new("40000000-0000-0000-0000-000000000002");
    private static readonly Guid NightFairnessRuleId = new("40000000-0000-0000-0000-000000000003");

    private static readonly Stage3Scenario[] Scenarios =
    [
        Stage3BenchmarkPipeline.Month,
        Stage3BenchmarkPipeline.LiveSize,
        Stage3BenchmarkPipeline.Large,
    ];

    private static IReadOnlyList<PlanRule> Rules =>
    [
        new MaxConsecutiveOfKindRule(MaxNightsRuleId, RuleSeverity.Hard, 1, RuleShiftKind.Night, 3),
        new RestAfterKindRule(RestAfterNightsRuleId, RuleSeverity.Hard, 1, RuleShiftKind.Night, 2),
        new TeamFairnessRule(NightFairnessRuleId, 1, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, true,
            new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }),
    ];

    [Test]
    public void FixedSeed_ProductionDeterministic_WithThreePlanningRules()
    {
        var filter = (Environment.GetEnvironmentVariable(ScenarioVariable) ?? string.Empty)
            .Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = new List<string> { Header };
        foreach (var scenario in Scenarios.Where(s => filter.Length == 0 || filter.Contains(s.Name)))
        {
            var raw = Stage3BenchmarkPipeline.RunStages(scenario, FixedSeed, Stage3TargetMode.Raw);
            var plainInput = raw.Stage1Input with
            {
                Agents = Stage3BenchmarkPipeline.BuildAgents(raw.Stage1Input.Agents.Select(a => a.Id), Stage3BenchmarkPipeline.TargetFor(scenario, Stage3TargetMode.Prorated)),
            };
            var ruleInput = plainInput with
            {
                Agents = plainInput.Agents.Select(a => a with { NightWindow = NightWindow, WorkloadPercent = 100m }).ToList(),
                Rules = new BitmapPlanningRules(Rules, [], NightMinOverlap),
            };
            var context = RuleEvaluationContextFactory.FromBitmap(ruleInput, NightMinOverlap, []);
            var oracle = PlanRuleEvaluatorFactory.Create(Rules, context);

            var stage2 = Stage3BenchmarkPipeline.RunStage2(plainInput, FixedSeed);
            rows.Add(Row(scenario, "S2", stage2, null, TimeSpan.Zero, ruleInput, context, oracle));
            rows.Add(RunStage3(scenario, "PROD_DET", plainInput, stage2, ruleInput, context, oracle));
            rows.Add(RunStage3(scenario, "PROD_DET_RULES", ruleInput, stage2, ruleInput, context, oracle));
            foreach (var lambda in Lambdas())
            {
                rows.Add(RunStage3(scenario, string.Create(CultureInfo.InvariantCulture, $"PROD_DET_RULES_L{lambda}"), ruleInput, stage2, ruleInput, context, oracle, lambda));
            }

            var watch = Stopwatch.StartNew();
            var stage2Rules = Stage3BenchmarkPipeline.RunStage2(ruleInput, FixedSeed);
            watch.Stop();
            rows.Add(Row(scenario, "S2_RULES", stage2Rules, null, watch.Elapsed, ruleInput, context, oracle));
            rows.Add(RunStage3(scenario, "S2R_DET_RULES", ruleInput, stage2Rules, ruleInput, context, oracle));
            foreach (var line in rows.Skip(rows.Count - 5 - Lambdas().Count))
            {
                TestContext.Progress.WriteLine(line);
            }
        }

        var report = string.Join(Environment.NewLine, rows);
        TestContext.Progress.WriteLine(report);
        var outDir = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, $"stage4-rules-{DateTime.UtcNow:yyyyMMddHHmmss}.csv"), report, Encoding.UTF8);
        }

        Assert.That(rows.Count, Is.GreaterThan(1));
    }

    private const string Header = "scenario,arm,correctedFitness,searchFitness,targetDevSum,devRms,maxRowDev,domainHardViolations,ruleHardFindings,ruleHardExcess,maxNightsFindings,restAfterFindings,softPenalty,runtimeS,evaluations,evalsPerSecond,applied,stop";

    private static IReadOnlyList<double> Lambdas()
        => (Environment.GetEnvironmentVariable(LambdaVariable) ?? string.Empty)
            .Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToList();

    private static string RunStage3(
        Stage3Scenario scenario, string arm, BitmapInput stage3Input, HarmonyBitmap stage2,
        BitmapInput ruleInput, RuleEvaluationContext context, IPlanRuleEvaluator oracle,
        double softPenaltyWeight = RuleAwareBitmapFitnessEvaluator.DefaultSoftPenaltyWeight)
    {
        var working = BitmapCloner.Clone(stage2);
        var components = HolisticHarmonizerComponents.Build(
            stage3Input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool, softPenaltyWeight);
        var watch = Stopwatch.StartNew();
        var result = new DeterministicHarmonyOptimizer(components, DeterministicSearchOptions.Default).Run(working, progress: null, CancellationToken.None);
        watch.Stop();
        return Row(scenario, arm, working, result, watch.Elapsed, ruleInput, context, oracle);
    }

    private static string Row(
        Stage3Scenario scenario, string arm, HarmonyBitmap bitmap, DeterministicSearchResult? result, TimeSpan runtime,
        BitmapInput ruleInput, RuleEvaluationContext context, IPlanRuleEvaluator oracle)
    {
        var metrics = Stage3BenchmarkMetrics.Measure(bitmap, scenario);
        var evaluation = oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, ruleInput, bitmap));
        var hard = evaluation.Findings.Where(f => f.Severity == RuleSeverity.Hard).ToList();
        var evaluations = result?.Evaluations ?? 0;
        var perSecond = runtime.TotalSeconds > 0 ? evaluations / runtime.TotalSeconds : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{scenario.Name},{arm},{metrics.CorrectedFitness:F5},{result?.FitnessAfter ?? double.NaN:F5},{metrics.TargetDeviationHours:F2},{metrics.TargetDeviationRms:F3},{metrics.MaxRowDeviationHours:F2},{metrics.HardViolations},{hard.Count},{hard.Sum(f => f.Excess)},{hard.Count(f => f.RuleId == MaxNightsRuleId)},{hard.Count(f => f.RuleId == RestAfterNightsRuleId)},{evaluation.SoftPenalty:F3},{runtime.TotalSeconds:F2},{evaluations},{perSecond:F0},{result?.AppliedBatches.Count ?? 0},{result?.StopReason.ToString() ?? "-"}");
    }
}
