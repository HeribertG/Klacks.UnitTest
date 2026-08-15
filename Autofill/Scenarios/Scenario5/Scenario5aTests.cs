// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Run S5a — the main run of specification scenario 5: three identically cut orders each carrying a
/// fourth day shift over March 2026, seventeen employees at 180 guaranteed hours, the full
/// previous-month fixture, five keyword windows and the day-shift preference of the two lowest ranks.
/// This is the run scenario 6 will inherit as its own reference.
/// <para>
/// Marked explicit. One run of this size costs minutes of engine time and the deterministic runner
/// executes it three times — both runs of the determinism proof plus the auction seed plan — so
/// leaving it in the normal suite would turn a short suite into a coffee break for every other session
/// sharing this working tree. Scenario 5 is selected by name when it is meant to run.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 5 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario5")]
public class Scenario5aTests : Scenario5RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheMainScenario()
        => BuildGuardAndRun(Scenario5Fixture.BuildMainRun, Scenario5SpecConstants.RunAArtifactName);
}
