// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Evaluates one engine configuration for the configuration search: applies the numeric overrides of a genome file to
/// <see cref="TokenEvolutionConfig"/>, runs scenario 1, 1b and 2 once per requested seed and writes the band values of
/// every run as JSON. Diagnosis only, never asserted; driven by environment variables so the production code carries no
/// search hooks.
/// </summary>
[TestFixture]
[Category("Autofill")]
[NonParallelizable]
[Explicit("Configuration search probe; driven by KLACKS_EVO_GENOME, KLACKS_EVO_SEEDS and KLACKS_EVO_OUT")]
public sealed class AutofillEvolutionProbeTests
{
    private const string GenomeVariable = "KLACKS_EVO_GENOME";
    private const string SeedsVariable = "KLACKS_EVO_SEEDS";
    private const string OutputVariable = "KLACKS_EVO_OUT";
    private const char SeedSeparator = ',';
    private const string ProbeTestName = "evolution-probe";
    private const string SnapshotsVariable = "KLACKS_EVO_SNAPSHOTS";
    private const string ParallelismVariable = "KLACKS_EVO_PARALLEL";
    private const string ProductionBudgetVariable = "KLACKS_EVO_PRODUCTION_BUDGET";
    private const string EnabledValue = "1";
    private const string ScenarioSetVariable = "KLACKS_EVO_SCENARIO_SET";
    private const string StoriesSetValue = "stories";
    private const string SnapshotExtension = ".json";


    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Test]
    public void EvaluateGenome()
    {
        var genomePath = Environment.GetEnvironmentVariable(GenomeVariable);
        var seedsText = Environment.GetEnvironmentVariable(SeedsVariable);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(seedsText) || string.IsNullOrWhiteSpace(outputPath))
        {
            Assert.Ignore($"{SeedsVariable} and {OutputVariable} must be set.");
        }

        var genome = string.IsNullOrWhiteSpace(genomePath)
            ? new Dictionary<string, double>()
            : JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(genomePath)) ?? [];
        var seeds = seedsText!.Split(SeedSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
            .ToList();

        var results = new List<object>();
        foreach (var (name, definition) in Scenarios())
        {
            var configured = definition with { Config = Apply(definition.Config, genome) };
            foreach (var seed in seeds)
            {
                var metrics = SeedBandRunner.Run(configured, seed, name, ProbeTestName);
                results.Add(ResultOf(name, seed, metrics, definition));
            }
        }

        File.WriteAllText(outputPath!, JsonSerializer.Serialize(new { genome, seeds, results }, JsonOptions));
    }

    /// <summary>
    /// Evolves every scenario and seed once without the end passes and stores the plans, so end-pass variants can be
    /// measured on identical inputs in seconds instead of re-running the evolution.
    /// </summary>
    [Test]
    public void CaptureEvolvedPlans()
    {
        var seeds = SeedsOrIgnore();
        var directory = Environment.GetEnvironmentVariable(SnapshotsVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.Ignore($"{SnapshotsVariable} must be set.");
        }

        Directory.CreateDirectory(directory!);
        var runs = Scenarios().SelectMany(s => seeds.Select(seed => (s.Name, s.Definition, Seed: seed))).ToList();
        Parallel.ForEach(runs, new ParallelOptions { MaxDegreeOfParallelism = Parallelism() }, run =>
        {
            var seeded = Seeded(run.Definition, run.Seed);
            var evolved = TokenEvolutionLoop.Create().Evolve(seeded.Context, seeded.Config);
            File.WriteAllText(SnapshotPath(directory!, run.Name, run.Seed), JsonSerializer.Serialize(evolved));
        });
    }

    /// <summary>
    /// Applies the current end passes to the stored evolved plans and writes the band values in the format of
    /// <see cref="EvaluateGenome"/>.
    /// </summary>
    [Test]
    public void EvaluateFinalPasses()
    {
        var seeds = SeedsOrIgnore();
        var directory = Environment.GetEnvironmentVariable(SnapshotsVariable);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(outputPath))
        {
            Assert.Ignore($"{SnapshotsVariable} and {OutputVariable} must be set.");
        }

        var runs = Scenarios().SelectMany(s => seeds.Select(seed => (s.Name, s.Definition, Seed: seed))).ToList();
        var results = new object[runs.Count];
        Parallel.For(0, runs.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism() }, index =>
        {
            var (name, definition, seed) = runs[index];
            var seeded = Seeded(definition, seed);
            var evolved = JsonSerializer.Deserialize<CoreScenario>(File.ReadAllText(SnapshotPath(directory!, name, seed)))
                ?? throw new InvalidOperationException($"Empty snapshot for {name} seed {seed}.");
            var plan = TokenEvolutionLoop.Create().ApplyFinalPasses(evolved, seeded.Context, seeded.Config);
            var metrics = AutofillPlanAnalyzer.Analyze(plan, seeded, name, ProbeTestName, ProbeTestName);
            results[index] = ResultOf(name, seed, metrics, definition);
        });

        File.WriteAllText(outputPath!, JsonSerializer.Serialize(
            new { genome = new Dictionary<string, double>(), seeds, results }, JsonOptions));
    }

    private static List<int> SeedsOrIgnore()
    {
        var seedsText = Environment.GetEnvironmentVariable(SeedsVariable);
        if (string.IsNullOrWhiteSpace(seedsText))
        {
            Assert.Ignore($"{SeedsVariable} must be set.");
        }

        return seedsText!.Split(SeedSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
            .ToList();
    }

    private static int Parallelism()
        => int.TryParse(Environment.GetEnvironmentVariable(ParallelismVariable), out var degree) && degree > 0
            ? degree
            : 1;

    private static AutofillScenarioDefinition Seeded(AutofillScenarioDefinition definition, int seed)
    {
        var config = definition.Config with { RandomSeed = seed };
        if (Environment.GetEnvironmentVariable(ProductionBudgetVariable) == EnabledValue)
        {
            var production = new TokenEvolutionConfig();
            config = config with
            {
                PopulationSize = production.PopulationSize,
                MaxGenerations = production.MaxGenerations,
                EarlyStopNoImprovementGenerations = production.EarlyStopNoImprovementGenerations,
            };
        }

        return definition with { Config = config };
    }

    private static string SnapshotPath(string directory, string scenario, int seed)
        => Path.Combine(directory, $"{scenario}-{seed.ToString(CultureInfo.InvariantCulture)}{SnapshotExtension}");

    private static (string Name, AutofillScenarioDefinition Definition)[] Scenarios()
        => Environment.GetEnvironmentVariable(ScenarioSetVariable) == StoriesSetValue
            ?
            [
                ("scenario3", Scenario3.Scenario3EligibilityFixture.BuildL1()),
                ("scenario4", Scenario4.Scenario4CarryInFixture.BuildMainRun()),
                ("scenario5", Scenario5.Scenario5Fixture.BuildMainRun()),
                ("scenario6", Scenario6.Scenario6Fixture.BuildMainRun()),
            ]
            :
        [
            ("scenario1", CleanStart(AutofillSpecConstants.GuaranteedHours)),
            ("scenario1b", CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours)),
            ("scenario2", Scenario2CarryInFixture.Build()),
        ];

    private static object ResultOf(
        string name, int seed, AutofillMetrics metrics, AutofillScenarioDefinition definition)
    {
        var values = AutofillBandValues.From(metrics, definition.CarryIns.Count > 0);
        TestContext.Out.WriteLine($"{name} seed {seed}: {JsonSerializer.Serialize(values)}");
        return new
        {
            scenario = name,
            seed,
            values,
            unfilled = metrics.Coverage.UnfilledShifts.Count,
            restViolations = metrics.Legality.RestViolations.Count,
        };
    }

    private static TokenEvolutionConfig Apply(TokenEvolutionConfig config, IReadOnlyDictionary<string, double> genome)
    {
        var copy = config with { };
        foreach (var (name, value) in genome)
        {
            var property = typeof(TokenEvolutionConfig).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Unknown configuration property '{name}'.");
            var converted = property.PropertyType == typeof(int)
                ? (object)(int)Math.Round(value)
                : value;
            property.SetValue(copy, converted);
        }

        return copy;
    }

    private static AutofillScenarioDefinition CleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)
            .Build();
}
