// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// Variant 1b of the specification, the calibration run: identical to scenario 1 except that the
/// guaranteed hours drop to 150 h (18.75 shifts, 750 h wanted against 744 h available), so every goal
/// is satisfiable at the same time. That is what makes the variant a calibration: a red assertion
/// here cannot be blamed on the structural undersupply of scenario 1 and therefore points at the
/// algorithm. Consequently A6 demands 95 % fulfilment from all five ranks and A8 covers all five
/// employees, while the remaining assertions are the same ones as in scenario 1.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Scenario1bTests : Scenario1AssertionsBase
{
    private const string ArtifactScenarioName = "scenario1b";

    private const string ArtifactFileName = "Scenario1bCalibration";

    protected override string ScenarioName => ArtifactScenarioName;

    protected override string ArtifactTestName => ArtifactFileName;

    protected override double GuaranteedHours => AutofillSpecConstants.CalibrationGuaranteedHours;

    /// <summary>
    /// A6 of variant 1b measures fulfilment against the guaranteed hours themselves: the 150 h are
    /// reachable, so no separate reference is needed and 95 % of 150 h is the threshold.
    /// </summary>
    protected override double ReferenceHours => AutofillSpecConstants.CalibrationGuaranteedHours;

    /// <summary>All five ranks are covered by A6 and A8 in the calibration variant.</summary>
    protected override int HighestAssertedRank => AutofillSpecConstants.EmployeeCount;
}
