// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Size of the genetic algorithm for the scenario-5 runs. It is the unmodified suite size, stated here
/// rather than read straight from the shared constants so that lowering it for scenario 5 alone stays
/// a one-line change that cannot touch the other scenarios.
/// <para>
/// COST, projected and NOT yet measured. The scenario-4 probe measured about 76 s for 279 slots and 15
/// agents against about 19 s for the 93-slot, 5-agent scenario 2. Scenario 5 is 372 slots and 17
/// agents, so a run is expected to land above scenario 4 — but expected is not measured, and phase C
/// is where the wall clock is taken. The reference below is the scenario-2 figure, so
/// <c>runtime.ratioVsScenario2</c> stays comparable across scenarios 4 and 5; the scenario-4 figure is
/// repeated as the nearer yardstick.
/// </para>
/// </summary>
public static class Scenario5GaParameters
{
    /// <summary>Scenarios per generation; the unmodified suite size.</summary>
    public const int PopulationSize = AutofillSpecConstants.PopulationSize;

    /// <summary>Generation cap; the unmodified suite size.</summary>
    public const int MaxGenerations = AutofillSpecConstants.MaxGenerations;

    /// <summary>Measured wall clock of one engine run of scenario 2, in milliseconds (probe 2026-08-09).</summary>
    public const long MeasuredReferenceRunMs = 19370;

    /// <summary>Measured wall clock of one engine run of scenario 4, in milliseconds (probe 2026-08-09).</summary>
    public const long MeasuredScenario4RunMs = 75588;
}
