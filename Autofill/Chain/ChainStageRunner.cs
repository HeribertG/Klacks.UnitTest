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
/// Wizard 2 and Wizard 3 on one Wizard 1 plan, wired like production without a database. Wizard 2 is the conductor-only
/// run of HarmonizerJobRunner with harmony guard and regression gate (<see cref="ChainStage.RunHarmonizer"/>, fixed seed,
/// no wall clock). Wizard 3 is HolisticHarmonizerDeterministicEngine: memoised harmony fitness, untrimmed candidate pool
/// and <see cref="DeterministicSearchOptions.Harmonized"/>, here with both wall-clock budgets lifted so only the
/// deterministic iteration and evaluation caps end the search. Each stage starts from a bitmap input rebuilt from the
/// previous stage's works, as production rebuilds it from the saved scenario.
/// </summary>
public static class ChainStageRunner
{
    /// <summary>Production options of Wizard 3 without a wall clock.</summary>
    public static readonly DeterministicSearchOptions UnboundedHarmonized = DeterministicSearchOptions.Harmonized with
    {
        WallClockBudget = TimeSpan.MaxValue,
        RegionRebuild = RegionRebuildOptions.Default with { WallClockBudgetSeconds = int.MaxValue },
    };

    /// <param name="definition">Scenario whose hard data both stages receive</param>
    /// <param name="wizard1">Works of the Wizard 1 plan</param>
    /// <param name="hints">Softening hints of the Wizard 1 run, read by the Wizard 2 conductor</param>
    public static ChainRunResult Run(
        AutofillScenarioDefinition definition,
        IReadOnlyList<ChainWork> wizard1,
        IReadOnlyList<SofteningHint> hints)
    {
        var harmonizerInput = ChainBitmapInputFactory.Build(definition, wizard1, hints);
        var wizard2 = ChainPlanConverter.WorksOf(ChainStage.RunHarmonizer(harmonizerInput), wizard1);

        var polishInput = ChainBitmapInputFactory.Build(definition, wizard2, null);
        var polished = RowSorter.Sort(BitmapBuilder.Build(polishInput), polishInput.KeepAgentOrder);
        var fitness = new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer());
        var components = HolisticHarmonizerComponents.Build(polishInput, fitness, HolisticHarmonizerComponents.UntrimmedPool);
        var search = new DeterministicHarmonyOptimizer(components, UnboundedHarmonized)
            .Run(polished, null, CancellationToken.None);
        var wizard3 = ChainPlanConverter.WorksOf(polished, wizard2);

        return new ChainRunResult(wizard2, wizard3, search, hints.Count);
    }
}
