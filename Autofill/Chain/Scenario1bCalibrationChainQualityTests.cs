// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario1;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Chain measurement of variant 1b, the calibration run (scenario 1 at 150 guaranteed hours); Wizard 1 must reproduce
/// the run1 of <see cref="Scenario1bTests"/>. The spread is taken over all five ranks, as A8 of variant 1b judges it.
/// </summary>
[TestFixture]
[Explicit(AutofillChainCategories.ExplicitReason)]
[Category(AutofillChainCategories.ChainQuality)]
[NonParallelizable]
public sealed class Scenario1bCalibrationChainQualityTests : AutofillChainQualityTestBase
{
    private const string Name = "Scenario1bCalibration";
    private const string SuiteFolder = "scenario1b";

    protected override string ChainName => Name;

    protected override string SuiteScenarioName => SuiteFolder;

    protected override string SuiteTestName => Name;

    protected override string SuiteFixtureName => nameof(Scenario1bTests);

    protected override int JudgedRankCount => AutofillSpecConstants.EmployeeCount;

    protected override AutofillScenarioDefinition BuildDefinition()
        => BuildCleanStart(AutofillSpecConstants.CalibrationGuaranteedHours);
}
