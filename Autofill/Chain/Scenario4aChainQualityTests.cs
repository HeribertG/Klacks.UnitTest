// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario4;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Chain measurement of the main run of scenario 4 (S4a): three identically cut orders over March 2026, fifteen
/// employees at 180 guaranteed hours, 279 shifts and the full previous-month fixture. Wizard 1 must reproduce the run1 of
/// <see cref="Scenario4aTests"/>. The spread is taken over ranks 1 to 12, as the specification of scenario 4 judges it.
/// </summary>
[TestFixture]
[Explicit(AutofillChainCategories.ExplicitReasonLarge)]
[Category(AutofillChainCategories.ChainQualityLarge)]
[NonParallelizable]
public sealed class Scenario4aChainQualityTests : AutofillChainQualityTestBase
{
    protected override string ChainName => Scenario4SpecConstants.RunAArtifactName;

    protected override string SuiteScenarioName => Scenario4SpecConstants.ScenarioName;

    protected override string SuiteTestName => Scenario4SpecConstants.RunAArtifactName;

    protected override string SuiteFixtureName => nameof(Scenario4aTests);

    protected override int JudgedRankCount => Scenario4SpecConstants.TopRankUpperBound;

    protected override AutofillScenarioDefinition BuildDefinition()
    {
        var definition = Scenario4CarryInFixture.BuildMainRun();
        return Valid(definition, Scenario4FixtureGuard.Validate(definition));
    }
}
