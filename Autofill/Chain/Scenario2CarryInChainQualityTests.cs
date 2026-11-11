// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Chain measurement of scenario 2, the carry-in from February: the previous-month packages reach Wizard 1 as boundary
/// works and Wizard 2/3 as boundary assignments. Wizard 1 must reproduce the run1 of <see cref="Scenario2Tests"/>; the
/// spread is taken over ranks 1 to 4, as A8 of scenario 2 judges it.
/// </summary>
[TestFixture]
[Explicit(AutofillChainCategories.ExplicitReason)]
[Category(AutofillChainCategories.ChainQuality)]
[NonParallelizable]
public sealed class Scenario2CarryInChainQualityTests : AutofillChainQualityTestBase
{
    private const string Name = "Scenario2";
    private const string SuiteFolder = "scenario2";

    protected override string ChainName => Name;

    protected override string SuiteScenarioName => SuiteFolder;

    protected override string SuiteTestName => Name;

    protected override string SuiteFixtureName => nameof(Scenario2Tests);

    protected override int JudgedRankCount => AutofillSpecConstants.TopRankUpperBound;

    protected override AutofillScenarioDefinition BuildDefinition()
    {
        var definition = Scenario2CarryInFixture.Build();
        return Valid(definition, Scenario2CarryInFixture.Validate(definition));
    }
}
