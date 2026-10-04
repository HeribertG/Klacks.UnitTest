// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

/// <summary>
/// Stopwatch breakdown of the stage-4 rule hooks on the large scenario (stage-2 plan, rules of the stage-4 benchmark):
/// fitness per evaluation, hard validation per same-day swap, candidate collection and one full deterministic run,
/// each without and with rules after a warm-up round. Run: dotnet test --filter "FullyQualifiedName~HolisticHarmonizerStage3RuleHotPathTests".
/// </summary>
[TestFixture, Explicit("Hot-path timing of the stage-4 rule hooks - run manually.")]
public class HolisticHarmonizerStage3RuleHotPathTests
{
    private const int Repeats = 20;
    private const int FixedSeed = 42;
    private const int NightMinOverlap = 60;

    [Test]
    public void Large_HotPathTimings_WithAndWithoutRules()
    {
        var scenario = Stage3BenchmarkPipeline.Large;
        var raw = Stage3BenchmarkPipeline.RunStages(scenario, FixedSeed, Stage3TargetMode.Raw);
        var plain = raw.Stage1Input with
        {
            Agents = Stage3BenchmarkPipeline.BuildAgents(raw.Stage1Input.Agents.Select(a => a.Id), Stage3BenchmarkPipeline.TargetFor(scenario, Stage3TargetMode.Prorated)),
        };
        var window = new CoreNightWindow(new TimeOnly(23, 0), new TimeOnly(6, 0));
        IReadOnlyList<PlanRule> rules =
        [
            new MaxConsecutiveOfKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 3),
            new RestAfterKindRule(Guid.NewGuid(), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2),
            new TeamFairnessRule(Guid.NewGuid(), 1, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, true,
                new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }),
        ];
        var ruled = plain with
        {
            Agents = plain.Agents.Select(a => a with { NightWindow = window, WorkloadPercent = 100m }).ToList(),
            Rules = new BitmapPlanningRules(rules, [], NightMinOverlap),
        };
        var stage2 = Stage3BenchmarkPipeline.RunStage2(plain, FixedSeed);

        foreach (var (name, input) in new[] { ("warm-plain", plain), ("warm-rules", ruled), ("plain", plain), ("rules", ruled) })
        {
            var components = HolisticHarmonizerComponents.Build(input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
            var bitmap = BitmapCloner.Clone(stage2);
            components.Fitness.Evaluate(bitmap);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < Repeats * 100; i++)
            {
                components.Fitness.Evaluate(bitmap);
            }

            var fitnessUs = watch.Elapsed.TotalMicroseconds / (Repeats * 100);

            watch.Restart();
            var validations = 0;
            for (var i = 0; i < Repeats; i++)
            {
                for (var day = 0; day < bitmap.DayCount; day++)
                {
                    for (var a = 0; a < bitmap.RowCount; a++)
                    {
                        for (var b = a + 1; b < bitmap.RowCount; b++)
                        {
                            components.Validator.Validate(bitmap, new PlanCellSwap(a, day, b, day, string.Empty));
                            validations++;
                        }
                    }
                }
            }

            var validateUs = watch.Elapsed.TotalMicroseconds / validations;

            watch.Restart();
            var candidates = 0;
            for (var i = 0; i < Repeats; i++)
            {
                foreach (var intent in HolisticIntent.All)
                {
                    candidates += components.Pool.Generate(bitmap, intent).Count;
                }
            }

            var poolMs = watch.Elapsed.TotalMilliseconds / Repeats;

            watch.Restart();
            var result = new DeterministicHarmonyOptimizer(components, DeterministicSearchOptions.Default).Run(BitmapCloner.Clone(stage2), progress: null, CancellationToken.None);
            var runS = watch.Elapsed.TotalSeconds;

            TestContext.Progress.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name}: fitness {fitnessUs:F2} us/eval, validate {validateUs:F3} us/swap ({validations / Repeats} swaps/sweep), pool {poolMs:F2} ms/collect ({candidates / Repeats} candidates), run {runS:F2} s, {result.Evaluations} evals, {result.Evaluations / runS:F0}/s, iterations {result.IterationsRun}, restarts {result.RestartsRun}"));
        }
    }
}
