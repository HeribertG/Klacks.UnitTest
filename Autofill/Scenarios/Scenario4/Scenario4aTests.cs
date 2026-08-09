// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Run S4a — the main run of specification scenario 4: three identically cut orders over March 2026,
/// fifteen employees at 180 guaranteed hours, and the full previous-month fixture.
/// <para>
/// Marked explicit. One run of this size costs minutes of engine time and the deterministic runner
/// executes it three times (both runs plus the auction seed plan), so leaving it in the normal suite
/// would turn a three-minute suite into a coffee break for every other session sharing this working
/// tree. Scenario 4 is selected by name when it is meant to run.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 4 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4aTests : Scenario4RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunTheMainScenario()
        => BuildGuardAndRun(Scenario4CarryInFixture.BuildMainRun, Scenario4SpecConstants.RunAArtifactName);
}
