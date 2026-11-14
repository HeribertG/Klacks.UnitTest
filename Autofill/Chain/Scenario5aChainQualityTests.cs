// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Chain measurement of the main run of scenario 5 (S5a): three identically cut orders each carrying a day shift over
/// March 2026, seventeen employees at 180 guaranteed hours, 372 shifts, the full previous-month fixture, five keyword
/// windows and the day-shift preference of the two lowest ranks. Wizard 1 must reproduce the run1 of
/// <see cref="Scenario5aTests"/>. The keyword windows and preferences reach Wizard 2/3 as day restrictions, keyword days,
/// preferred symbols and blacklisted shifts (<see cref="ChainKeywordDays"/>, <see cref="ChainShiftPreferences"/>). The
/// day shift counts as an early shift for every shift-kind rule.
/// </summary>
[TestFixture]
[Explicit(AutofillChainCategories.ExplicitReasonLarge)]
[Category(AutofillChainCategories.ChainQualityLarge)]
[NonParallelizable]
public sealed class Scenario5aChainQualityTests : AutofillChainQualityTestBase
{
    protected override string ChainName => Scenario5SpecConstants.RunAArtifactName;

    protected override string SuiteScenarioName => Scenario5SpecConstants.ScenarioName;

    protected override string SuiteTestName => Scenario5SpecConstants.RunAArtifactName;

    protected override string SuiteFixtureName => nameof(Scenario5aTests);

    protected override int JudgedRankCount => Scenario5SpecConstants.NightPoolSize;

    protected override string JudgedKindsNote
        => "scenario 5 judges early and late over all 17 ranks and night over the 15 night-pool ranks (MA-16 and MA-17 are "
        + "blacklisted from nights and prefer the day shift, which counts as early); the table takes ranks 1-15 for all "
        + "three kinds so that the two blacklisted ranks do not inflate the night spread.";

    protected override AutofillScenarioDefinition BuildDefinition()
    {
        var definition = Scenario5Fixture.BuildMainRun();
        return Valid(definition, Scenario5FixtureGuard.Validate(definition));
    }
}
