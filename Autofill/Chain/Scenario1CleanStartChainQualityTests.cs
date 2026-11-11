// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario1;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Chain measurement of scenario 1, the clean start (March 2026, five employees at 180 guaranteed hours, no previous
/// month); Wizard 1 must reproduce the run1 of <see cref="Scenario1Tests"/>. The spread is taken over ranks 1 to 4, as
/// A8 of scenario 1 judges it.
/// </summary>
[TestFixture]
[Explicit(AutofillChainCategories.ExplicitReason)]
[Category(AutofillChainCategories.ChainQuality)]
[NonParallelizable]
public sealed class Scenario1CleanStartChainQualityTests : AutofillChainQualityTestBase
{
    private const string Name = "Scenario1CleanStart";
    private const string SuiteFolder = "scenario1";

    protected override string ChainName => Name;

    protected override string SuiteScenarioName => SuiteFolder;

    protected override string SuiteTestName => Name;

    protected override string SuiteFixtureName => nameof(Scenario1Tests);

    protected override int JudgedRankCount => AutofillSpecConstants.TopRankUpperBound;

    protected override AutofillScenarioDefinition BuildDefinition()
        => BuildCleanStart(AutofillSpecConstants.GuaranteedHours);
}
