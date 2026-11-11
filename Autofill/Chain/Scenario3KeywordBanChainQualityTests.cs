// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario3;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Chain measurement of scenario 3, main run L1: scenario 2 plus the NACHT-BEF restriction resolved to its ban list of
/// (employee, shift, day) triples. Wizard 1 reads the list as IneligibleAssignments, Wizard 2/3 as
/// BitmapInput.IneligibleAssignments, the field production fills from the eligibility matrix. Wizard 1 must reproduce
/// the L1 run1 of <see cref="Scenario3MainRunTests"/>; the suite's fixture validation and carry-in precheck run first.
/// </summary>
[TestFixture]
[Explicit(AutofillChainCategories.ExplicitReason)]
[Category(AutofillChainCategories.ChainQuality)]
[NonParallelizable]
public sealed class Scenario3KeywordBanChainQualityTests : AutofillChainQualityTestBase
{
    protected override string ChainName => Scenario3SpecValues.L1ArtifactName;

    protected override string SuiteScenarioName => Scenario3SpecValues.ScenarioName;

    protected override string SuiteTestName => Scenario3SpecValues.L1ArtifactName;

    protected override string SuiteFixtureName => nameof(Scenario3MainRunTests);

    protected override int JudgedRankCount => AutofillSpecConstants.TopRankUpperBound;

    protected override string JudgedKindsNote
        => "scenario 3 judges early and late only (MA-3 and MA-4 may never work nights); night is shown, not judged.";

    protected override AutofillScenarioDefinition BuildDefinition()
    {
        var definition = Scenario3EligibilityFixture.BuildL1();
        var problems = new List<string>(Scenario3EligibilityFixture.Validate(definition, Scenario3SpecValues.L1TripleCount));
        problems.AddRange(Scenario3EligibilityFixture.CarryInStockConformityProblems(definition));
        problems.AddRange(Scenario3EligibilityFixture.CarryInContinuationConformityProblems(definition));
        return Valid(definition, problems);
    }
}
