// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Size of the genetic algorithm for the scenario-4 runs. It is the unmodified suite size, and it is
/// stated here rather than read straight from the shared constants so that lowering it for scenario 4
/// alone stays a one-line change that cannot touch the other scenarios.
/// <para>
/// MEASURED, not assumed. Phase A' derived a factor of 27 to 81 from the loop nesting of the auction,
/// the top-down handover and the shift kind balancer, and concluded the suite size would be
/// impractical. The probe says otherwise: one engine run of the 279-slot, 15-agent scenario takes
/// about 76 s against about 19 s for the 93-slot, 5-agent scenario 2 on the same machine in the same
/// session — a factor of about 3.9, close to the pure fitness estimate and far below the derived
/// bound. The derivation counted the theoretical worst case of operators that in practice stop early.
/// Since 76 s is inside the 120 s decision rule, the runs keep the full size and stay comparable to
/// scenario 2 without a second reference run.
/// </para>
/// <para>
/// What that costs. One scenario-4 test is three engine runs — both runs of the determinism proof plus
/// the auction seed plan — so roughly four minutes, and the five runs together roughly twenty-five.
/// That is why the run fixtures are explicit: the suite is shared, and nobody who asked for the
/// three-minute suite asked for this.
/// </para>
/// </summary>
public static class Scenario4GaParameters
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
