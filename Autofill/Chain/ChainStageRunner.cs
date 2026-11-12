// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search.Region;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.ScheduleOptimizer.Pipeline;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Wizard 3 on one Wizard 1 plan as the production autofill chain runs it (Wizard 1 -> Wizard 3, owner decision
/// 2026-11-12), plus Wizard 2 on the same plan, which production runs only as a single stage. Wizard 2 is the
/// conductor-only run of HarmonizerJobRunner with harmony guard and regression gate (<see cref="ChainStage.RunHarmonizer"/>,
/// fixed seed, no wall clock). Wizard 3 is HolisticHarmonizerDeterministicEngine: memoised harmony fitness, untrimmed
/// candidate pool and <see cref="DeterministicSearchOptions.Harmonized"/>, here with both wall-clock budgets lifted so
/// only the deterministic iteration and evaluation caps end the search. Each stage starts from a bitmap input rebuilt
/// from the works it starts from, as production rebuilds it from the saved scenario.
/// </summary>
public static class ChainStageRunner
{
    /// <summary>Stage label of the Wizard 3 run as production runs it: from the Wizard 1 plan, with the stage-3 protection.</summary>
    public const string ProductionLabel = "w3";

    /// <summary>Stage label of the Wizard 3 run of the former chain, started from the Wizard 2 plan.</summary>
    public const string ViaWizard2Label = "w3-via-w2";

    private const string Wizard1StartLabel = "W1";
    private const string Wizard2StartLabel = "W2";
    private const string ProtectionDescription =
        "no hours from a higher to a lower rank, no package cut up, no rest shortened, no package "
        + "beyond 5 days (days before the period included), no more mixed-kind packages";
    private const string ProductionDescription = "as in production, started from the Wizard 1 plan: " + ProtectionDescription;
    private const string ViaWizard2Description =
        "the former chain, started from the Wizard 2 plan: " + ProtectionDescription;

    /// <summary>Production options of Wizard 3 without a wall clock.</summary>
    public static readonly DeterministicSearchOptions UnboundedHarmonized = DeterministicSearchOptions.Harmonized with
    {
        WallClockBudget = TimeSpan.MaxValue,
        RegionRebuild = RegionRebuildOptions.Default with { WallClockBudgetSeconds = int.MaxValue },
    };

    /// <param name="definition">Scenario whose hard data every stage receives</param>
    /// <param name="wizard1">Works of the Wizard 1 plan; both Wizard 2 and the production Wizard 3 start from it</param>
    /// <param name="hints">Softening hints of the Wizard 1 run, read by the Wizard 2 conductor</param>
    public static ChainRunResult Run(
        AutofillScenarioDefinition definition,
        IReadOnlyList<ChainWork> wizard1,
        IReadOnlyList<SofteningHint> hints)
    {
        var harmonizerInput = ChainBitmapInputFactory.Build(definition, wizard1, hints);
        var wizard2 = ChainPlanConverter.WorksOf(ChainStage.RunHarmonizer(harmonizerInput), wizard1);

        var wizard3 = RunWizard3(definition, wizard1, Wizard1StartLabel, ProductionLabel, ProductionDescription);

        return new ChainRunResult(wizard2, wizard3.Works, wizard3.Search, hints.Count);
    }

    /// <summary>
    /// The stage-2 comparison: Wizard 3 as production runs it from the Wizard 1 plan, and the same Wizard 3 started
    /// from the Wizard 2 plan, as the former chain ran it.
    /// </summary>
    /// <param name="definition">Scenario whose hard data the stage receives</param>
    /// <param name="wizard1">Works of the Wizard 1 plan the production run started from</param>
    /// <param name="run">The chain run whose Wizard 3 run is the first entry and whose Wizard 2 plan the second starts from</param>
    public static IReadOnlyList<ChainProtectedRun> RunProtectionComparison(
        AutofillScenarioDefinition definition, IReadOnlyList<ChainWork> wizard1, ChainRunResult run)
        =>
        [
            new ChainProtectedRun(
                ProductionLabel, ProductionDescription, run.Wizard3, run.Wizard3Search, wizard1, Wizard1StartLabel),
            RunWizard3(definition, run.Wizard2, Wizard2StartLabel, ViaWizard2Label, ViaWizard2Description),
        ];

    private static ChainProtectedRun RunWizard3(
        AutofillScenarioDefinition definition,
        IReadOnlyList<ChainWork> start,
        string startLabel,
        string label,
        string description)
    {
        var polishInput = ChainBitmapInputFactory.Build(definition, start, null);
        var polished = RowSorter.Sort(BitmapBuilder.Build(polishInput), polishInput.KeepAgentOrder);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(polishInput, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        var search = new DeterministicHarmonyOptimizer(components, UnboundedHarmonized)
            .Run(polished, null, CancellationToken.None);

        return new ChainProtectedRun(label, description, ChainPlanConverter.WorksOf(polished, start), search, start, startLabel);
    }
}
