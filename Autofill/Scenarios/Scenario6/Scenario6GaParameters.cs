// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Size of the genetic algorithm for the scenario-6 runs: the same unmodified suite size scenario 5
/// used. Stated here rather than read from scenario 5 so that changing it for scenario 6 alone stays a
/// one-line change, and kept EQUAL on purpose — the scenario-6 runs are compared against the finished
/// scenario-5 run, and a different population or generation cap would make every difference between
/// them a difference of the search budget rather than of the holiday.
/// <para>
/// COST, measured. Phase C timed one scenario-5 run at 522 s wall clock, and the deterministic runner
/// executes each variant three times — two runs for the determinism proof plus the auction seed plan.
/// Eight scenario-6 variants are therefore hours of engine time, which is why phase B builds them and
/// phase C runs them.
/// </para>
/// </summary>
public static class Scenario6GaParameters
{
    /// <summary>Scenarios per generation; the unmodified suite size, identical to scenario 5.</summary>
    public const int PopulationSize = AutofillSpecConstants.PopulationSize;

    /// <summary>Generation cap; the unmodified suite size, identical to scenario 5.</summary>
    public const int MaxGenerations = AutofillSpecConstants.MaxGenerations;

    /// <summary>Measured wall clock of one engine run of scenario 2, in milliseconds (probe 2026-08-09).</summary>
    public const long MeasuredReferenceRunMs = Scenario5GaParameters.MeasuredReferenceRunMs;

    /// <summary>Measured wall clock of one engine run of scenario 5, in milliseconds (phase C 2026-08-14).</summary>
    public const long MeasuredScenario5RunMs = 522000;
}
