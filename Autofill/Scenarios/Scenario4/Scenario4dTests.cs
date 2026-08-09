// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Run S4d — the same three orders and the same fifteen employees, but without a previous month. It is
/// the diagnostic counterpart of S4a: if S4a fails an assertion and S4d passes the same one, the cause
/// lies in the interplay of the month transition with the parallel orders and not in either alone.
/// <para>
/// The carry-in guard does not run here, because the two tables it checks do not exist in this
/// variant, and the carry-in assertions A10 to A13 and S4-7, S4-8, S4-11 are vacuous for the same
/// reason — a plan without a previous month cannot continue one.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 4 run; minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4dTests : Scenario4RunTestBase
{
    [OneTimeSetUp]
    public void BuildAndRunWithoutCarryIn()
        => BuildGuardAndRun(
            Scenario4CarryInFixture.BuildWithoutCarryIn,
            Scenario4SpecConstants.RunDArtifactName,
            runsGuard: false);
}
