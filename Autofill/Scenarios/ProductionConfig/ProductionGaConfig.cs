// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.ProductionConfig;

/// <summary>
/// The engine configuration production Wizard 1 runs with (Klacks.Api WizardJobRunner: a default
/// <see cref="TokenEvolutionConfig"/> — population 50, 200 generations, early stop after 30 generations
/// without improvement — with no training overrides), made reproducible: the seed is fixed instead of
/// <c>Random.Shared</c>, evaluation is sequential, and the 90-second wall-clock budget is removed so a run
/// ends on generations, never on the machine's speed. Everything else stays the engine default on purpose;
/// <c>AutofillScenarioBuilder.WithGaParameters</c> must NOT be used here, because it also disables the
/// early stop.
/// </summary>
public static class ProductionGaConfig
{
    /// <summary>Production defaults with a fixed seed, sequential evaluation and no wall clock.</summary>
    /// <param name="seed">Seed of the single random source of the run</param>
    public static TokenEvolutionConfig Create(int seed = AutofillSpecConstants.RandomSeed)
        => new()
        {
            RandomSeed = seed,
            EvaluationParallelism = AutofillSpecConstants.EvaluationParallelism,
            MaxRuntime = null,
        };
}
